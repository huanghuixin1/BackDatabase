using System.Net;
using System.Text;
using System.Text.Json;
using BackDatabaseManageServer.Models;
using BackDatabaseManageServer.Services;
using HxSimpleWebAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var nodeStore = new NodeStore(baseDirectory);
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(nodeStore);
builder.Services.AddHttpClient<BackNodeClient>();
builder.Services.AddSingleton<NodeOnlineStore>();
builder.Services.AddSingleton<NodeOnlineMonitor>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<NodeOnlineMonitor>());
var app = builder.Build();

var webDirectory = Path.Combine(baseDirectory, "web");
if (Directory.Exists(webDirectory))
{
    var fileProvider = new PhysicalFileProvider(webDirectory);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fileProvider });
}

var serverPassword = ServerEnvConfigLoader.Load(baseDirectory).WebPassword;
var authRequired = !string.IsNullOrEmpty(serverPassword);
var auth = new WebAdminAuth(serverPassword, logDirectory: baseDirectory);

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/api")
        || path.StartsWithSegments("/api/session")
        || auth.IsAuthPath(path.ToString()))
    {
        await next();
        return;
    }

    if (!authRequired)
    {
        if (IsLoopback(context.Connection.RemoteIpAddress))
        {
            await next();
            return;
        }

        await WriteApiResponseAsync(context, ApiResponse.Error(403, "未配置 BACK_SERVER_WEB_PASSWORD，仅允许本机访问。"));
        return;
    }

    if (auth.Authorize(await CreateAuthRequestAsync(context)))
    {
        await next();
        return;
    }

    await WriteApiResponseAsync(context, ApiResponse.Error(401, "Unauthorized."));
});

app.MapGet("/", () => Results.Text(
    IndexWithVersion(Path.Combine(webDirectory, "index.html"), webDirectory),
    "text/html; charset=utf-8"));

app.MapGet("/api/session", (HttpContext context) => Results.Ok(new
{
    required = authRequired,
    authenticated = !authRequired || auth.Authorize(CreateAuthRequest(context)),
}));

app.MapPost("/api/auth/login", async (HttpContext context) =>
{
    var response = auth.Handle(await CreateAuthRequestAsync(context), context.Request.Path.ToString());
    await WriteApiResponseAsync(context, response);
});

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    var response = auth.Handle(await CreateAuthRequestAsync(context), context.Request.Path.ToString());
    await WriteApiResponseAsync(context, response);
});

app.MapGet("/api/nodes", (NodeOnlineStore onlineStore) =>
    nodeStore.List().Select(node => ToView(node, onlineStore.Get(node.Id))));

app.MapPost("/api/nodes/refresh", async (NodeOnlineMonitor monitor, NodeOnlineStore onlineStore, CancellationToken cancellationToken) =>
{
    await monitor.RefreshAllAsync(cancellationToken);
    return Results.Ok(nodeStore.List().Select(node => ToView(node, onlineStore.Get(node.Id))));
});

app.MapPost("/api/nodes/{id:guid}/refresh", async (Guid id, NodeOnlineMonitor monitor, NodeOnlineStore onlineStore, CancellationToken cancellationToken) =>
{
    if (!await monitor.RefreshNodeAsync(id, cancellationToken))
        return Results.NotFound(new { message = "节点不存在。" });
    var node = nodeStore.Find(id)!;
    return Results.Ok(ToView(node, onlineStore.Get(id)));
});

app.MapPost("/api/nodes", (BackNodeWriteRequest request) =>
{
    try
    {
        return Results.Ok(ToView(nodeStore.Add(request)));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPut("/api/nodes/{id:guid}", (Guid id, BackNodeWriteRequest request) =>
{
    try
    {
        var node = nodeStore.Update(id, request);
        return node is null ? Results.NotFound(new { message = "节点不存在。" }) : Results.Ok(ToView(node));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapDelete("/api/nodes/{id:guid}", (Guid id) =>
    nodeStore.Delete(id) ? Results.Ok(new { message = "节点已删除。" }) : Results.NotFound(new { message = "节点不存在。" }));

app.MapGet("/api/nodes/{id:guid}/status", async (Guid id, BackNodeClient client, CancellationToken cancellationToken) =>
    await ProxyAsync(nodeStore, client, id, client.GetStatusAsync, cancellationToken));

app.MapGet("/api/nodes/{id:guid}/configs", async (Guid id, BackNodeClient client, CancellationToken cancellationToken) =>
    await ProxyAsync(nodeStore, client, id, client.GetConfigsAsync, cancellationToken));

app.MapGet("/api/nodes/{id:guid}/environment", async (Guid id, BackNodeClient client, CancellationToken cancellationToken) =>
    await ProxyAsync(nodeStore, client, id, client.GetEnvironmentAsync, cancellationToken));

app.MapPost("/api/nodes/{id:guid}/restart", async (Guid id, BackNodeClient client, CancellationToken cancellationToken) =>
    await ProxyAsync(nodeStore, client, id, client.RestartAsync, cancellationToken));

app.MapPost("/api/nodes/{id:guid}/configs", async (Guid id, JsonElement body, BackNodeClient client, CancellationToken cancellationToken) =>
    await ProxyAsync(nodeStore, client, id, (node, token) => client.SaveConfigAsync(node, null, body, token), cancellationToken));

app.MapPut("/api/nodes/{id:guid}/configs/{fileName}", async (Guid id, string fileName, JsonElement body, BackNodeClient client, CancellationToken cancellationToken) =>
    await ProxyAsync(nodeStore, client, id, (node, token) => client.SaveConfigAsync(node, fileName, body, token), cancellationToken));

app.MapDelete("/api/nodes/{id:guid}/configs/{fileName}", async (Guid id, string fileName, BackNodeClient client, CancellationToken cancellationToken) =>
    await ProxyAsync(nodeStore, client, id, (node, token) => client.DeleteConfigAsync(node, fileName, token), cancellationToken));

app.MapPut("/api/nodes/{id:guid}/environment", async (Guid id, JsonElement body, BackNodeClient client, CancellationToken cancellationToken) =>
    await ProxyAsync(nodeStore, client, id, (node, token) => client.SaveEnvironmentAsync(node, body, token), cancellationToken));

app.Run("http://0.0.0.0:5090");

static BackNodeView ToView(BackNode node, NodeOnlineState? onlineState = null) => new()
{
    Id = node.Id,
    Name = node.Name,
    BaseUrl = node.BaseUrl,
    Enabled = node.Enabled,
    PasswordConfigured = !string.IsNullOrEmpty(node.WebPassword),
    Online = onlineState?.Online,
    LastCheckedAtUtc = onlineState?.CheckedAtUtc,
    OnlineError = onlineState?.Error,
};

static async Task<IResult> ProxyAsync(
    NodeStore store,
    BackNodeClient client,
    Guid id,
    Func<BackNode, CancellationToken, Task<NodeResponse>> call,
    CancellationToken cancellationToken)
{
    var node = store.Find(id);
    if (node is null)
        return Results.NotFound(new { message = "节点不存在。" });
    if (!node.Enabled)
        return Results.Problem("节点已禁用。", statusCode: 409);

    try
    {
        var response = await call(node, cancellationToken);
        return Results.Content(response.Content, response.ContentType, Encoding.UTF8, response.StatusCode);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("请求节点超时或已取消。", statusCode: 504);
    }
    catch (HttpRequestException ex)
    {
        return Results.Problem($"请求节点失败: {ex.Message}", statusCode: 502);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 400);
    }
}

static bool IsLoopback(IPAddress? address)
{
    if (address is null)
        return false;
    if (address.IsIPv4MappedToIPv6)
        address = address.MapToIPv4();
    return IPAddress.IsLoopback(address);
}

/// <summary>
/// 读取 index.html 并给 app.js / styles.css 的根路径引用追加 <c>?v=&lt;最后修改时间戳&gt;</c>，
/// 文件改动后版本参数自动变化，浏览器不会再用旧缓存——避免每次都要 Ctrl+F5。
/// </summary>
static string IndexWithVersion(string indexHtmlPath, string webDir)
{
    string html;
    try
    {
        html = File.ReadAllText(indexHtmlPath);
    }
    catch
    {
        return "<!doctype html><meta charset=\"utf-8\"><title>BackDatabase Manage</title><p>页面资源加载失败。</p>";
    }

    foreach (var asset in new[] { "app.js", "styles.css" })
    {
        string version;
        try
        {
            version = new FileInfo(Path.Combine(webDir, asset)).LastWriteTimeUtc.Ticks
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            version = "0";
        }

        html = System.Text.RegularExpressions.Regex.Replace(
            html,
            $"((?:href|src)\\s*=\\s*[\"'])\\/{System.Text.RegularExpressions.Regex.Escape(asset)}(\\?v=[^\"']*)?([\"'])",
            $"$1/{asset}?v={version}$3",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    return html;
}

static HttpRequestData CreateAuthRequest(HttpContext context, string body = "", string? method = null)
{
    var headers = context.Request.Headers.ToDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    var target = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return new HttpRequestData((method ?? context.Request.Method).ToUpperInvariant(), target, headers, body, remoteIp);
}

static async Task<HttpRequestData> CreateAuthRequestAsync(HttpContext context)
{
    context.Request.EnableBuffering();
    using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
    var body = await reader.ReadToEndAsync(context.RequestAborted);
    context.Request.Body.Position = 0;
    return CreateAuthRequest(context, body);
}

static async Task WriteApiResponseAsync(HttpContext context, ApiResponse response)
{
    context.Response.StatusCode = response.StatusCode;
    if (response.AllowHeader is not null)
        context.Response.Headers.Allow = response.AllowHeader;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
}
