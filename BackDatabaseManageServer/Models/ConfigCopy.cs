namespace BackDatabaseManageServer.Models;

/// <summary>复制任务的请求：把源节点上的任务复制到目标节点。</summary>
public sealed class CopyConfigsRequest
{
    /// <summary>来源节点 Id，必须与目标节点不同。</summary>
    public Guid SourceNodeId { get; init; }

    /// <summary>要复制的任务文件名列表；为空或 null 表示复制源节点的全部任务。</summary>
    public List<string>? FileNames { get; init; }

    /// <summary>为 true 时覆盖目标节点上已存在的同名任务；否则跳过同名任务。</summary>
    public bool Overwrite { get; init; }
}

/// <summary>
/// back 节点 <c>/api/configs</c> 返回的任务视图，用于跨节点复制任务。
/// 忽略 back 返回的 passwordConfigured / error 等附加字段（反序列化时自动跳过）。
/// </summary>
public sealed class RemoteConfigView
{
    public string FileName { get; init; } = "";
    public string DbType { get; init; } = "mysql";
    public string Host { get; init; } = "";
    public string Port { get; init; } = "";
    public string User { get; init; } = "";
    public string Password { get; init; } = "";
    public string Databases { get; init; } = "";
    public string SaveDir { get; init; } = "";
    public int MaxFiles { get; init; } = 180;
    public string Backtime { get; init; } = "60";
    public string DbTimes { get; init; } = "";
    public string DbMaxFiles { get; init; } = "";
    public string? Error { get; init; }
}