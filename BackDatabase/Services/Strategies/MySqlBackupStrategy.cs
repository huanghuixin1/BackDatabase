using BackDatabase.Config;

namespace BackDatabase.Services.Strategies;

/// <summary>
/// MySQL / MariaDB 备份策略：调用 mysqldump，SQL 从标准输出写出。
/// conf：dbType=mysql（MariaDB 也填 mysql，或使用别名 mariadb）
/// </summary>
public sealed class MySqlBackupStrategy : IDatabaseBackupStrategy
{
    private readonly MySqlDumpClientDetector _detector;

    public MySqlBackupStrategy(MySqlDumpClientDetector detector)
    {
        _detector = detector;
    }

    public IReadOnlyCollection<string> SupportedDbTypes { get; } = ["mysql", "mariadb"];

    public DumpCommand BuildCommand(BackupConfig config, string database, string sqlFilePath)
    {
        // 不主动传 --skip-ssl / --ssl-mode，让不同版本客户端按默认行为协商 SSL。
        var kind = _detector.Detect();
        var columnStatisticsOption = kind == MySqlDumpClientKind.MySql8
            ? "--column-statistics=0" // 跳过直方图统计，兼容没有 COLUMN_STATISTICS 表的老服务端
            : "";

        var args = new List<string>();
        args.Add("--single-transaction"); // InnoDB 一致性快照，尽量不锁表
        if (columnStatisticsOption.Length > 0)
            args.Add(columnStatisticsOption);
        args.Add($"--host={config.Host}");
        args.Add($"--port={config.Port}");
        args.Add($"-u{config.User}");
        args.Add($"-p{config.Password}");
        args.Add("--databases");
        args.Add(database);

        // 日志脱敏：不打印真实密码
        var display =
            $"mysqldump {columnStatisticsOption}".TrimEnd() +
            $" --single-transaction --host {config.Host} --port {config.Port} " +
            $"-u{config.User} -p*** --databases {database} > {sqlFilePath}";

        return new DumpCommand
        {
            FileName = "mysqldump",
            Arguments = args,
            ExtraEnvironment = null,
            RedirectStdoutToFile = true,
            DisplayCommand = display,
        };
    }
}
