using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackDatabase.Config;
using BackDatabase.Utils;
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
    /// <param name="webPassword">env.conf 的 webPassword；为空则不启用登录校验</param>
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
        var auth = new WebAuthGuard(webPassword);
        var webDir = Path.Combine(baseDir, "web");

        // 访问口令中间件：只拦 /api，登录接口与静态页面本身不含敏感数据，始终放行。
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path;
            if (!auth.Required
                || !path.StartsWithSegments("/api")
                || path.StartsWithSegments("/api/session")
                || auth.IsValidSession(context.Request.Cookies[WebAuthGuard.CookieName]))
            {
                await next();
                return;
            }

            // 手写 JSON，避免为一条固定提示引入反射序列化
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsync("{\"message\":\"请先输入管理界面访问口令。\"}");
        });

        if (Directory.Exists(webDir))
        {
            var provider = new PhysicalFileProvider(webDir);
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
            app.MapGet("/", () => Results.File(Path.Combine(webDir, "index.html"), "text/html; charset=utf-8"));
        }

        // 会话状态：前端据此决定是否弹出登录框
        app.MapGet("/api/session", (HttpContext context) => Results.Ok(new
        {
            required = auth.Required,
            authenticated = auth.IsValidSession(context.Request.Cookies[WebAuthGuard.CookieName]),
        }));

        app.MapPost("/api/session", (HttpContext context, LoginRequest request) =>
        {
            if (!auth.Required)
                return Results.Ok(new { message = "当前未配置访问口令，可直接使用。" });

            var result = auth.Login(request.Password);
            if (!result.Success)
                return Results.Json(new { message = result.Message }, statusCode: StatusCodes.Status401Unauthorized);

            context.Response.Cookies.Append(WebAuthGuard.CookieName, result.Token!, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                Path = "/",
                MaxAge = auth.Lifetime,
            });
            return Results.Ok(new { message = result.Message });
        });

        app.MapDelete("/api/session", (HttpContext context) =>
        {
            auth.Logout(context.Request.Cookies[WebAuthGuard.CookieName]);
            context.Response.Cookies.Delete(WebAuthGuard.CookieName, new CookieOptions { Path = "/" });
            return Results.Ok(new { message = "已退出登录。" });
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
