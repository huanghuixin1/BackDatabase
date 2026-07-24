using BackDatabase.Config;

namespace BackDatabase.Services.Strategies;

/// <summary>
/// PostgreSQL 备份策略：调用 pg_dump，通过 --file 写 SQL，密码走 PGPASSWORD。
/// conf：dbType=pgsql
/// </summary>
public sealed class PgSqlBackupStrategy : IDatabaseBackupStrategy
{
    public IReadOnlyCollection<string> SupportedDbTypes { get; } = ["pgsql", "postgres", "postgresql"];

    public DumpCommand BuildCommand(BackupConfig config, string database, string sqlFilePath)
    {
        IReadOnlyList<string> args =
        [
            $"--host={config.Host}",
            $"--port={config.Port}",
            $"--username={config.User}",
            $"--dbname={database}",
            $"--file={sqlFilePath}",
        ];

        var display =
            $"pg_dump --host {config.Host} --port {config.Port} " +
            $"--username={config.User} --dbname={database} --file={sqlFilePath}";

        return new DumpCommand
        {
            FileName = "pg_dump",
            Arguments = args,
            // pg_dump 从环境变量读密码，避免必须交互输入
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["PGPASSWORD"] = config.Password,
            },
            RedirectStdoutToFile = false,
            DisplayCommand = display,
        };
    }
}
