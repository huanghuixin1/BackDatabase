using BackDatabase.Config;

namespace BackDatabase.Services.Strategies;

/// <summary>
/// 外部 dump 工具要执行的命令描述。
/// 由具体数据库策略构建，BackupRunner 只负责启动进程。
/// </summary>
public sealed class DumpCommand
{
    /// <summary>可执行文件名（依赖 PATH），如 mysqldump、pg_dump</summary>
    public required string FileName { get; init; }

    /// <summary>命令行参数列表（不经 shell 拼接）</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>额外环境变量，例如 pgsql 的 PGPASSWORD</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    /// <summary>
    /// 是否将标准输出重定向写入目标 sql 文件。
    /// true：工具把 SQL 打到 stdout（如 mysqldump）；
    /// false：工具自己通过参数写文件（如 pg_dump --file）。
    /// </summary>
    public bool RedirectStdoutToFile { get; init; }

    /// <summary>日志用的脱敏命令行（密码显示为 ***）</summary>
    public required string DisplayCommand { get; init; }
}

/// <summary>
/// 数据库备份策略：根据配置拼出对应 dump 工具的命令。
/// 新增数据库类型时实现此接口，并在 <see cref="DatabaseBackupStrategyFactory"/> 中注册即可。
/// </summary>
public interface IDatabaseBackupStrategy
{
    /// <summary>
    /// 本策略支持的 dbType 标识（conf 中的 dbType 值，小写比较）。
    /// 可支持多个别名，例如 mysql / mariadb。
    /// </summary>
    IReadOnlyCollection<string> SupportedDbTypes { get; }

    /// <summary>
    /// 根据连接配置、库名、目标 sql 路径构建 dump 命令。
    /// </summary>
    /// <param name="config">任务配置（主机、端口、账号等）</param>
    /// <param name="database">当前要备份的数据库名</param>
    /// <param name="sqlFilePath">输出 .sql 文件的绝对路径</param>
    DumpCommand BuildCommand(BackupConfig config, string database, string sqlFilePath);
}
