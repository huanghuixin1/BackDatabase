using System.Globalization;
using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BackDatabase.Config;
using BackDatabase.Services;
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
    public static void Configure(WebApplication app, string baseDir, string configDir, string? webPassword = null,
        BackupRunner? runner = null, BackupRunRegistry? runRegistry = null,
        BackupScheduleManager? scheduleManager = null)
    {
        var store = new ConfigFileStore(baseDir, configDir, scheduleManager);
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
            // 服务 index.html 时给 app.js / styles.css 注入 ?v=<文件最后修改时间>，
            // 文件改动后版本参数自动变化，浏览器不会再用旧缓存——避免每次都要 Ctrl+F5。
            app.MapGet("/", () => Results.Text(
                IndexWithVersion(Path.Combine(webDir, "index.html"), webDir),
                "text/html; charset=utf-8"));
        }

        app.MapGet("/api/session", (HttpContext context) => Results.Ok(new
        {
            required = authRequired,
            authenticated = !authRequired || auth.Authorize(CreateAuthRequest(context)),
            version = AppInfo.Version,
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

        // 回收站：列出 / 恢复 / 彻底删除
        app.MapGet("/api/trash", () => ApiCall(store.ListTrashed));
        app.MapPost("/api/trash/{fileName}/restore", (string fileName) =>
            ApiCall(() => store.Restore(fileName)));
        app.MapDelete("/api/trash/{fileName}", (string fileName) =>
            ApiCall(() => store.Purge(fileName)));

        // 立即备份：从磁盘读取最新 .conf 后执行一次，不影响调度器。
        // 如指定 ?db=xxx 则只备份该库，否则备份任务的全部库（每个库各起一次运行）。
        app.MapPost("/api/configs/{fileName}/backup", (string fileName, string? db) =>
        {
            if (runner is null || runRegistry is null)
                return Results.Problem("备份执行器未配置。", statusCode: 500);
            try
            {
                var config = scheduleManager is not null
                             && scheduleManager.TryGetSnapshot(fileName, out var current)
                    ? current
                    : store.LoadConfig(fileName);
                if (!string.IsNullOrWhiteSpace(db)
                    && !config.Databases.Contains(db, StringComparer.OrdinalIgnoreCase))
                {
                    return Results.BadRequest(new { message = $"数据库 {db} 不在配置 {fileName} 中。" });
                }
                // 后台执行：RunAsync 内部会自行 BeginRun/FinishRun（取锁、记状态、写日志、释放）。
                // 返回 false 表示该库已有备份在跑——打日志即可（HTTP 已先回复 200）。
                _ = Task.Run(async () =>
                {
                    var name = Path.GetFileName(config.SourceFile);
                    if (!string.IsNullOrWhiteSpace(db))
                    {
                        try
                        {
                            var ran = await runner.RunAsync(config, "manual", db, cancellationToken: default);
                            if (!ran)
                                Console.WriteLine($"[{name}/{db}] 立即备份跳过：已有备份在运行");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"立即备份异常 [{name}/{db}]: {ex.Message}");
                        }
                        return;
                    }

                    // 未指定库：对任务下每个库各触发一次后台备份
                    foreach (var database in config.Databases)
                    {
                        var captured = database;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var ran = await runner.RunAsync(config, "manual", captured, cancellationToken: default);
                                if (!ran)
                                    Console.WriteLine($"[{name}/{captured}] 立即备份跳过：已有备份在运行");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"立即备份异常 [{name}/{captured}]: {ex.Message}");
                            }
                        });
                    }
                });
                return Results.Ok(new { message = "已开始备份，请查看运行状态。" });
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { message = ex.Message });
            }
            catch (ConfigValidationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        // 立即备份全部配置：从磁盘读取每个 .conf，逐个后台执行一次。
        // 已有备份在跑的配置会被 RunAsync 跳过（返回 false），不影响其它配置。
        app.MapPost("/api/configs/backup-all", () =>
        {
            if (runner is null || runRegistry is null)
                return Results.Problem("备份执行器未配置。", statusCode: 500);

            var configs = scheduleManager?.GetSnapshots()
                          ?? store.List()
                              .Where(view => string.IsNullOrEmpty(view.Error))
                              .Select(view => store.LoadConfig(view.FileName))
                              .ToArray();
            if (configs.Count == 0)
                return Results.Ok(new { message = "暂无可备份的配置。", triggered = 0 });

            var triggered = 0;
            foreach (var config in configs)
            {
                triggered++;
                // 对该配置下的每个库各触发一次后台备份
                foreach (var database in config.Databases)
                {
                    var capturedConfig = config;
                    var capturedDb = database;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var ran = await runner.RunAsync(capturedConfig, "manual", capturedDb, cancellationToken: default);
                            if (!ran)
                                Console.WriteLine($"[{Path.GetFileName(capturedConfig.SourceFile)}/{capturedDb}] 全量备份跳过：已有备份在运行");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"全量备份异常 [{Path.GetFileName(capturedConfig.SourceFile)}/{capturedDb}]: {ex.Message}");
                        }
                    });
                }
            }

            return Results.Ok(new { message = $"已触发 {triggered} 个任务的备份，请查看运行状态。", triggered });
        });

        // 查询所有配置最新运行状态
        app.MapGet("/api/runs", () =>
            runRegistry is null
                ? Results.Ok(Array.Empty<BackupRunView>())
                : Results.Ok(runRegistry.GetAllViews()));

        // 查询某个配置下所有库的最新运行状态列表
        app.MapGet("/api/runs/{fileName}", (string fileName) =>
            Results.Ok(runRegistry?.GetViewsForConfig(fileName)
                      ?? (IReadOnlyList<BackupRunView>)Array.Empty<BackupRunView>()));

        // 列出某个配置保存目录下所有 .sql 备份文件，按创建时间从新到旧编号返回。
        // 用于界面「文件」按钮：查看历史备份文件清单。
        app.MapGet("/api/configs/{fileName}/files", (string fileName) =>
            ApiCall(() => store.ListBackupFiles(fileName, baseDir)));

        app.MapGet("/api/environment", () => ApiCall(store.GetEnvironment));
        app.MapPut("/api/environment", (EnvironmentWriteRequest request) =>
            ApiCall(() => store.SaveEnvironment(request)));

        // 查询保存目录所在盘符的硬盘空间：saveDir 是相对程序目录的路径，
        // 盘符由程序所在盘决定；前端在新建/编辑配置时实时展示可用空间。
        app.MapGet("/api/disk", (string? path) => ApiCall(() => store.GetDiskInfo(path)));

        app.MapGet("/api/status", () => Results.Ok(new
        {
            service = "BackDatabase",
            version = AppInfo.Version,
            utcNow = DateTime.UtcNow,
            // 兼容旧客户端：环境配置仍有需要重启的字段，因此通用标记保持 true。
            restartRequiredAfterChanges = true,
            backupConfigHotReload = true,
            environmentRestartRequired = true,
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

    /// <summary>
    /// 读取 index.html 并给 app.js / styles.css 的引用追加 <c>?v=&lt;最后修改时间戳&gt;</c>。
    /// 文件改动后时间戳变化，浏览器视为新 URL 强制重新加载，免去客户端 Ctrl+F5。
    /// 找不到文件时原样返回源码，不影响渲染。仅替换根路径引用（href="/..."、src="/..."），
    /// 避免误伤其它相对引用。
    /// </summary>
    private static string IndexWithVersion(string indexHtmlPath, string webDir)
    {
        string html;
        try
        {
            html = File.ReadAllText(indexHtmlPath);
        }
        catch
        {
            // 读不到就回退到原始文件输出（下方 Results.File 已被替换为本方法，
            // 极端情况下至少要返回点东西，这里给个最小骨架避免白屏）
            return "<!doctype html><meta charset=\"utf-8\"><title>BackDatabase</title><p>页面资源加载失败。</p>";
        }

        foreach (var asset in new[] { "app.js", "styles.css" })
        {
            var assetPath = Path.Combine(webDir, asset);
            string version;
            try
            {
                // 用 UTC Ticks 做版本号：单调递增、文件改了就变、无特殊字符
                version = new FileInfo(assetPath).LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                // 文件不存在或不访问时给固定值，省略 ?v 等价于不缓存破坏，但保持引用合法
                version = "0";
            }

            // 匹配 href="/styles.css" / src="/app.js" 形式的根路径引用（含 defer 等属性也兼容：
            // 我们只替换路径本身，前后属性原样保留）。同时容忍已有的旧 ?v=xxx 被覆盖。
            html = Regex.Replace(
                html,
                $"((?:href|src)\\s*=\\s*[\"'])\\/{Regex.Escape(asset)}(\\?v=[^\"']*)?([\"'])",
                $"$1/{asset}?v={version}$3",
                RegexOptions.IgnoreCase);
        }

        return html;
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
        private readonly string _trashDir;
        private readonly BackupScheduleManager? _scheduleManager;
        private readonly object _writeLock = new();

        public ConfigFileStore(string baseDir, string configDir, BackupScheduleManager? scheduleManager)
        {
            _baseDir = Path.GetFullPath(baseDir);
            _configDir = Path.GetFullPath(configDir);
            _scheduleManager = scheduleManager;
            Directory.CreateDirectory(_configDir);
            // 回收站目录：config/.trash。ConfigLoader.LoadAll 只枚举 *.conf（不含子目录），
            // 回收站里的文件不会被当作活动任务加载，天然隔离。
            _trashDir = Path.Combine(_configDir, ".trash");
            Directory.CreateDirectory(_trashDir);
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
                var previousContent = File.Exists(path) ? File.ReadAllText(path) : null;
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
                // dbtimes 为空时不写该行，保持旧 conf 干净
                var dbTimes = (request.DbTimes ?? "").Trim();
                var dbMaxFiles = (request.DbMaxFiles ?? "").Trim();
                var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;
                if (!string.IsNullOrEmpty(dbTimes))
                    content += $"dbtimes={dbTimes}{Environment.NewLine}";
                if (!string.IsNullOrEmpty(dbMaxFiles))
                    content += $"dbmaxfiles={dbMaxFiles}{Environment.NewLine}";

                try
                {
                    AtomicWrite(path, content);
                    // 用正式解析器做最终校验，并立即替换运行态快照。
                    var saved = ConfigLoader.ParseFile(path);
                    _scheduleManager?.Upsert(saved);
                    return new { message = "配置已保存并立即生效。", config = ToView(saved) };
                }
                catch
                {
                    if (previousContent is null)
                    {
                        if (File.Exists(path))
                            File.Delete(path);
                        _scheduleManager?.Remove(fileName);
                    }
                    else
                    {
                        AtomicWrite(path, previousContent);
                        _scheduleManager?.Upsert(ConfigLoader.ParseFile(path));
                    }
                    throw;
                }
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

                // 移入回收站而非物理删除：同名任务删过两次时追加时间戳后缀避免覆盖
                var trashName = fileName;
                var trashPath = Path.Combine(_trashDir, fileName);
                if (File.Exists(trashPath))
                {
                    var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd__HH.mm.ss");
                    trashName = $"{Path.GetFileNameWithoutExtension(fileName)}__{stamp}.conf";
                    trashPath = Path.Combine(_trashDir, trashName);
                }
                try
                {
                    File.Move(path, trashPath, overwrite: false);
                    // 记录删除时间（移动会保留原 LastWriteTime，故单独写一次）
                    File.SetLastWriteTimeUtc(trashPath, DateTime.UtcNow);
                    _scheduleManager?.Remove(fileName);
                }
                catch
                {
                    if (!File.Exists(path) && File.Exists(trashPath))
                        File.Move(trashPath, path, overwrite: false);
                    throw;
                }
            }

            return new { message = "配置已移入回收站，调度已立即停止。" };
        }

        /// <summary>列出回收站中所有被删除的配置（按删除时间倒序）。</summary>
        public IReadOnlyList<TrashedConfigView> ListTrashed()
        {
            var result = new List<TrashedConfigView>();
            foreach (var path in Directory.EnumerateFiles(_trashDir, "*.conf")
                         .OrderByDescending(p => new FileInfo(p).LastWriteTimeUtc))
            {
                try
                {
                    var cfg = ConfigLoader.ParseFile(path);
                    result.Add(new TrashedConfigView
                    {
                        FileName = Path.GetFileName(path),
                        DbType = cfg.DbType,
                        Host = cfg.Host,
                        Port = cfg.Port,
                        User = cfg.User,
                        Databases = string.Join(',', cfg.Databases),
                        DeletedAtUtc = new FileInfo(path).LastWriteTimeUtc,
                    });
                }
                catch (Exception ex)
                {
                    result.Add(new TrashedConfigView
                    {
                        FileName = Path.GetFileName(path),
                        Error = ex.Message,
                        DeletedAtUtc = new FileInfo(path).LastWriteTimeUtc,
                    });
                }
            }
            return result;
        }

        /// <summary>从回收站恢复一个配置到 config 目录；同名活动配置已存在时报错。</summary>
        public object Restore(string routeFileName)
        {
            var fileName = NormalizeFileName(routeFileName);
            var trashPath = Path.Combine(_trashDir, fileName);
            var activePath = ResolveConfigPath(fileName);
            lock (_writeLock)
            {
                if (!File.Exists(trashPath))
                    throw new FileNotFoundException($"回收站中没有配置 {fileName}。");
                if (File.Exists(activePath))
                    throw new ConfigValidationException($"已存在同名活动配置 {fileName}，请先重命名或删除现有配置。");
                try
                {
                    File.Move(trashPath, activePath, overwrite: false);
                    var restored = ConfigLoader.ParseFile(activePath);
                    _scheduleManager?.Upsert(restored);
                }
                catch
                {
                    if (!File.Exists(trashPath) && File.Exists(activePath))
                        File.Move(activePath, trashPath, overwrite: false);
                    throw;
                }
            }
            return new { message = "配置已从回收站恢复并立即生效。" };
        }

        /// <summary>从回收站彻底删除一个配置（不可恢复）。</summary>
        public object Purge(string routeFileName)
        {
            var fileName = NormalizeFileName(routeFileName);
            var trashPath = Path.Combine(_trashDir, fileName);
            lock (_writeLock)
            {
                if (!File.Exists(trashPath))
                    throw new FileNotFoundException($"回收站中没有配置 {fileName}。");
                File.Delete(trashPath);
            }
            return new { message = "配置已彻底删除。" };
        }

        /// <summary>
        /// 从磁盘读取最新 .conf 为 <see cref="BackupConfig"/>。
        /// 用于立即备份等需要「读最新配置」的场景（保存后未重启也能跑到最新）。
        /// </summary>
        public BackupConfig LoadConfig(string routeFileName)
        {
            var fileName = NormalizeFileName(routeFileName);
            var path = ResolveConfigPath(fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"配置 {fileName} 不存在。");
            return ConfigLoader.ParseFile(path);
        }

        /// <summary>
        /// 列出某个配置保存目录下的所有 .sql 备份文件，按创建时间从新到旧编号。
        /// 文件名包含的 UTC 时间戳不可靠时，退回 LastWriteTimeUtc 排序。
        /// 目录不存在视为空列表（不报错），方便界面在首次备份前展示空状态。
        /// </summary>
        public IReadOnlyList<BackupFileView> ListBackupFiles(string routeFileName, string baseDir)
        {
            var config = LoadConfig(routeFileName);
            var saveDir = config.ResolveSaveDir(baseDir);

            var result = new List<BackupFileView>();
            if (!Directory.Exists(saveDir))
                return result;

            // 文件名格式：{db}_{UTC时间}.sql。提取前导 db 作为分组键。
            var knownDbs = new HashSet<string>(config.Databases, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(saveDir, "*.sql")
                         .Select(p => new FileInfo(p)))
            {
                var name = file.Name;
                var db = ExtractDatabase(name);
                // 不在配置库里（无法识别）的文件归到一个独立组
                if (!string.IsNullOrEmpty(db) && !knownDbs.Contains(db))
                    db = "_other";
                result.Add(new BackupFileView
                {
                    Name = name,
                    Database = db,
                    SizeBytes = file.Length,
                    CreatedAtUtc = file.LastWriteTimeUtc,
                });
            }

            // 新文件在前：按 CreatedAtUtc 倒序；时间相同再按文件名倒序，保证稳定
            return result
                .OrderByDescending(f => f.CreatedAtUtc)
                .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select((f, i) =>
                {
                    f.Index = i + 1; // 序号从 1 开始
                    return f;
                })
                .ToList();
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
            ValidateSingleLine(request.DbTimes, "每库备份计划", allowEmpty: true);
            ValidateSingleLine(request.DbMaxFiles, "每库最大保留数量", allowEmpty: true);

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

            // 校验 dbtimes：每个 entry 形如「库名:计划」，库名必须在 dbs 列表里，计划须合法
            var dbSet = new HashSet<string>(
                request.Databases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            var dbTimesRaw = (request.DbTimes ?? "").Trim();
            if (!string.IsNullOrEmpty(dbTimesRaw))
            {
                foreach (var entry in dbTimesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var colon = entry.IndexOf(':');
                    if (colon <= 0 || colon >= entry.Length - 1)
                        throw new ConfigValidationException($"每库备份计划格式错误: {entry}（应为 库名:分钟 或 库名:HH:mm）");
                    var db = entry[..colon].Trim();
                    var t = entry[(colon + 1)..].Trim();
                    if (!dbSet.Contains(db))
                        throw new ConfigValidationException($"每库备份计划里的 {db} 未在数据库列表中。");
                    if (ConfigLoader.ParseSingleSchedule(t).IsInvalid)
                        throw new ConfigValidationException($"每库备份计划 {db} 的时间无效: {t}（应为大于 0 的分钟数，或 HH:mm）");
                }
            }

            var dbMaxFilesRaw = (request.DbMaxFiles ?? "").Trim();
            if (!string.IsNullOrEmpty(dbMaxFilesRaw))
            {
                foreach (var entry in dbMaxFilesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var colon = entry.IndexOf(':');
                    if (colon <= 0 || colon >= entry.Length - 1)
                        throw new ConfigValidationException($"每库最大保留数量格式错误: {entry}（应为 库名:数量）");
                    var db = entry[..colon].Trim();
                    var value = entry[(colon + 1)..].Trim();
                    if (!dbSet.Contains(db))
                        throw new ConfigValidationException($"每库最大保留数量中的 {db} 未在数据库列表中");
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxFiles) || maxFiles <= 0)
                        throw new ConfigValidationException($"每库最大保留数量 {db} 的值无效: {value}");
                }
            }

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

        private static BackupConfigView ToView(BackupConfig config)
        {
            var backtime = config.IntervalMinutes?.ToString(CultureInfo.InvariantCulture)
                           ?? (config.DailyAtUtc is { } d ? $"{d.Hour:00}:{d.Minute:00}" : "60");

            // dbtimes 视图：库名:计划（沿用任务级 backtime 格式化规则）
            var dbTimes = config.DbSchedules
                .Select(kv =>
                {
                    var s = kv.Value;
                    var t = s.IntervalMinutes?.ToString(CultureInfo.InvariantCulture)
                            ?? $"{s.DailyAtUtc!.Value.Hour:00}:{s.DailyAtUtc.Value.Minute:00}";
                    return $"{kv.Key}:{t}";
                })
                .ToList();
            var dbMaxFiles = config.DbMaxFiles
                .Select(kv => $"{kv.Key}:{kv.Value.ToString(CultureInfo.InvariantCulture)}")
                .ToList();

            return new BackupConfigView
            {
                FileName = Path.GetFileName(config.SourceFile),
                DbType = config.DbType,
                Host = config.Host,
                Port = config.Port,
                User = config.User,
                PasswordConfigured = !string.IsNullOrEmpty(config.Password),
                Password = config.Password,
                Databases = string.Join(',', config.Databases),
                SaveDir = config.SaveDirRelative,
                MaxFiles = config.MaxFiles,
                Backtime = backtime,
                DbTimes = string.Join(',', dbTimes),
                DbMaxFiles = string.Join(',', dbMaxFiles),
            };
        }

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

        /// <summary>
        /// 从备份文件名提取库名。文件名格式：{db}_{yyyy-MM-dd__HH.mm.ss}.sql
        /// （见 BackupRunner.BackupOneDatabase）。库名本身可能含下划线，
        /// 所以按结尾的时间戳来切，而不是找最后一个下划线。无法识别返回空。
        /// </summary>
        private static string ExtractDatabase(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var match = BackupFileName().Match(name);
            return match.Success ? match.Groups[1].Value : "";
        }

        /// <summary>匹配 {库名}_{yyyy-MM-dd__HH.mm.ss}，组1=库名。</summary>
        [GeneratedRegex(@"^(.+)_\d{4}-\d{2}-\d{2}__\d{2}\.\d{2}\.\d{2}$", RegexOptions.CultureInvariant)]
        private static partial Regex BackupFileName();
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
    /// <summary>当前已保存的数据库密码明文，供编辑界面回显。</summary>
    public string Password { get; init; } = "";
    public string Databases { get; init; } = "";
    public string SaveDir { get; init; } = "/backup/";
    public int MaxFiles { get; init; } = 180;
    public string Backtime { get; init; } = "60";
    /// <summary>每个库的单独备份计划，格式 db1:60,db2:02:00；空表示全部沿用任务级 Backtime。</summary>
    public string DbTimes { get; init; } = "";
    public string DbMaxFiles { get; init; } = "";
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
    /// <summary>每个库的单独备份计划，格式 db1:60,db2:02:00；空表示全部沿用 Backtime。</summary>
    public string DbTimes { get; init; } = "";
    public string DbMaxFiles { get; init; } = "";
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

/// <summary>单个备份文件的展示视图，按创建时间从新到旧编号。</summary>
public sealed class BackupFileView
{
    /// <summary>序号，从 1 开始（最新的为 1）。</summary>
    public int Index { get; set; }
    /// <summary>文件名，例如 users_db_2026-08-04__01.02.03.sql。</summary>
    public string Name { get; set; } = "";
    /// <summary>所属库名（从文件名前导部分解析）；无法识别时为 "_other"。</summary>
    public string Database { get; set; } = "";
    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; init; }
    /// <summary>文件创建时间（UTC）。</summary>
    public DateTime CreatedAtUtc { get; init; }
}

/// <summary>回收站中被删除的配置视图。</summary>
public sealed class TrashedConfigView
{
    public string FileName { get; init; } = "";
    public string DbType { get; init; } = "mysql";
    public string Host { get; init; } = "";
    public string Port { get; init; } = "";
    public string User { get; init; } = "";
    public string Databases { get; init; } = "";
    /// <summary>移入回收站的时间（UTC）。</summary>
    public DateTime DeletedAtUtc { get; init; }
    /// <summary>解析失败时的错误信息；成功时为 null。</summary>
    public string? Error { get; init; }
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
