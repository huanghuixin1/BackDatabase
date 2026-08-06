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

// 控制台 iframe 直连 back 节点，跨域填不了它的登录框。这里由 server 拼一个带口令片段的地址：
// URL 片段不会随请求发给 back（也就不会进它的访问日志），back 页面读到后立即清除片段并自动登录。
app.MapPost("/api/nodes/{id:guid}/console-url", (Guid id) =>
{
    var node = nodeStore.Find(id);
    if (node is null)
        return Results.NotFound(new { message = "节点不存在。" });
    if (!node.Enabled)
        return Results.Problem("节点已禁用。", statusCode: 409);

    var src = string.IsNullOrEmpty(node.WebPassword)
        ? $"{node.BaseUrl}/"
        : $"{node.BaseUrl}/#k={Uri.EscapeDataString(node.WebPassword)}";
    return Results.Ok(new { src });
});

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

// 复制任务：把 sourceNodeId 节点上的任务（configs）复制到 targetId 节点。
// 可指定 fileNames 只复制部分任务；overwrite 为 false 时跳过目标节点上已存在的同名任务。
app.MapPost("/api/nodes/{targetId:guid}/configs/copy", async (Guid targetId, CopyConfigsRequest request, BackNodeClient client, CancellationToken cancellationToken) =>
{
    var target = nodeStore.Find(targetId);
    if (target is null)
        return Results.NotFound(new { message = "目标节点不存在。" });
    if (!target.Enabled)
        return Results.Problem("目标节点已禁用。", statusCode: 409);

    var source = nodeStore.Find(request.SourceNodeId);
    if (source is null)
        return Results.NotFound(new { message = "源节点不存在。" });
    if (!source.Enabled)
        return Results.Problem("源节点已禁用。", statusCode: 409);
    if (source.Id == target.Id)
        return Results.BadRequest(new { message = "源节点与目标节点不能相同。" });

    try
    {
        var sourceResponse = await client.GetConfigsAsync(source, cancellationToken);
        if (sourceResponse.StatusCode is < 200 or >= 300)
            return Results.Content(sourceResponse.Content, sourceResponse.ContentType, Encoding.UTF8, sourceResponse.StatusCode);

        var sourceConfigs = CopyHelpers.ParseRemoteConfigs(sourceResponse.Content);
        if (sourceConfigs is null)
            return Results.Problem("源节点返回的任务数据格式无法解析。", statusCode: 502);

        var selectedNames = new HashSet<string>(
            request.FileNames?.Select(name => name.Trim()).Where(name => name.Length > 0) ?? [],
            StringComparer.OrdinalIgnoreCase);

        HashSet<string>? targetNames = null;
        if (!request.Overwrite)
        {
            var targetResponse = await client.GetConfigsAsync(target, cancellationToken);
            if (targetResponse.StatusCode is < 200 or >= 300)
                return Results.Content(targetResponse.Content, targetResponse.ContentType, Encoding.UTF8, targetResponse.StatusCode);
            var targetConfigs = CopyHelpers.ParseRemoteConfigs(targetResponse.Content);
            if (targetConfigs is null)
                return Results.Problem("目标节点返回的任务数据格式无法解析。", statusCode: 502);
            targetNames = new HashSet<string>(targetConfigs.Select(config => config.FileName), StringComparer.OrdinalIgnoreCase);
        }

        var copied = new List<string>();
        var skipped = new List<string>();
        var failed = new List<object>();

        foreach (var config in sourceConfigs)
        {
            var fileName = config.FileName;
            if (string.IsNullOrWhiteSpace(fileName) || config.Error is not null)
                continue;
            if (selectedNames.Count > 0 && !selectedNames.Contains(fileName))
                continue;
            if (targetNames is not null && targetNames.Contains(fileName))
            {
                skipped.Add(fileName);
                continue;
            }

            try
            {
                var result = await client.SaveConfigAsync(target, null, CopyHelpers.BuildConfigPayload(config), cancellationToken);
                if (result.StatusCode is >= 200 and < 300)
                    copied.Add(fileName);
                else
                    failed.Add(new { fileName, message = $"目标节点返回 HTTP {result.StatusCode}。" });
            }
            catch (Exception ex)
            {
                failed.Add(new { fileName, message = ex.Message });
            }
        }

        return Results.Ok(new { copied, skipped, failed });
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
});

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

/// <summary>复制任务相关的反序列化与请求构造工具。</summary>
static partial class CopyHelpers
{
    // back 的 /api/configs 返回驼峰字段（fileName、dbType...），反序列化必须忽略大小写
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static List<RemoteConfigView>? ParseRemoteConfigs(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<List<RemoteConfigView>>(content, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement BuildConfigPayload(RemoteConfigView config) => JsonSerializer.SerializeToElement(new
    {
        fileName = config.FileName,
        dbType = config.DbType,
        host = config.Host,
        port = config.Port,
        user = config.User,
        password = config.Password,
        clearPassword = false,
        databases = config.Databases,
        saveDir = config.SaveDir,
        maxFiles = config.MaxFiles,
        backtime = config.Backtime,
        dbTimes = config.DbTimes,
        dbMaxFiles = config.DbMaxFiles,
    });
}
