using System.Globalization;
using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackDatabase.Config;
using BackDatabase.Utils;
using HxSimpleWebAuth;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;


namespace BackDatabase.Web;

/// <summary>
/// 基于 Kestrel 的配置管理服务。只允许维护固定格式的 conf/env.conf，
/// 不提供任意路径访问或任意命令执行能力。
/// </summary>
public static partial class ConfigWebService
{
    /// <summary>注册静态管理页面与配置 API。</summary>
    /// <param name="app">Kestrel 宿主</param>
    /// <param name="baseDir">程序根目录（env.conf 所在目录）</param>
    /// <param name="configDir">备份配置目录</param>
    /// <param name="webPassword">env.conf 的 webPassword；为空则仅允许本机回环来源访问 API，不启用登录校验</param>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "本项目采用 partial trim 并保留反射 JSON；Web API DTO 均由当前程序集直接引用。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "项目不发布 NativeAOT；Minimal API 路由允许使用运行时代码生成。")]
    public static void Configure(WebApplication app, string baseDir, string configDir, string? webPassword = null)
    {
        var store = new ConfigFileStore(baseDir, configDir);
        var authRequired = !string.IsNullOrEmpty(webPassword);
        var auth = new WebAdminAuth(webPassword ?? string.Empty, logDirectory: baseDir);
        var webDir = Path.Combine(baseDir, "web");

        // HxSimpleWebAuth owns credential, token, IP binding, and lockout validation.
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
                // 未配置 webPassword 时不再全部放行（fail-closed）：仅本机回环来源免认证，
                // 其余来源一律拒绝，避免监听地址被改动或绕过本机边界时管理面裸奔。
                if (IsLoopbackAddress(context.Connection.RemoteIpAddress))
                {
                    await next();
                    return;
                }

                await WriteApiResponseAsync(context, ApiResponse.Error(403, "Forbidden: loopback only when webPassword is not configured."));
                return;
            }

            var request = await CreateAuthRequestAsync(context);
            if (auth.Authorize(request))
            {
                await next();
                return;
            }

            await WriteApiResponseAsync(context, ApiResponse.Error(401, "Unauthorized."));
        });

        if (Directory.Exists(webDir))
        {
            var provider = new PhysicalFileProvider(webDir);
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            app.MapGet("/", () => Results.File(Path.Combine(webDir, "index.html"), "text/html; charset=utf-8"));
        }

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

        // Keep the old session routes as adapters; validation still runs in HxSimpleWebAuth.
        app.MapPost("/api/session", async (HttpContext context, LoginRequest request) =>
        {
            if (!authRequired)
            {
                await WriteApiResponseAsync(context, ApiResponse.Json(200, new { message = "Authentication is disabled." }));
                return;
            }

            var body = JsonSerializer.Serialize(new { key = request.Password });
            var response = auth.Handle(CreateAuthRequest(context, body), "/api/auth/login");
            await WriteApiResponseAsync(context, response);
        });

        app.MapDelete("/api/session", async (HttpContext context) =>
        {
            if (!authRequired)
            {
                await WriteApiResponseAsync(context, ApiResponse.Json(200, new { message = "Authentication is disabled." }));
                return;
            }

            var response = auth.Handle(
                CreateAuthRequest(context, method: "POST"),
                "/api/auth/logout");
            await WriteApiResponseAsync(context, response);
        });

        app.MapGet("/api/configs", () => ApiCall(store.List));

        app.MapPost("/api/configs", (BackupConfigWriteRequest request) =>
            ApiCall(() => store.Save(request, null)));
        app.MapPut("/api/configs/{fileName}", (string fileName, BackupConfigWriteRequest request) =>
            ApiCall(() => store.Save(request, fileName)));
        app.MapDelete("/api/configs/{fileName}", (string fileName) =>
            ApiCall(() => store.Delete(fileName)));

        app.MapGet("/api/environment", () => ApiCall(store.GetEnvironment));
        app.MapPut("/api/environment", (EnvironmentWriteRequest request) =>
            ApiCall(() => store.SaveEnvironment(request)));

        // 查询保存目录所在盘符的硬盘空间：saveDir 是相对程序目录的路径，
        // 盘符由程序所在盘决定；前端在新建/编辑配置时实时展示可用空间。
        app.MapGet("/api/disk", (string? path) => ApiCall(() => store.GetDiskInfo(path)));

        app.MapGet("/api/status", () => Results.Ok(new
        {
            service = "BackDatabase",
            utcNow = DateTime.UtcNow,
            restartRequiredAfterChanges = true,
        }));

        // 重启服务：本机回环 + 已登录后才能到达（中间件已拦 /api）。后台拉起新进程后退出当前进程。
        app.MapPost("/api/restart", () =>
        {
            // 先回复 200，避免客户端在连接被重置时报错；真正退出放在后台线程稍后执行。
            _ = Task.Run(async () =>
            {
                await Task.Delay(300);
                try { AppEntry.RestartSelf(); }
                catch (Exception ex) { Console.WriteLine($"重启失败: {ex.Message}"); }
            });
            return Results.Ok(new { message = "正在重启，请稍候刷新页面。" });
        });

        app.MapFallback(() => Results.NotFound(new { message = "页面资源不存在，请确认发布目录中包含 web 文件夹。" }));
    }

    /// <summary>判断是否本机回环地址；兼容 IPv4（127/8）、IPv6（::1）与 IPv4 映射的 IPv6 地址。</summary>
    private static bool IsLoopbackAddress(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return IPAddress.IsLoopback(address);
    }

    private static HttpRequestData CreateAuthRequest(HttpContext context, string body = "", string? method = null)
    {
        var headers = context.Request.Headers.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var target = $"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return new HttpRequestData(
            (method ?? context.Request.Method).ToUpperInvariant(),
            target,
            headers,
            body,
            remoteIp);
    }

    private static async Task<HttpRequestData> CreateAuthRequestAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(context.RequestAborted);
        context.Request.Body.Position = 0;
        return CreateAuthRequest(context, body);
    }

    private static async Task WriteApiResponseAsync(HttpContext context, ApiResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        if (response.AllowHeader is not null)
            context.Response.Headers.Allow = response.AllowHeader;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = response.Body.Length;
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }
    private static IResult ApiCall<T>(Func<T> action)
    {
        try
        {
            return Results.Ok(action());
        }
        catch (ConfigValidationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
        catch (FileNotFoundException ex)
        {
            return Results.NotFound(new { message = ex.Message });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Web 配置操作失败: {ex.Message}");
            return Results.Problem("配置文件操作失败，请检查目录权限和服务日志。", statusCode: 500);
        }
    }

    private sealed partial class ConfigFileStore
    {
        private readonly string _baseDir;
        private readonly string _configDir;
        private readonly object _writeLock = new();

        public ConfigFileStore(string baseDir, string configDir)
        {
            _baseDir = Path.GetFullPath(baseDir);
            _configDir = Path.GetFullPath(configDir);
            Directory.CreateDirectory(_configDir);
        }

        public IReadOnlyList<BackupConfigView> List()
        {
            var result = new List<BackupConfigView>();
            foreach (var path in Directory.EnumerateFiles(_configDir, "*.conf")
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    result.Add(ToView(ConfigLoader.ParseFile(path)));
                }
                catch (Exception ex)
                {
                    result.Add(new BackupConfigView
                    {
                        FileName = Path.GetFileName(path),
                        Error = ex.Message,
                    });
                }
            }

            return result;
        }

        public object Save(BackupConfigWriteRequest request, string? routeFileName)
        {
            var requestedName = routeFileName ?? request.FileName;
            ValidateRequest(request, routeFileName is null);
            var fileName = NormalizeFileName(requestedName);
            var path = ResolveConfigPath(fileName);

            lock (_writeLock)
            {
                var password = request.Password ?? "";
                if (File.Exists(path) && string.IsNullOrEmpty(request.Password) && !request.ClearPassword)
                    password = ConfigLoader.ParseFile(path).Password;

                var lines = new[]
                {
                    $"dbType={request.DbType.Trim().ToLowerInvariant()}",
                    $"backtime={request.Backtime.Trim()}",
                    $"port={request.Port.Trim()}",
                    $"host={request.Host.Trim()}",
                    $"dbs={request.Databases.Trim()}",
                    $"user={request.User.Trim()}",
                    $"pwd={password}",
                    $"savedir={request.SaveDir.Trim()}",
                    $"maxfiles={request.MaxFiles.ToString(CultureInfo.InvariantCulture)}",
                };

                AtomicWrite(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
                // 用正式解析器做最终校验；失败时会向调用方报告。
                var saved = ConfigLoader.ParseFile(path);
                return new { message = "配置已保存，重启 BackDatabase 后生效。", config = ToView(saved) };
            }
        }

        public object Delete(string routeFileName)
        {
            var fileName = NormalizeFileName(routeFileName);
            var path = ResolveConfigPath(fileName);
            lock (_writeLock)
            {
                if (!File.Exists(path))
                    throw new FileNotFoundException($"配置 {fileName} 不存在。");
                File.Delete(path);
            }

            return new { message = "配置已删除，重启 BackDatabase 后生效。" };
        }

        public EnvironmentView GetEnvironment()
        {
            var path = Path.Combine(_baseDir, "env.conf");
            if (!File.Exists(path))
                return new EnvironmentView();

            try
            {
                var env = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.EnvConfig)
                          ?? new EnvConfig();
                return new EnvironmentView
                {
                    PushAddr = env.PushAddr,
                    PushHwid = env.PushHwid,
                    PushGroup = env.PushGroup,
                    PushKeyConfigured = !string.IsNullOrWhiteSpace(env.PushKey),
                    WebPasswordConfigured = env.IsWebAuthEnabled,
                };
            }
            catch (JsonException ex)
            {
                throw new ConfigValidationException($"env.conf 格式错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 查询指定保存目录所在盘符的可用空间。saveDir 是相对程序目录的路径
        /// （形如 /backup/ 或 backup/）；解析到实际目录后返回所在盘符与空间信息。
        /// path 为空或非法时回落到程序根目录所在盘。
        /// </summary>
        public DiskInfoView GetDiskInfo(string? path)
        {
            string target;
            try
            {
                var relative = (path ?? "").Replace('\\', '/').TrimStart('/');
                target = Path.GetFullPath(Path.Combine(_baseDir, relative));
            }
            catch
            {
                target = _baseDir;
            }

            var rootPath = Path.GetPathRoot(target);
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            {
                rootPath = Path.GetPathRoot(_baseDir);
            }
            if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
            {
                throw new ConfigValidationException("无法确定保存目录所在的盘符。");
            }

            rootPath = Path.GetFullPath(rootPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            // 在所有已挂载盘符中找到包含该根目录的那个（Windows 为盘符，Linux 为挂载点）。
            DriveInfo? drive = null;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (rootPath.StartsWith(d.Name, comparison))
                    {
                        drive = d;
                        break;
                    }
                }
                catch { /* 某些盘符可能不可访问，跳过 */ }
            }

            if (drive is null || !drive.IsReady)
            {
                throw new ConfigValidationException($"盘符 {rootPath} 未就绪，无法读取空间信息。");
            }

            return new DiskInfoView
            {
                Root = rootPath,
                DriveName = drive.Name,
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.AvailableFreeSpace,
            };
        }

        public object SaveEnvironment(EnvironmentWriteRequest request)
        {
            ValidateSingleLine(request.PushAddr, "推送地址", allowEmpty: true);
            ValidateSingleLine(request.PushHwid, "设备 ID", allowEmpty: true);
            ValidateSingleLine(request.PushGroup, "消息分组", allowEmpty: true);
            ValidateSingleLine(request.PushKey, "Push Key", allowEmpty: true);
            ValidateWebPassword(request.WebPassword);

            if (!string.IsNullOrWhiteSpace(request.PushAddr)
                && (!Uri.TryCreate(request.PushAddr, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https" or "ws" or "wss")))
            {
                throw new ConfigValidationException("推送地址必须是有效的 http、https、ws 或 wss 地址。");
            }

            var path = Path.Combine(_baseDir, "env.conf");
            lock (_writeLock)
            {
                // 现有文件里的机密字段（Push Key / 访问口令）在前端不回显，保存时需要原样保留
                EnvConfig? current = null;
                string? readError = null;
                var exists = File.Exists(path);
                if (exists)
                {
                    try
                    {
                        current = JsonSerializer.Deserialize(File.ReadAllText(path), AppJsonContext.Default.EnvConfig);
                    }
                    catch (JsonException ex)
                    {
                        readError = ex.Message;
                    }
                }

                // 传了新值 → 用新值；勾了清除或文件不存在 → 置空；否则保留现有值
                string Keep(string? incoming, bool clear, Func<EnvConfig, string> selector, string field)
                {
                    if (!string.IsNullOrEmpty(incoming))
                        return incoming;
                    if (clear || !exists)
                        return "";
                    if (readError is not null)
                        throw new ConfigValidationException(
                            $"env.conf 格式错误，无法安全保留现有{field}: {readError}。请填写新值或勾选清除。");
                    return current is null ? "" : selector(current);
                }

                var env = new EnvConfig
                {
                    PushAddr = request.PushAddr?.Trim() ?? "",
                    PushKey = Keep(request.PushKey, request.ClearPushKey, x => x.PushKey, "Push Key"),
                    PushHwid = request.PushHwid?.Trim() ?? "",
                    PushGroup = request.PushGroup?.Trim() ?? "",
                    WebPassword = Keep(request.WebPassword, request.ClearWebPassword, x => x.WebPassword, "访问口令"),
                };
                var json = JsonSerializer.Serialize(env, AppJsonContext.Default.EnvConfig);
                AtomicWrite(path, json + Environment.NewLine);
                return new { message = "环境配置已保存，重启 BackDatabase 后生效。" };
            }
        }

        private void ValidateRequest(BackupConfigWriteRequest request, bool validateFileName)
        {
            if (validateFileName)
                NormalizeFileName(request.FileName);
            var dbType = request.DbType.Trim().ToLowerInvariant();
            if (dbType is not ("mysql" or "mariadb" or "pgsql" or "postgres" or "postgresql"))
                throw new ConfigValidationException("数据库类型仅支持 mysql、mariadb、pgsql、postgres、postgresql。");

            ValidateSingleLine(request.Host, "主机");
            ValidateSingleLine(request.Port, "端口");
            ValidateSingleLine(request.User, "用户名");
            ValidateCredential(request.Password, "密码");
            ValidateSingleLine(request.Databases, "数据库列表");
            ValidateSingleLine(request.SaveDir, "保存目录");
            ValidateSingleLine(request.Backtime, "备份计划");

            if (!int.TryParse(request.Port, out var port) || port is < 1 or > 65535)
                throw new ConfigValidationException("端口必须是 1 到 65535 之间的整数。");
            if (request.MaxFiles <= 0)
                throw new ConfigValidationException("最大保留数量必须大于 0。");
            if (request.Databases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length == 0)
                throw new ConfigValidationException("请至少填写一个数据库名称。");

            var validSchedule = double.TryParse(request.Backtime, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)
                                && double.IsFinite(minutes)
                                && minutes is > 0 and <= 525600;
            if (!validSchedule)
            {
                var parts = request.Backtime.Split(':', StringSplitOptions.TrimEntries);
                validSchedule = parts.Length == 2
                                && int.TryParse(parts[0], out var hour) && hour is >= 0 and <= 23
                                && int.TryParse(parts[1], out var minute) && minute is >= 0 and <= 59;
            }
            if (!validSchedule)
                throw new ConfigValidationException("备份计划应为大于 0 的分钟数，或 UTC 时间 HH:mm。");

            var relative = request.SaveDir.Replace('\\', '/').TrimStart('/');
            var savePath = Path.GetFullPath(Path.Combine(_baseDir, relative));
            if (!IsUnderDirectory(savePath, _baseDir))
                throw new ConfigValidationException("保存目录必须位于 BackDatabase 程序目录内。");
        }

        private string ResolveConfigPath(string fileName)
        {
            var path = Path.GetFullPath(Path.Combine(_configDir, fileName));
            if (!IsUnderDirectory(path, _configDir))
                throw new ConfigValidationException("配置文件路径无效。");
            return path;
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        }

        private static string NormalizeFileName(string? value)
        {
            var name = value?.Trim() ?? "";
            if (name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                name = name[..^5];
            if (!SafeFileName().IsMatch(name))
                throw new ConfigValidationException("配置名称只能包含字母、数字、点、下划线和短横线，长度 1-64。 ");
            return name + ".conf";
        }

        private static void ValidateSingleLine(string? value, string field, bool allowEmpty = false)
        {
            if (!allowEmpty && string.IsNullOrWhiteSpace(value))
                throw new ConfigValidationException($"{field}不能为空。");
            if (value?.IndexOfAny(['\r', '\n', '#']) >= 0)
                throw new ConfigValidationException($"{field}不能包含换行或 #。");
        }

        private static void ValidateCredential(string? value, string field)
        {
            ValidateSingleLine(value, field, allowEmpty: true);
            if (!string.IsNullOrEmpty(value) && !string.Equals(value, value.Trim(), StringComparison.Ordinal))
                throw new ConfigValidationException($"{field}不能包含首尾空白字符。");
        }

        /// <summary>
        /// 校验界面访问口令。env.conf 是 JSON，不受 conf 的 # 注释限制，
        /// 因此这里只禁控制字符与首尾空白，并要求最小长度。
        /// </summary>
        private static void ValidateWebPassword(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;
            if (value.Any(char.IsControl))
                throw new ConfigValidationException("访问口令不能包含换行或控制字符。");
            if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
                throw new ConfigValidationException("访问口令不能包含首尾空白字符。");
            if (value.Length < 6)
                throw new ConfigValidationException("访问口令至少 6 位。");
        }

        private static BackupConfigView ToView(BackupConfig config) => new()
        {
            FileName = Path.GetFileName(config.SourceFile),
            DbType = config.DbType,
            Host = config.Host,
            Port = config.Port,
            User = config.User,
            PasswordConfigured = !string.IsNullOrEmpty(config.Password),
            Databases = string.Join(',', config.Databases),
            SaveDir = config.SaveDirRelative,
            MaxFiles = config.MaxFiles,
            Backtime = config.IntervalMinutes?.ToString(CultureInfo.InvariantCulture)
                       ?? $"{config.DailyAtUtc!.Value.Hour:00}:{config.DailyAtUtc.Value.Minute:00}",
        };

        private static void AtomicWrite(string path, string content)
        {
            var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
        private static partial Regex SafeFileName();
    }
}

public sealed class BackupConfigView
{
    public string FileName { get; init; } = "";
    public string DbType { get; init; } = "mysql";
    public string Host { get; init; } = "";
    public string Port { get; init; } = "";
    public string User { get; init; } = "";
    public bool PasswordConfigured { get; init; }
    public string Databases { get; init; } = "";
    public string SaveDir { get; init; } = "/backup/";
    public int MaxFiles { get; init; } = 180;
    public string Backtime { get; init; } = "60";
    public string? Error { get; init; }
}

public sealed class BackupConfigWriteRequest
{
    public string FileName { get; init; } = "";
    public string DbType { get; init; } = "mysql";
    public string Host { get; init; } = "127.0.0.1";
    public string Port { get; init; } = "3306";
    public string User { get; init; } = "root";
    public string? Password { get; init; }
    public bool ClearPassword { get; init; }
    public string Databases { get; init; } = "";
    public string SaveDir { get; init; } = "/backup/";
    public int MaxFiles { get; init; } = 180;
    public string Backtime { get; init; } = "60";
}

public sealed class EnvironmentView
{
    public string PushAddr { get; init; } = "";
    public string PushHwid { get; init; } = "";
    public string PushGroup { get; init; } = "";
    public bool PushKeyConfigured { get; init; }
    public bool WebPasswordConfigured { get; init; }
}

/// <summary>保存目录所在盘符的空间信息（用于配置界面实时展示）。</summary>
public sealed class DiskInfoView
{
    /// <summary>盘符根路径，例如 C:\ 或 /。</summary>
    public string Root { get; init; } = "";
    /// <summary>盘符名称，Windows 形如 C:\，Linux 形如 / 或挂载点。</summary>
    public string DriveName { get; init; } = "";
    /// <summary>盘符总容量（字节）。</summary>
    public long TotalBytes { get; init; }
    /// <summary>盘符当前可用空间（字节）。</summary>
    public long FreeBytes { get; init; }
}

public sealed class EnvironmentWriteRequest
{
    public string? PushAddr { get; init; }
    public string? PushKey { get; init; }
    public bool ClearPushKey { get; init; }
    public string? PushHwid { get; init; }
    public string? PushGroup { get; init; }
    public string? WebPassword { get; init; }
    public bool ClearWebPassword { get; init; }
}

/// <summary>登录请求（口令明文只在本机回环连接上传输一次）。</summary>
public sealed class LoginRequest
{
    public string? Password { get; init; }
}

public sealed class ConfigValidationException(string message) : Exception(message);
