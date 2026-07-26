using System.Text;
using BackDatabase.Config;
using BackDatabase.Services;
using BackDatabase.Utils;

// 注册代码页提供程序，便于 Windows 下将 mysqldump 的 GBK 错误信息尝试解码为可读中文
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// 版本号
var version = "1.3";
Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd_HH:mm:ss} 当前版本: {version}, 服务开启成功...");

// 与 Go 版一致：以可执行文件所在目录为根目录（不是当前工作目录）
// 这样无论从哪里启动，config、备份输出路径都相对 exe 所在位置
var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
var configDir = Path.Combine(baseDir, "config");

Console.WriteLine($"运行地址 {configDir}");

// 启动时先读全局 env.conf（推送地址/AppKey 等），再加载各库备份 conf
var env = EnvConfigLoader.Load(baseDir);

// 加载 config 目录下所有 *.conf（不加载 .example）
var configs = ConfigLoader.LoadAll(configDir);
if (configs.Count == 0)
{
    Console.WriteLine("未找到任何 .conf 配置文件，请将配置放到程序目录下的 config/ 中（可参考 config/*.conf.example）。");
    Console.WriteLine("进程结束");
    return 1; // 无配置时直接退出，避免空转
}

// 备份失败推送（env 未配 pushAddr/pushKey 时内部为空操作）
using var pushNotifier = new PushNotifier(env);

// 备份执行器：真正调用 mysqldump / pg_dump
var runner = new BackupRunner(baseDir, pushNotifier: pushNotifier);
// 调度器：按间隔分钟或每日 UTC 时刻循环触发
var scheduler = new BackupScheduler(runner);

// Ctrl+C / 控制台关闭信号：取消所有调度循环，优雅退出
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 阻止进程被立刻杀掉，让我们有机会收尾
    Console.WriteLine("收到退出信号，正在停止...");
    scheduler.Stop();
};

// 每个 .conf 启动一个后台任务（对应 Go 的 go startBackInterval）
scheduler.StartAll(configs);
// 阻塞直到全部任务结束（通常是收到取消信号后）
await scheduler.WaitAllAsync();

Console.WriteLine("进程结束");
return 0;
