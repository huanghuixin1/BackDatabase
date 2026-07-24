using BackDatabase.Config;

namespace BackDatabase.Services;

/// <summary>
/// 备份任务调度器。
/// 每个 <see cref="BackupConfig"/> 对应一个长期运行的后台任务：
/// - 间隔模式：立即执行一次，然后每隔 N 分钟再执行（对齐 Go 的 for { invoke; sleep }）；
/// - 每日模式：约每 40 秒检查一次 UTC 时分，到点且当天未跑过则执行。
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
    /// 为每个配置启动一个独立后台循环（互不阻塞）。
    /// </summary>
    public void StartAll(IEnumerable<BackupConfig> configs)
    {
        foreach (var config in configs)
        {
            // 闭包捕获当前 config，避免循环变量问题
            var captured = config;
            _workers.Add(Task.Run(() => RunLoopAsync(captured, _cts.Token), _cts.Token));
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
    /// 单个配置的调度主循环。
    /// </summary>
    private async Task RunLoopAsync(BackupConfig config, CancellationToken ct)
    {
        var name = Path.GetFileName(config.SourceFile);
        // 日志里打印解析后的绝对保存路径，方便排查
        var saveDir = config.ResolveSaveDir(AppContext.BaseDirectory);
        Console.WriteLine(
            $"数据库正在备份准备中: {config.Host}。保存路径为:{saveDir} 最大保存数量：{config.MaxFiles} 配置:{name}");
        Console.WriteLine($"数据库类型 {config.DbType}");

        if (config.IntervalMinutes is { } minutes)
        {
            // ---------- 间隔模式 ----------
            // 与 Go 版一致：先备份，再 sleep，再备份……
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _runner.Run(config);
                }
                catch (Exception ex)
                {
                    // 单次异常不退出循环，继续下一轮
                    Console.WriteLine($"[{name}] 备份异常: {ex.Message}");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(minutes), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break; // 收到停止信号
                }
            }
        }
        else if (config.DailyAtUtc is { } daily)
        {
            // ---------- 每日定点模式（UTC）----------
            // Go 版每 40 秒看一次当前 UTC 时分是否匹配；
            // 这里额外记录 lastRunDate，避免同一分钟内被轮询多次而重复备份。
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
                        _runner.Run(config);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{name}] 备份异常: {ex.Message}");
                    }
                }

                try
                {
                    // 约 40 秒轮询一次，对齐 Go：time.Sleep(40 * time.Second)
                    await Task.Delay(TimeSpan.FromSeconds(40), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        else
        {
            // 理论上 ConfigLoader 已保证二选一，这里兜底
            Console.WriteLine($"[{name}] 无效的 backtime 配置，跳过");
        }
    }
}
