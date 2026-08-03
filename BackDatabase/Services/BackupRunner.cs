using System.Diagnostics;
using System.Text;
using BackDatabase.Config;
using BackDatabase.Services.Strategies;

namespace BackDatabase.Services;

/// <summary>
/// 执行一次备份流程（对应 Go 版 invokeBack）：
/// 1. 确保保存目录存在；
/// 2. 先删除 0 字节空文件，再按 maxfiles 删除最旧文件；
/// 3. 通过策略解析 dbType，调用对应 dump 工具生成 .sql；
/// 4. 失败则删除残缺文件并自动重试一次。
/// </summary>
public sealed class BackupRunner
{
    /// <summary>程序根目录，用于把 conf 里的相对 savedir 拼成绝对路径</summary>
    private readonly string _baseDir;

    /// <summary>按 dbType 解析具体数据库备份策略</summary>
    private readonly DatabaseBackupStrategyFactory _strategyFactory;

    /// <summary>备份失败时可选推送（env.conf 未配置则为空操作）</summary>
    private readonly PushNotifier? _pushNotifier;

    /// <summary>备份运行注册表，记录每次运行的状态与日志；为 null 时不记录。</summary>
    private readonly BackupRunRegistry? _registry;

    public BackupRunner(
        string baseDir,
        DatabaseBackupStrategyFactory? strategyFactory = null,
        PushNotifier? pushNotifier = null,
        BackupRunRegistry? registry = null)
    {
        _baseDir = baseDir;
        // 未注入时使用内置 MySQL / PostgreSQL 策略
        _strategyFactory = strategyFactory ?? DatabaseBackupStrategyFactory.CreateDefault();
        _pushNotifier = pushNotifier;
        _registry = registry;
    }

    /// <summary>
    /// 同步执行一次备份（仅写日志到控制台，不进入运行注册表）。
    /// 保留以兼容旧调用方；新调用方应使用 <see cref="RunAsync"/>。
    /// </summary>
    public void Run(
        BackupConfig config,
        IReadOnlyList<string>? onlyDatabases = null,
        CancellationToken cancellationToken = default)
        => RunCore(config, onlyDatabases, null, cancellationToken);

    /// <summary>
    /// 异步执行一次备份，并把过程记录到 <see cref="BackupRunRegistry"/>。
    /// 返回 false 表示该配置已有备份在运行（取锁失败）。
    /// 单次异常按现有约定不抛出（吞掉并记为 failed），返回 true 表示已执行。
    /// </summary>
    public async Task<bool> RunAsync(
        BackupConfig config,
        string trigger,
        CancellationToken cancellationToken = default)
    {
        if (_registry is null)
        {
            // 未注入注册表时退化为同步执行
            try
            {
                RunCore(config, null, null, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{Path.GetFileName(config.SourceFile)}] 备份异常: {ex.Message}");
            }
            return true;
        }

        var handle = _registry.BeginRun(config, trigger);
        if (handle is null)
            return false;

        bool success = false;
        string? error = null;
        try
        {
            RunCore(config, null, handle, cancellationToken);
            success = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 取消：记为失败但不发推送（与现有取消语义一致）
            error = "备份已取消";
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            // 单次异常不向上抛，与调度循环既有约定一致
        }
        finally
        {
            _registry.FinishRun(handle, success, error);
        }
        // 走到这里说明已成功取锁并执行（无论成功或吞掉的异常），返回 true。
        return true;
    }

    /// <summary>
    /// 备份核心流程。
    /// </summary>
    /// <param name="config">任务配置</param>
    /// <param name="onlyDatabases">
    /// 可选：只备份指定库（重试单个库时使用）；
    /// 为 null 时备份 conf 中配置的全部 dbs。
    /// </param>
    /// <param name="handle">运行记录句柄，用于同步日志到 Web；为 null 时只写控制台。</param>
    private void RunCore(
        BackupConfig config,
        IReadOnlyList<string>? onlyDatabases,
        BackupRunHandle? handle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var saveDir = config.ResolveSaveDir(_baseDir);
        Directory.CreateDirectory(saveDir);

        // 先删空文件、再按数量裁剪旧备份，再写新文件，避免磁盘被脏/旧文件占满
        TrimOldFiles(saveDir, config.MaxFiles, handle);

        var dbs = onlyDatabases ?? config.Databases;
        if (dbs.Count == 0)
        {
            Log(handle, $"[{Path.GetFileName(config.SourceFile)}] 未配置 dbs，跳过");
            return;
        }

        // 逐个数据库备份（与 Go 版 for _, db := range dbs 一致）
        foreach (var db in dbs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupOneDatabase(config, saveDir, db, handle, cancellationToken);
        }
    }

    /// <summary>
    /// 备份单个数据库：策略拼命令 → 启动进程 → 失败重试。
    /// </summary>
    /// <param name="isRetry">是否已是重试；true 时失败不再递归重试，防止死循环</param>
    private void BackupOneDatabase(
        BackupConfig config,
        string saveDir,
        string db,
        BackupRunHandle? handle,
        CancellationToken cancellationToken,
        bool isRetry = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 文件名格式对齐 Go：{库名}_{UTC时间}.sql，时间里用点代替冒号，避免 Windows 路径非法字符
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd__HH.mm.ss");
        var sqlPath = Path.Combine(saveDir, $"{db}_{stamp}.sql");

        // 策略模式：按 dbType 取策略，避免在此写 switch / if-else
        var strategy = _strategyFactory.Resolve(config.DbType);
        if (strategy is null)
        {
            var reason =
                $"不支持的数据库类型: {config.DbType}。已注册: {string.Join(", ", _strategyFactory.RegisteredDbTypes)}";
            Log(handle, reason);
            // 配置错误无法靠重试解决，直接推送
            NotifyFailure(config, db, reason, cancellationToken);
            return;
        }

        DumpCommand command;
        try
        {
            command = strategy.BuildCommand(config, db, sqlPath);
        }
        catch (Exception ex)
        {
            var reason = $"构建备份命令失败 ({config.DbType}/{db}): {ex.Message}";
            Log(handle, reason);
            NotifyFailure(config, db, reason, cancellationToken);
            return;
        }

        Log(handle, $"备份命令 {command.DisplayCommand}");

        try
        {
            var redirectPath = command.RedirectStdoutToFile ? sqlPath : null;
            var (exitCode, stdout, stderr) = RunProcess(
                command.FileName,
                command.Arguments,
                command.ExtraEnvironment,
                redirectPath,
                handle,
                cancellationToken);

            if (exitCode != 0)
            {
                var combined = string.Join(Environment.NewLine,
                    new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var chinese = TryDecodeGbk(combined);
                Log(handle, $"错误信息: {combined} | 中文解码: {chinese} | exit={exitCode}");

                // 失败时删掉可能不完整的 sql，避免脏文件占用 maxfiles 名额
                TryDelete(sqlPath, handle);

                if (!isRetry)
                {
                    Log(handle, "重新执行一次备份");
                    BackupOneDatabase(config, saveDir, db, handle, cancellationToken, isRetry: true);
                }
                else
                {
                    // 重试仍失败再推送，避免首次瞬时失败刷屏
                    var reason = $"exit={exitCode}; {chinese}";
                    if (string.IsNullOrWhiteSpace(chinese))
                        reason = $"exit={exitCode}; {combined}";
                    NotifyFailure(config, db, reason, cancellationToken);
                }
            }
            else
            {
                Log(handle, $"数据库 {db} 备份完毕 -> {sqlPath}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 中断时不重试、不推送；子进程释放文件句柄后删除当前残缺备份。
            var deleted = TryDelete(sqlPath, handle);
            Log(handle, deleted
                ? $"数据库 {db} 备份已中断，已删除残缺文件: {sqlPath}"
                : $"数据库 {db} 备份已中断，但残缺文件删除失败: {sqlPath}");
            throw;
        }
        catch (Exception ex)
        {
            // 例如 dump 工具不在 PATH、无法启动进程等
            Log(handle, $"执行备份失败 ({db}): {ex.Message}");
            TryDelete(sqlPath, handle);
            if (!isRetry)
            {
                Log(handle, "重新执行一次备份");
                BackupOneDatabase(config, saveDir, db, handle, cancellationToken, isRetry: true);
            }
            else
            {
                NotifyFailure(config, db, ex.Message, cancellationToken);
            }
        }
    }

    /// <summary>备份最终失败时发 HxPush 通知（未配置推送则跳过并打日志）。</summary>
    private void NotifyFailure(
        BackupConfig config,
        string database,
        string reason,
        CancellationToken cancellationToken)
    {
        Log(null,
            $"[备份失败待推送] conf={Path.GetFileName(config.SourceFile)} db={database} reason={reason}");
        if (_pushNotifier is null)
        {
            Log(null, "[推送跳过] BackupRunner 未注入 PushNotifier");
            return;
        }

        _pushNotifier.NotifyBackupFailure(config, database, reason, cancellationToken);
    }

    /// <summary>同时写控制台与运行记录日志（handle 为 null 时只写控制台）。</summary>
    private static void Log(BackupRunHandle? handle, string message)
    {
        Console.WriteLine(message);
        handle?.Append(message);
    }

    /// <summary>
    /// 启动外部进程并等待结束。
    /// </summary>
    /// <param name="fileName">可执行文件名（依赖 PATH）</param>
    /// <param name="args">参数列表</param>
    /// <param name="extraEnv">额外环境变量，可为 null</param>
    /// <param name="redirectStdoutTo">
    /// 若不为 null，将标准输出原样写入该文件（用于 mysqldump）；
    /// 若为 null，则把标准输出收集到字符串返回（pg_dump 一般无大量 stdout）。
    /// </param>
    /// <param name="handle">运行记录句柄，用于把 stderr/stdout 错误同步到 Web 日志。</param>
    private static (int ExitCode, string Stdout, string Stderr) RunProcess(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? extraEnv,
        string? redirectStdoutTo,
        BackupRunHandle? handle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false, // 必须 false 才能重定向 IO
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // ArgumentList 会正确处理含空格参数，避免自己拼命令行
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        if (extraEnv != null)
        {
            foreach (var (k, v) in extraEnv)
                psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        var stdout = new StringBuilder();

        // 异步读 stderr，避免缓冲区塞满导致子进程死锁；同时同步到运行日志
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
                handle?.Append($"[stderr] {e.Data}");
            }
        };

        // 不需要重定向到文件时，异步读 stdout
        if (redirectStdoutTo is null)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stdout.AppendLine(e.Data);
                    handle?.Append($"[stdout] {e.Data}");
                }
            };
        }

        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程: {fileName}");

        // Ctrl+C 时终止整个 dump 进程树，避免客户端继续写入半成品文件。
        using var cancellationRegistration = cancellationToken.Register(() => TryKill(process));

        process.BeginErrorReadLine();

        Task? stdoutCopyTask = null;
        FileStream? outputFile = null;
        if (redirectStdoutTo is not null)
        {
            // 同步把 stdout 字节流拷到 .sql 文件（保留原始 SQL 内容，不做二次编码转换）
            outputFile = new FileStream(redirectStdoutTo, FileMode.Create, FileAccess.Write, FileShare.Read);
            stdoutCopyTask = process.StandardOutput.BaseStream.CopyToAsync(outputFile, cancellationToken);
        }
        else
        {
            process.BeginOutputReadLine();
        }

        try
        {
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
            stdoutCopyTask?.GetAwaiter().GetResult();
            process.WaitForExit(); // 确保异步输出事件已经全部处理完毕。
            return (process.ExitCode, stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            process.WaitForExit();
            try
            {
                stdoutCopyTask?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // 复制任务使用同一个取消令牌，取消属于预期结果。
            }
            throw;
        }
        finally
        {
            outputFile?.Dispose();
        }
    }

    /// <summary>尽力终止备份工具及其子进程；进程可能已自行退出。</summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 取消清理属于尽力操作，退出竞态或权限问题不覆盖原始异常。
        }
    }

    /// <summary>
    /// 清理保存目录：
    /// 1. 先删除 0 字节空文件（失败/中断留下的残缺备份）；
    /// 2. 再当文件数 &gt; maxFiles 时，按 LastWriteTimeUtc 从旧到新删除，直到不超过上限。
    /// 对应 Go 的 getMinModifyTimeFile + 循环 Remove，并额外处理空文件。
    /// </summary>
    private static void TrimOldFiles(string saveDir, int maxFiles, BackupRunHandle? handle)
    {
        // 先清 0KB 空文件，再按数量裁剪，避免空文件占 maxfiles 名额
        DeleteEmptyFiles(saveDir, handle);

        if (maxFiles <= 0)
            return;

        while (true)
        {
            var files = Directory.EnumerateFiles(saveDir)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            if (files.Count <= maxFiles)
                break;

            var oldest = files[0];
            try
            {
                oldest.Delete();
                Log(handle, $"已删除过期备份: {oldest.FullName}");
            }
            catch (Exception ex)
            {
                // 删不掉就停止循环，避免死循环打日志
                Log(handle, $"删除文件失败 {oldest.FullName}: {ex.Message}");
                break;
            }
        }
    }

    /// <summary>
    /// 删除目录内所有 0 字节文件（备份失败、中断或 dump 空输出时常见）。
    /// 单个文件删失败只打日志并继续，不中断整体清理。
    /// </summary>
    private static void DeleteEmptyFiles(string saveDir, BackupRunHandle? handle)
    {
        IEnumerable<FileInfo> emptyFiles;
        try
        {
            emptyFiles = Directory.EnumerateFiles(saveDir)
                .Select(f => new FileInfo(f))
                .Where(f => f.Length == 0)
                .ToList();
        }
        catch (Exception ex)
        {
            Log(handle, $"扫描空文件失败 {saveDir}: {ex.Message}");
            return;
        }

        foreach (var file in emptyFiles)
        {
            try
            {
                file.Delete();
                Log(handle, $"已删除空备份文件: {file.FullName}");
            }
            catch (Exception ex)
            {
                Log(handle, $"删除空文件失败 {file.FullName}: {ex.Message}");
            }
        }
    }

    /// <summary>尽力删除文件，忽略任何异常（用于清理失败产生的残缺 sql）</summary>
    private static bool TryDelete(string path, BackupRunHandle? handle)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);

            return true;
        }
        catch (Exception ex)
        {
            Log(handle, $"删除残缺备份失败 {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Windows 下 mysqldump 错误信息有时是 GBK。
    /// 这里做一次尽力转换，方便控制台阅读；转换失败则原样返回。
    /// </summary>
    private static string TryDecodeGbk(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        try
        {
            // 说明：若字符串已经是正确 Unicode，再经 Default→GBK 可能仍不完美，
            // 仅作为辅助日志，与 Go 版 translateErrorToChineseInGo 目的一致。
            var gbk = Encoding.GetEncoding(936); // 936 = GBK
            var bytes = Encoding.Default.GetBytes(text);
            return gbk.GetString(bytes);
        }
        catch
        {
            return text;
        }
    }
}
