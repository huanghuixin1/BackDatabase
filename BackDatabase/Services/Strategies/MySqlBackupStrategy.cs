using BackDatabase.Config;

namespace BackDatabase.Services.Strategies;

/// <summary>
/// MySQL / MariaDB 备份策略：调用 mysqldump，SQL 从标准输出写出。
/// conf：dbType=mysql（MariaDB 也填 mysql，或使用别名 mariadb）
/// </summary>
public sealed class MySqlBackupStrategy : IDatabaseBackupStrategy
{
    public IReadOnlyCollection<string> SupportedDbTypes { get; } = ["mysql", "mariadb"];

    public DumpCommand BuildCommand(BackupConfig config, string database, string sqlFilePath)
    {
        // mysqldump 默认把 dump 内容写到 stdout，由调用方重定向到文件
        // MySQL 8 no longer accepts --skip-ssl; MariaDB clients do not support --ssl-mode.
        var isMariaDbClient = string.Equals(config.DbType, "mariadb", StringComparison.OrdinalIgnoreCase);
        var sslOption = isMariaDbClient ? "--skip-ssl" : "--ssl-mode=DISABLED";

        // MySQL 8.0+ 客户端默认会查询 information_schema.COLUMN_STATISTICS（直方图统计），
        // 而 MySQL 5.x / MariaDB 服务端没有该表，直接报 Unknown table 'COLUMN_STATISTICS' (1109) 导致失败。
        // --column-statistics=0 跳过该查询；MariaDB 客户端没有此选项且不会发起该查询，不传。
        var columnStatisticsOption = isMariaDbClient ? "" : "--column-statistics=0";

        var args = new List<string>
        {
            sslOption,
            "--single-transaction", // InnoDB 一致性快照，尽量不锁表
            $"--host={config.Host}",
            $"--port={config.Port}",
            $"-u{config.User}",
            $"-p{config.Password}",
            "--databases",
            database,
        };
        if (columnStatisticsOption.Length > 0)
            args.Insert(1, columnStatisticsOption);

        // 日志脱敏：不打印真实密码
        var display =
            $"mysqldump {sslOption} {columnStatisticsOption}".TrimEnd() +
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
