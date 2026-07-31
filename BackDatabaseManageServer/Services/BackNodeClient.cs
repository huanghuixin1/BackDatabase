using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BackDatabaseManageServer.Models;

namespace BackDatabaseManageServer.Services;

public sealed class BackNodeClient
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<Guid, AccessToken> _tokens = new();

    public BackNodeClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
    }

    public Task<NodeResponse> GetStatusAsync(BackNode node, CancellationToken cancellationToken = default) =>
        SendAsync(node, HttpMethod.Get, "/api/status", null, cancellationToken);

    public Task<NodeResponse> GetConfigsAsync(BackNode node, CancellationToken cancellationToken = default) =>
        SendAsync(node, HttpMethod.Get, "/api/configs", null, cancellationToken);

    public Task<NodeResponse> GetEnvironmentAsync(BackNode node, CancellationToken cancellationToken = default) =>
        SendAsync(node, HttpMethod.Get, "/api/environment", null, cancellationToken);

    public Task<NodeResponse> SaveConfigAsync(BackNode node, string? fileName, JsonElement body, CancellationToken cancellationToken = default) =>
        SendAsync(node, fileName is null ? HttpMethod.Post : HttpMethod.Put,
            fileName is null ? "/api/configs" : $"/api/configs/{Uri.EscapeDataString(fileName)}", body, cancellationToken);

    public Task<NodeResponse> DeleteConfigAsync(BackNode node, string fileName, CancellationToken cancellationToken = default) =>
        SendAsync(node, HttpMethod.Delete, $"/api/configs/{Uri.EscapeDataString(fileName)}", null, cancellationToken);

    public Task<NodeResponse> SaveEnvironmentAsync(BackNode node, JsonElement body, CancellationToken cancellationToken = default) =>
        SendAsync(node, HttpMethod.Put, "/api/environment", body, cancellationToken);

    public Task<NodeResponse> RestartAsync(BackNode node, CancellationToken cancellationToken = default) =>
        SendAsync(node, HttpMethod.Post, "/api/restart", null, cancellationToken);

    private async Task<NodeResponse> SendAsync(BackNode node, HttpMethod method, string path, JsonElement? body, CancellationToken cancellationToken)
    {
        var response = await SendOnceAsync(node, method, path, body, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _tokens.TryRemove(node.Id, out _);
            response.Dispose();
            response = await SendOnceAsync(node, method, path, body, cancellationToken);
        }

        using (response)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return new NodeResponse((int)response.StatusCode, content, response.Content.Headers.ContentType?.ToString() ?? "application/json");
        }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(BackNode node, HttpMethod method, string path, JsonElement? body, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(node.WebPassword))
            throw new InvalidOperationException("节点未配置 webPassword，远程访问 back 会被拒绝。");

        var token = await GetTokenAsync(node, cancellationToken);
        using var request = new HttpRequestMessage(method, node.BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body.HasValue)
            request.Content = JsonContent.Create(body.Value);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<string> GetTokenAsync(BackNode node, CancellationToken cancellationToken)
    {
        if (_tokens.TryGetValue(node.Id, out var cached) && cached.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            return cached.Token;

        using var response = await _httpClient.PostAsJsonAsync(
            node.BaseUrl + "/api/auth/login",
            new { key = node.WebPassword },
            cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            if (statusCode is 401 or 429)
                throw new NodeAuthenticationException(statusCode, content);
            throw new HttpRequestException($"节点登录暂时失败 ({statusCode}): {content}");
        }

        using var json = JsonDocument.Parse(content);
        var token = json.RootElement.GetProperty("token").GetString();
        if (string.IsNullOrWhiteSpace(token))
            throw new HttpRequestException("节点登录响应缺少 token。");
        var expiresAt = json.RootElement.TryGetProperty("expiresAt", out var expiresElement)
                        && expiresElement.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow.AddHours(8);
        _tokens[node.Id] = new AccessToken(token, expiresAt);
        return token;
    }

    private sealed record AccessToken(string Token, DateTimeOffset ExpiresAtUtc);
}

public sealed record NodeResponse(int StatusCode, string Content, string ContentType);

public sealed class NodeAuthenticationException(int statusCode, string responseBody)
    : HttpRequestException($"节点登录失败 ({statusCode}): {responseBody}")
{
    public int HttpStatusCode { get; } = statusCode;
}
