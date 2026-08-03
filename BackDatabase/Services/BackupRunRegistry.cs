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
    public string FileName { get; init; } = "";
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
    public BackupRunView View { get; }

    public BackupRunHandle(string fileName, string trigger, int maxLines)
    {
        FileName = fileName;
        _maxLines = maxLines;
        View = new BackupRunView
        {
            FileName = fileName,
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
/// 备份运行注册表：记录每个配置最近一次运行的状态与日志，
/// 并为每个配置维护一把锁，保证同一配置不会并发备份
/// （手动立即备份与定时调度互斥）。
/// </summary>
public sealed class BackupRunRegistry
{
    /// <summary>每个配置最近一次运行的句柄（按带后缀的 .conf 文件名索引）。</summary>
    private readonly ConcurrentDictionary<string, BackupRunHandle> _handles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每个配置一把信号量，保证同配置不并发备份。</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxLogLines = 200;

    /// <summary>
    /// 尝试开始一次运行。返回 null 表示该配置已有备份在运行（调用方应跳过/回 409）。
    /// 拿到锁后会创建句柄并写入字典，调用方在结束时必须调用 <see cref="FinishRun"/> 或
    /// <see cref="Release"/> 释放锁。
    /// </summary>
    public BackupRunHandle? BeginRun(BackupConfig config, string trigger)
    {
        var fileName = Path.GetFileName(config.SourceFile);
        var semaphore = _locks.GetOrAdd(fileName, _ => new SemaphoreSlim(1, 1));

        if (!semaphore.Wait(0))
            return null;

        var handle = new BackupRunHandle(fileName, trigger, MaxLogLines);
        _handles[fileName] = handle;
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
            Release(handle.FileName);
        }
    }

    /// <summary>仅释放锁（异常路径下使用，不更新状态）。</summary>
    public void Release(string fileName)
    {
        if (_locks.TryGetValue(fileName, out var semaphore))
        {
            try { semaphore.Release(); } catch { /* 释放竞态忽略 */ }
        }
    }

    public BackupRunView GetView(string fileName)
    {
        if (_handles.TryGetValue(fileName, out var handle))
            return handle.Snapshot();
        return new BackupRunView { FileName = fileName, Status = BackupRunStatus.Idle };
    }

    public IReadOnlyList<BackupRunView> GetAllViews() =>
        _handles.Values.Select(h => h.Snapshot()).ToList();
}
