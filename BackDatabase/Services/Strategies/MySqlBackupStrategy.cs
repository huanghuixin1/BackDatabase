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
        var sslOption = string.Equals(config.DbType, "mariadb", StringComparison.OrdinalIgnoreCase)
            ? "--skip-ssl"
            : "--ssl-mode=DISABLED";

        IReadOnlyList<string> args =
        [
            sslOption,
            "--single-transaction", // InnoDB 一致性快照，尽量不锁表
            $"--host={config.Host}",
            $"--port={config.Port}",
            $"-u{config.User}",
            $"-p{config.Password}",
            "--databases",
            database,
        ];

        // 日志脱敏：不打印真实密码
        var display =
            $"mysqldump {sslOption} --single-transaction --host {config.Host} --port {config.Port} " +
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
