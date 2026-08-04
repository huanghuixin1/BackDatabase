using BackDatabase.Config;

namespace BackDatabase.Services;

/// <summary>
/// 备份任务调度器。
/// 「每个配置 × 每个库」对应一个独立后台循环，各自按自己的计划运行：
/// - 间隔模式：先等待一个周期再首次备份，之后每隔 N 分钟执行（不再启动即跑）；
/// - 每日模式：约每 40 秒检查一次 UTC 时分，到点且当天未跑过则执行。
/// 同任务下不同库的循环彼此独立、可并行；同一库的「手动立即备份」与本循环互斥（由 RunRegistry 取锁保证）。
/// </summary>
public sealed class BackupScheduler
{
    private readonly BackupRunner _runner;

    /// <summary>统一取消源：Stop() 后所有循环退出</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>已启动的后台任务列表，供 WaitAllAsync 等待</summary>
    private readonly List<Task> _workers = new();

    public BackupScheduler(BackupRunner runner)
    {
        _runner = runner;
    }

    /// <summary>
    /// 为每个配置的每个库各启动一个独立后台循环。
    /// </summary>
    public void StartAll(IEnumerable<BackupConfig> configs)
    {
        foreach (var config in configs)
        {
            var name = Path.GetFileName(config.SourceFile);
            var saveDir = config.ResolveSaveDir(AppContext.BaseDirectory);
            Console.WriteLine(
                $"数据库正在备份准备中: {config.Host}。保存路径为:{saveDir} 最大保存数量：{config.MaxFiles} 配置:{name}");
            Console.WriteLine($"数据库类型 {config.DbType}，库数量：{config.Databases.Count}");

            foreach (var db in config.Databases)
            {
                var capturedConfig = config;
                var capturedDb = db;
                _workers.Add(Task.Run(
                    () => RunLoopAsync(capturedConfig, capturedDb, _cts.Token), _cts.Token));
            }
        }
    }

    /// <summary>
    /// 等待所有调度任务结束。
    /// 正常情况下会一直阻塞，直到 <see cref="Stop"/> 被调用。
    /// </summary>
    public async Task WaitAllAsync()
    {
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消是正常退出路径，不向上抛
        }
    }

    /// <summary>请求停止所有调度循环（例如收到 Ctrl+C）</summary>
    public void Stop()
    {
        _cts.Cancel();
    }

    /// <summary>
    /// 单个「配置+库」的调度主循环，按该库生效计划运行。
    /// </summary>
    private async Task RunLoopAsync(BackupConfig config, string database, CancellationToken ct)
    {
        var name = Path.GetFileName(config.SourceFile);
        var schedule = config.EffectiveSchedule(database);

        if (schedule.IsInvalid)
        {
            // ConfigLoader 已保证任务级默认有效；这里兜底
            Console.WriteLine($"[{name}/{database}] 无效的备份计划，跳过");
            return;
        }

        if (schedule.IntervalMinutes is { } minutes)
        {
            // ---------- 间隔模式 ----------
            // 启动后先等待一个周期再首次备份（不再启动即跑），避免重启时集中触发。
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // 收到停止信号
                }

                try
                {
                    var ran = await _runner.RunAsync(config, "schedule", database, ct).ConfigureAwait(false);
                    if (!ran)
                        Console.WriteLine($"[{name}/{database}] 跳过本轮备份：已有备份正在运行");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{name}/{database}] 备份异常: {ex.Message}");
                }
            }
        }
        else if (schedule.DailyAtUtc is { } daily)
        {
            // ---------- 每日定点模式（UTC）----------
            // 每 40 秒看一次当前 UTC 时分是否匹配；当天跑过就不再重复。
            var lastRunDate = DateOnly.MinValue;
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var today = DateOnly.FromDateTime(now);
                if (now.Hour == daily.Hour
                    && now.Minute == daily.Minute
                    && lastRunDate != today)
                {
                    lastRunDate = today;
                    try
                    {
                        var ran = await _runner.RunAsync(config, "schedule", database, ct).ConfigureAwait(false);
                        if (!ran)
                            Console.WriteLine($"[{name}/{database}] 跳过本次备份：已有备份正在运行");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{name}/{database}] 备份异常: {ex.Message}");
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
