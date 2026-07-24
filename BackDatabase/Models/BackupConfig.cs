namespace BackDatabase.Config;

/// <summary>
/// 单个 .conf 配置文件对应的备份任务配置。
/// 字段含义与原 Go 版 backmysql 的 conf 键一一对应，便于直接复用旧配置。
/// </summary>
public sealed class BackupConfig
{
    /// <summary>来源配置文件完整路径（用于日志里显示文件名）</summary>
    public required string SourceFile { get; init; }

    /// <summary>
    /// 数据库类型。
    /// mysql：调用 mysqldump（MariaDB 也填 mysql）；
    /// pgsql：调用 pg_dump。
    /// </summary>
    public string DbType { get; init; } = "mysql";

    /// <summary>数据库主机地址</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>端口；mysql 默认 3306，pgsql 默认 5432（解析时按类型填默认）</summary>
    public string Port { get; init; } = "3306";

    /// <summary>登录用户名</summary>
    public string User { get; init; } = "root";

    /// <summary>登录密码（对应 conf 里的 pwd）</summary>
    public string Password { get; init; } = "";

    /// <summary>
    /// 需要备份的数据库名列表。
    /// conf 中 dbs 为逗号分隔，例如：ss,hhx
    /// </summary>
    public IReadOnlyList<string> Databases { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 备份保存路径（相对程序目录）。
    /// conf 示例：/backup/ 或 /test2/t/
    /// 会去掉开头斜杠后与 baseDir 拼接。
    /// </summary>
    public string SaveDirRelative { get; init; } = "/";

    /// <summary>
    /// 保存目录下最多保留的备份文件数量。
    /// 超过时按修改时间删除最旧的文件（对齐 Go 的 maxfiles，默认 180）。
    /// </summary>
    public int MaxFiles { get; init; } = 180;

    /// <summary>
    /// 备份间隔（分钟）。
    /// 与 <see cref="DailyAtUtc"/> 二选一：
    /// conf 的 backtime 能解析为数字 → 走间隔模式；
    /// 解析为 HH:mm → 走每日定点模式。
    /// </summary>
    public double? IntervalMinutes { get; init; }

    /// <summary>
    /// 每天在 UTC 时区的固定备份时刻（时, 分）。
    /// 注意：与 Go 版一样使用 UTC，不是本地时区。
    /// </summary>
    public (int Hour, int Minute)? DailyAtUtc { get; init; }

    /// <summary>
    /// 将 conf 中的相对 savedir 解析为绝对路径。
    /// </summary>
    /// <param name="baseDir">程序运行根目录（通常为 exe 所在目录）</param>
    /// <returns>规范化后的绝对保存目录</returns>
    public string ResolveSaveDir(string baseDir)
    {
        // 统一斜杠，并去掉开头的 / 或 \，避免 Path.Combine 把它当成根路径
        var relative = SaveDirRelative.Replace('\\', '/').TrimStart('/');
        return Path.GetFullPath(Path.Combine(baseDir, relative));
    }
}
