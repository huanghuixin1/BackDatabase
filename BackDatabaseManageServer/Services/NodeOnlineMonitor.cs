using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BackDatabaseManageServer.Models;
using Microsoft.Extensions.Hosting;

namespace BackDatabaseManageServer.Services;

public sealed record NodeOnlineState(bool Online, DateTimeOffset CheckedAtUtc, string? Error);

public sealed class NodeOnlineStore
{
    private readonly ConcurrentDictionary<Guid, NodeOnlineState> _states = new();

    public NodeOnlineState? Get(Guid nodeId) =>
        _states.TryGetValue(nodeId, out var state) ? state : null;

    public void Set(Guid nodeId, NodeOnlineState state) => _states[nodeId] = state;

    public void Remove(Guid nodeId) => _states.TryRemove(nodeId, out _);

    public void RemoveExcept(IEnumerable<Guid> nodeIds)
    {
        var activeIds = nodeIds.ToHashSet();
        foreach (var nodeId in _states.Keys)
        {
            if (!activeIds.Contains(nodeId))
                _states.TryRemove(nodeId, out _);
        }
    }
}

public sealed class NodeOnlineMonitor(
    NodeStore nodeStore,
    NodeOnlineStore onlineStore,
    BackNodeClient nodeClient) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<Guid, string> _authenticationFailures = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProbeAllAsync(stoppingToken);
            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProbeAllAsync(CancellationToken stoppingToken)
    {
        var nodes = nodeStore.List();
        var enabledNodes = nodes.Where(node => node.Enabled).ToArray();
        onlineStore.RemoveExcept(enabledNodes.Select(node => node.Id));
        await Task.WhenAll(enabledNodes.Select(node => ProbeAsync(node, stoppingToken)));
    }

    private async Task ProbeAsync(BackNode node, CancellationToken stoppingToken)
    {
        var credentialFingerprint = GetCredentialFingerprint(node);
        if (_authenticationFailures.TryGetValue(node.Id, out var failedFingerprint)
            && failedFingerprint == credentialFingerprint)
        {
            return;
        }

        _authenticationFailures.TryRemove(node.Id, out _);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            var response = await nodeClient.GetStatusAsync(node, timeout.Token);
            var online = response.StatusCode is >= 200 and < 300;
            onlineStore.Set(node.Id, new NodeOnlineState(
                online,
                DateTimeOffset.UtcNow,
                online ? null : $"HTTP {response.StatusCode}"));
        }
        catch (NodeAuthenticationException ex)
        {
            _authenticationFailures[node.Id] = credentialFingerprint;
            onlineStore.Set(node.Id, new NodeOnlineState(
                false,
                DateTimeOffset.UtcNow,
                $"认证失败，修改节点口令后重试（HTTP {ex.HttpStatusCode}）"));
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            onlineStore.Set(node.Id, new NodeOnlineState(false, DateTimeOffset.UtcNow, "连接超时"));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
        {
            onlineStore.Set(node.Id, new NodeOnlineState(false, DateTimeOffset.UtcNow, ex.Message));
        }
    }

    private static string GetCredentialFingerprint(BackNode node)
    {
        var bytes = Encoding.UTF8.GetBytes($"{node.BaseUrl}\n{node.WebPassword}");
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
