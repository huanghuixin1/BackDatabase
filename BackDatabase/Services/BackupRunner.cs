using System.Diagnostics;
using System.Text;
using BackDatabase.Config;
using BackDatabase.Services.Strategies;

namespace BackDatabase.Services;

/// <summary>
/// 执行一次备份流程（对应 Go 版 invokeBack）：
/// 1. 确保保存目录存在；
/// 2. 若文件数超过 maxfiles，删除最旧文件；
/// 3. 通过策略解析 dbType，调用对应 dump 工具生成 .sql；
/// 4. 失败则删除残缺文件并自动重试一次。
/// </summary>
public sealed class BackupRunner
{
    /// <summary>程序根目录，用于把 conf 里的相对 savedir 拼成绝对路径</summary>
    private readonly string _baseDir;

    /// <summary>按 dbType 解析具体数据库备份策略</summary>
    private readonly DatabaseBackupStrategyFactory _strategyFactory;

    public BackupRunner(string baseDir, DatabaseBackupStrategyFactory? strategyFactory = null)
    {
        _baseDir = baseDir;
        // 未注入时使用内置 MySQL / PostgreSQL 策略
        _strategyFactory = strategyFactory ?? DatabaseBackupStrategyFactory.CreateDefault();
    }

    /// <summary>
    /// 按配置执行备份。
    /// </summary>
    /// <param name="config">任务配置</param>
    /// <param name="onlyDatabases">
    /// 可选：只备份指定库（重试单个库时使用）；
    /// 为 null 时备份 conf 中配置的全部 dbs。
    /// </param>
    public void Run(BackupConfig config, IReadOnlyList<string>? onlyDatabases = null)
    {
        var saveDir = config.ResolveSaveDir(_baseDir);
        Directory.CreateDirectory(saveDir);

        // 先裁剪旧文件，再写新备份，避免磁盘被旧文件占满
        TrimOldFiles(saveDir, config.MaxFiles);

        var dbs = onlyDatabases ?? config.Databases;
        if (dbs.Count == 0)
        {
            Console.WriteLine($"[{Path.GetFileName(config.SourceFile)}] 未配置 dbs，跳过");
            return;
        }

        // 逐个数据库备份（与 Go 版 for _, db := range dbs 一致）
        foreach (var db in dbs)
        {
            BackupOneDatabase(config, saveDir, db);
        }
    }

    /// <summary>
    /// 备份单个数据库：策略拼命令 → 启动进程 → 失败重试。
    /// </summary>
    /// <param name="isRetry">是否已是重试；true 时失败不再递归重试，防止死循环</param>
    private void BackupOneDatabase(BackupConfig config, string saveDir, string db, bool isRetry = false)
    {
        // 文件名格式对齐 Go：{库名}_{UTC时间}.sql，时间里用点代替冒号，避免 Windows 路径非法字符
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd__HH.mm.ss");
        var sqlPath = Path.Combine(saveDir, $"{db}_{stamp}.sql");

        // 策略模式：按 dbType 取策略，避免在此写 switch / if-else
        var strategy = _strategyFactory.Resolve(config.DbType);
        if (strategy is null)
        {
            Console.WriteLine(
                $"不支持的数据库类型: {config.DbType}。已注册: {string.Join(", ", _strategyFactory.RegisteredDbTypes)}");
            return;
        }

        DumpCommand command;
        try
        {
            command = strategy.BuildCommand(config, db, sqlPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"构建备份命令失败 ({config.DbType}/{db}): {ex.Message}");
            return;
        }

        Console.WriteLine($"备份命令 {command.DisplayCommand}");

        try
        {
            var redirectPath = command.RedirectStdoutToFile ? sqlPath : null;
            var (exitCode, stdout, stderr) = RunProcess(
                command.FileName,
                command.Arguments,
                command.ExtraEnvironment,
                redirectPath);

            if (exitCode != 0)
            {
                var combined = string.Join(Environment.NewLine,
                    new[] { stdout, stderr }.Where(s => !string.IsNullOrWhiteSpace(s)));
                var chinese = TryDecodeGbk(combined);
                Console.WriteLine($"错误信息: {combined} | 中文解码: {chinese} | exit={exitCode}");

                // 失败时删掉可能不完整的 sql，避免脏文件占用 maxfiles 名额
                TryDelete(sqlPath);

                if (!isRetry)
                {
                    Console.WriteLine("重新执行一次备份");
                    BackupOneDatabase(config, saveDir, db, isRetry: true);
                }
            }
            else
            {
                Console.WriteLine($"数据库 {db} 备份完毕 -> {sqlPath}");
            }
        }
        catch (Exception ex)
        {
            // 例如 dump 工具不在 PATH、无法启动进程等
            Console.WriteLine($"执行备份失败 ({db}): {ex.Message}");
            TryDelete(sqlPath);
            if (!isRetry)
            {
                Console.WriteLine("重新执行一次备份");
                BackupOneDatabase(config, saveDir, db, isRetry: true);
            }
        }
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
    private static (int ExitCode, string Stdout, string Stderr) RunProcess(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? extraEnv,
        string? redirectStdoutTo)
    {
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

        // 异步读 stderr，避免缓冲区塞满导致子进程死锁
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };

        // 不需要重定向到文件时，异步读 stdout
        if (redirectStdoutTo is null)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stdout.AppendLine(e.Data);
            };
        }

        if (!process.Start())
            throw new InvalidOperationException($"无法启动进程: {fileName}");

        process.BeginErrorReadLine();

        if (redirectStdoutTo is not null)
        {
            // 同步把 stdout 字节流拷到 .sql 文件（保留原始 SQL 内容，不做二次编码转换）
            using var fs = new FileStream(redirectStdoutTo, FileMode.Create, FileAccess.Write, FileShare.Read);
            process.StandardOutput.BaseStream.CopyTo(fs);
        }
        else
        {
            process.BeginOutputReadLine();
        }

        process.WaitForExit();
        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// 当目录内文件数 &gt; maxFiles 时，按 LastWriteTimeUtc 从旧到新删除，直到不超过上限。
    /// 对应 Go 的 getMinModifyTimeFile + 循环 Remove。
    /// </summary>
    private static void TrimOldFiles(string saveDir, int maxFiles)
    {
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
                Console.WriteLine($"已删除过期备份: {oldest.FullName}");
            }
            catch (Exception ex)
            {
                // 删不掉就停止循环，避免死循环打日志
                Console.WriteLine($"删除文件失败 {oldest.FullName}: {ex.Message}");
                break;
            }
        }
    }

    /// <summary>尽力删除文件，忽略任何异常（用于清理失败产生的残缺 sql）</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 忽略：清理失败不应中断主流程
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
