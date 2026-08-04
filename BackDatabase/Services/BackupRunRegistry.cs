using System.Collections.Concurrent;
using BackDatabase.Config;

namespace BackDatabase.Services;

/// <summary>
/// 备份运行状态。idle=从未运行；running=进行中；success=最近一次成功；failed=最近一次失败。
/// </summary>
public enum BackupRunStatus
{
    Idle,
    Running,
    Success,
    Failed,
}

/// <summary>单次备份运行的实时视图，供 Web 展示。</summary>
public sealed class BackupRunView
{
    /// <summary>所属配置文件名（带 .conf）。</summary>
    public string FileName { get; set; } = "";
    /// <summary>该次运行备份的数据库；为旧版任务级运行兼容保留，可能为空。</summary>
    public string Database { get; set; } = "";
    public BackupRunStatus Status { get; set; } = BackupRunStatus.Idle;
    /// <summary>触发来源：manual / schedule。</summary>
    public string Trigger { get; set; } = "";
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? Error { get; set; }
    /// <summary>最近一次运行的日志行（已裁剪到上限）。</summary>
    public IReadOnlyList<string> Log { get; set; } = Array.Empty<string>();
}

/// <summary>
/// 单次备份运行的可变句柄。日志追加与状态更新都在自身锁内完成，
/// 避免外部对同一记录并发读写时出现撕裂。
/// </summary>
public sealed class BackupRunHandle
{
    private readonly object _gate = new();
    private readonly List<string> _log = new();
    private readonly int _maxLines;

    public string FileName { get; }
    public string Database { get; }
    /// <summary>注册表里的唯一键：fileName#database（小写）。</summary>
    public string Key { get; }
    public BackupRunView View { get; }

    public BackupRunHandle(string fileName, string database, string trigger, int maxLines)
    {
        FileName = fileName;
        Database = database;
        Key = BackupRunRegistry.MakeKey(fileName, database);
        _maxLines = maxLines;
        View = new BackupRunView
        {
            FileName = fileName,
            Database = database,
            Status = BackupRunStatus.Running,
            Trigger = trigger,
            StartedAtUtc = DateTime.UtcNow,
        };
    }

    public void Append(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        lock (_gate)
        {
            _log.Add(line);
            // 环形裁剪：只保留最近 _maxLines 行，避免长任务无限增长
            if (_log.Count > _maxLines)
                _log.RemoveRange(0, _log.Count - _maxLines);
        }
    }

    public void Finish(bool success, string? error)
    {
        lock (_gate)
        {
            View.Status = success ? BackupRunStatus.Success : BackupRunStatus.Failed;
            View.FinishedAtUtc = DateTime.UtcNow;
            View.Error = error;
            View.Log = _log.ToArray();
        }
    }

    /// <summary>读取当前快照（运行中也可读，用于轮询实时日志）。</summary>
    public BackupRunView Snapshot()
    {
        lock (_gate)
        {
            return new BackupRunView
            {
                FileName = View.FileName,
                Database = View.Database,
                Status = View.Status,
                Trigger = View.Trigger,
                StartedAtUtc = View.StartedAtUtc,
                FinishedAtUtc = View.FinishedAtUtc,
                Error = View.Error,
                Log = _log.ToArray(),
            };
        }
    }
}

/// <summary>
/// 备份运行注册表：记录每个「配置+数据库」最近一次运行的状态与日志，
/// 并为每个「配置+数据库」维护一把锁，保证同一库不会并发备份
/// （手动立即备份与定时调度互斥；同一任务下不同库可并行）。
/// </summary>
public sealed class BackupRunRegistry
{
    /// <summary>每个「配置+库」最近一次运行的句柄（按 fileName#database 索引）。</summary>
    private readonly ConcurrentDictionary<string, BackupRunHandle> _handles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每个「配置+库」一把信号量，保证同库不并发备份。</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxLogLines = 200;

    /// <summary>
    /// 尝试开始一次「单库」运行。返回 null 表示该库已有备份在运行（调用方应跳过/回 409）。
    /// </summary>
    /// <param name="database">本次运行备份的库名；为空时视为任务级运行（旧路径）。</param>
    public BackupRunHandle? BeginRun(BackupConfig config, string database, string trigger)
    {
        var fileName = Path.GetFileName(config.SourceFile);
        var key = MakeKey(fileName, database);
        var semaphore = _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

        if (!semaphore.Wait(0))
            return null;

        var handle = new BackupRunHandle(fileName, database, trigger, MaxLogLines);
        _handles[key] = handle;
        return handle;
    }

    /// <summary>结束一次运行：记录结果并释放锁。</summary>
    public void FinishRun(BackupRunHandle handle, bool success, string? error)
    {
        try
        {
            handle.Finish(success, error);
        }
        finally
        {
            Release(handle.Key);
        }
    }

    /// <summary>仅释放锁（异常路径下使用，不更新状态）。</summary>
    public void Release(string key)
    {
        if (_locks.TryGetValue(key, out var semaphore))
        {
            try { semaphore.Release(); } catch { /* 释放竞态忽略 */ }
        }
    }

    /// <summary>构造注册表键：fileName#database（database 为空时退化为 fileName 旧格式）。</summary>
    public static string MakeKey(string fileName, string database)
        => string.IsNullOrEmpty(database) ? fileName : $"{fileName}#{database.ToLowerInvariant()}";

    public BackupRunView GetView(string fileName)
        => GetView(fileName, "");

    /// <summary>查询某个「配置+库」的最新运行状态。</summary>
    public BackupRunView GetView(string fileName, string database)
    {
        var key = MakeKey(fileName, database);
        if (_handles.TryGetValue(key, out var handle))
            return handle.Snapshot();
        return new BackupRunView { FileName = fileName, Database = database, Status = BackupRunStatus.Idle };
    }

    public IReadOnlyList<BackupRunView> GetAllViews() =>
        _handles.Values.Select(h => h.Snapshot()).ToList();

    /// <summary>返回某个配置下所有库的运行视图（含 fileName 匹配的所有 handle）。</summary>
    public IReadOnlyList<BackupRunView> GetViewsForConfig(string fileName)
    {
        var prefix = fileName + "#";
        return _handles.Values
            .Where(h => string.Equals(h.FileName, fileName, StringComparison.OrdinalIgnoreCase)
                        || h.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Snapshot())
            .ToList();
    }
}
