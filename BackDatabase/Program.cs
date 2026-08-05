using BackDatabase;
using System.Text;
using BackDatabase.Config;
using BackDatabase.Services;
using BackDatabase.Utils;
using BackDatabase.Web; // 添加 Web 命名空间，以便使用 ConfigWebService
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting; // UseUrls 扩展方法所在命名空间

// 注册代码页提供程序，便于 Windows 下将 mysqldump 的 GBK 错误信息尝试解码为可读中文
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var applicationArgs = AppEntry.WaitForRestartParentIfRequested(args);

// 版本号
var version = AppInfo.Version;
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
    Console.WriteLine("备份调度暂未启动，可通过 Web 管理界面创建配置后重启程序。");
}

// 备份失败推送（env 未配 pushAddr/pushKey 时内部为空操作）
using var pushNotifier = new PushNotifier(env);

// 备份运行注册表：记录每个配置最近一次运行的状态与日志，供 Web 展示
var runRegistry = new BackupRunRegistry();

// 备份执行器：真正调用 mysqldump / pg_dump
var runner = new BackupRunner(baseDir, pushNotifier: pushNotifier, registry: runRegistry);
// 调度器：按间隔分钟或每日 UTC 时刻循环触发
var scheduler = new BackupScheduler(runner);

// 每个 .conf 启动一个后台任务（对应 Go 的 go startBackInterval）
scheduler.StartAll(configs);

// ==== Web 管理界面 ====
const string webUrls = "http://0.0.0.0:5080";
var builder = WebApplication.CreateBuilder(applicationArgs);
builder.WebHost.UseUrls(webUrls);
var app = builder.Build();

ConfigWebService.Configure(app, baseDir, configDir, env.WebPassword, runner, runRegistry);

// Ctrl+C / SIGTERM 由 Kestrel 宿主统一处理，并同步停止原有备份调度。
app.Lifetime.ApplicationStopping.Register(() =>
{
    Console.WriteLine("收到退出信号，正在停止 Web 服务和备份调度...");
    scheduler.Stop();
});

Console.WriteLine($"Web 配置管理界面: {webUrls}");
await app.RunAsync();
await scheduler.WaitAllAsync();

Console.WriteLine("进程结束");
return 0;
