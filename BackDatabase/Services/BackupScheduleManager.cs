using BackDatabase.Config;

namespace BackDatabase.Services;

/// <summary>
/// 动态备份调度管理器。每个“配置文件 + 数据库”对应一个调度工作项；
/// 配置快照可热替换，计划或数据库集合变化时只重建受影响的工作项。
/// </summary>
public sealed class BackupScheduleManager
{
    private sealed class ScheduledWorker(string key, string fileName, string database)
    {
        public string Key { get; } = key;
        public string FileName { get; } = fileName;
        public string Database { get; } = database;
        public CancellationTokenSource ScheduleCancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public bool Retired { get; set; }
        public int ActiveLeases { get; set; }
    }

    private readonly BackupRunner _runner;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, BackupConfig> _configs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ScheduledWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _allWorkerTasks = [];
    private bool _stopping;

    public BackupScheduleManager(BackupRunner runner)
    {
        _runner = runner;
    }

    public void StartAll(IEnumerable<BackupConfig> configs)
    {
        foreach (var config in configs)
            Upsert(config);
    }

    /// <summary>
    /// 新增或替换一个任务配置。连接、凭据、目录和保留数量立即更新快照；
    /// 后续触发使用新快照。计划变化会从当前时刻重新计算等待时间。
    /// </summary>
    public void Upsert(BackupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var fileName = Path.GetFileName(config.SourceFile);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            _configs.TryGetValue(fileName, out var previous);
            _configs[fileName] = config;

            var databases = config.Databases
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var databaseSet = new HashSet<string>(databases, StringComparer.OrdinalIgnoreCase);

            foreach (var worker in _workers.Values
                         .Where(worker => string.Equals(worker.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                var scheduleChanged = previous is not null
                                      && databaseSet.Contains(worker.Database)
                                      && previous.EffectiveSchedule(worker.Database) != config.EffectiveSchedule(worker.Database);
                var databaseNameChanged = databaseSet.Contains(worker.Database)
                                          && !databases.Contains(worker.Database, StringComparer.Ordinal);
                if (!databaseSet.Contains(worker.Database) || scheduleChanged || databaseNameChanged)
                    RetireWorkerLocked(worker);
            }

            foreach (var database in databases)
            {
                var key = MakeWorkerKey(fileName, database);
                if (!_workers.ContainsKey(key))
                    StartWorkerLocked(fileName, database);
            }
        }

        Console.WriteLine($"配置已热更新: {fileName}，数据库数量: {config.Databases.Count}");
    }

    /// <summary>停止该配置的未来调度；已经开始的备份允许使用原快照完成。</summary>
    public void Remove(string fileName)
    {
        fileName = Path.GetFileName(fileName);
        lock (_gate)
        {
            _configs.Remove(fileName);
            foreach (var worker in _workers.Values
                         .Where(worker => string.Equals(worker.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                RetireWorkerLocked(worker);
            }
        }

        Console.WriteLine($"配置已停止调度: {fileName}");
    }

    public bool TryGetSnapshot(string fileName, out BackupConfig config)
    {
        fileName = Path.GetFileName(fileName);
        lock (_gate)
            return _configs.TryGetValue(fileName, out config!);
    }

    public IReadOnlyList<BackupConfig> GetSnapshots()
    {
        lock (_gate)
            return _configs.Values.ToArray();
    }

    public async Task WaitAllAsync()
    {
        Task[] tasks;
        lock (_gate)
            tasks = _allWorkerTasks.ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 应用退出和调度退休都是正常路径。
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stopping)
                return;
            _stopping = true;
            foreach (var worker in _workers.Values)
                worker.ScheduleCancellation.Cancel();
            _workers.Clear();
            _configs.Clear();
        }

        // 只有应用退出才取消正在执行的 dump 进程。
        _shutdown.Cancel();
    }

    private void StartWorkerLocked(string fileName, string database)
    {
        var key = MakeWorkerKey(fileName, database);
        var worker = new ScheduledWorker(key, fileName, database);
        _workers[key] = worker;
        worker.Task = Task.Run(() => RunLoopAsync(worker));
        _allWorkerTasks.RemoveAll(task => task.IsCompleted);
        _allWorkerTasks.Add(worker.Task);
    }

    private void RetireWorkerLocked(ScheduledWorker worker)
    {
        worker.Retired = true;
        if (_workers.TryGetValue(worker.Key, out var current) && ReferenceEquals(current, worker))
            _workers.Remove(worker.Key);
        worker.ScheduleCancellation.Cancel();
    }

    private async Task RunLoopAsync(ScheduledWorker worker)
    {
        BackupConfig initialConfig;
        lock (_gate)
        {
            if (!_configs.TryGetValue(worker.FileName, out initialConfig!))
                return;
        }

        var schedule = initialConfig.EffectiveSchedule(worker.Database);
        if (schedule.IsInvalid)
        {
            Console.WriteLine($"[{worker.FileName}/{worker.Database}] 无效的备份计划，跳过");
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            worker.ScheduleCancellation.Token,
            _shutdown.Token);
        var scheduleToken = linked.Token;

        if (schedule.IntervalMinutes is { } minutes)
        {
            while (!scheduleToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(minutes), scheduleToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!TryAcquireExecution(worker, out var config))
                    break;
                await RunScheduledBackupAsync(config, worker).ConfigureAwait(false);
            }
        }
        else if (schedule.DailyAtUtc is { } daily)
        {
            var lastRunDate = DateOnly.MinValue;
            while (!scheduleToken.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var today = DateOnly.FromDateTime(now);
                if (now.Hour == daily.Hour && now.Minute == daily.Minute && lastRunDate != today)
                {
                    lastRunDate = today;
                    if (!TryAcquireExecution(worker, out var config))
                        break;
                    await RunScheduledBackupAsync(config, worker).ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(40), scheduleToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private bool TryAcquireExecution(ScheduledWorker worker, out BackupConfig config)
    {
        lock (_gate)
        {
            if (worker.Retired
                || !_workers.TryGetValue(worker.Key, out var current)
                || !ReferenceEquals(current, worker)
                || !_configs.TryGetValue(worker.FileName, out config!)
                || !config.Databases.Contains(worker.Database, StringComparer.OrdinalIgnoreCase))
            {
                config = null!;
                return false;
            }

            // 取得租约即视为本轮已开始；之后发生的热删除/改计划不会取消本轮。
            worker.ActiveLeases++;
            return true;
        }
    }

    private async Task RunScheduledBackupAsync(BackupConfig config, ScheduledWorker worker)
    {
        try
        {
            // 热更新取消只停止未来调度；运行中的备份只响应应用退出。
            var ran = await _runner.RunAsync(
                config,
                "schedule",
                worker.Database,
                _shutdown.Token).ConfigureAwait(false);
            if (!ran)
                Console.WriteLine($"[{worker.FileName}/{worker.Database}] 跳过本轮备份：已有备份正在运行");
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // 应用退出。
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{worker.FileName}/{worker.Database}] 备份异常: {ex.Message}");
        }
        finally
        {
            lock (_gate)
                worker.ActiveLeases--;
        }
    }

    private static string MakeWorkerKey(string fileName, string database)
        => BackupRunRegistry.MakeKey(fileName, database);
}
