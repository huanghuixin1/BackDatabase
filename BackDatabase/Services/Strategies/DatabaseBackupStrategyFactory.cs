namespace BackDatabase.Services.Strategies;

/// <summary>
/// 数据库备份策略工厂：按 conf 中的 dbType 解析出对应策略。
/// 新增数据库时：
/// 1. 实现 <see cref="IDatabaseBackupStrategy"/>；
/// 2. 在 <see cref="CreateDefault"/> 里 Register 即可，无需改 BackupRunner。
/// </summary>
public sealed class DatabaseBackupStrategyFactory
{
    /// <summary>dbType（小写）→ 策略实例</summary>
    private readonly Dictionary<string, IDatabaseBackupStrategy> _strategies =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 注册策略。同一 dbType 重复注册会覆盖。
    /// </summary>
    public DatabaseBackupStrategyFactory Register(IDatabaseBackupStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        foreach (var key in strategy.SupportedDbTypes)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            _strategies[key.Trim()] = strategy;
        }
        return this;
    }

    /// <summary>
    /// 按 dbType 获取策略；不支持时返回 null。
    /// </summary>
    public IDatabaseBackupStrategy? Resolve(string? dbType)
    {
        if (string.IsNullOrWhiteSpace(dbType))
            return null;
        return _strategies.TryGetValue(dbType.Trim(), out var strategy) ? strategy : null;
    }

    /// <summary>
    /// 当前已注册的全部 dbType 标识（便于日志/排错）。
    /// </summary>
    public IReadOnlyCollection<string> RegisteredDbTypes => _strategies.Keys.OrderBy(k => k).ToArray();

    /// <summary>
    /// 创建默认工厂并注册内置策略（MySQL、PostgreSQL）。
    /// 以后加 SQL Server / Mongo 等在这里 Register 一行即可。
    /// </summary>
    public static DatabaseBackupStrategyFactory CreateDefault()
    {
        return new DatabaseBackupStrategyFactory()
            .Register(new MySqlBackupStrategy())
            .Register(new PgSqlBackupStrategy());
        // 示例：以后加 SQL Server
        // .Register(new SqlServerBackupStrategy());
    }
}
