using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BackDatabase.Web;

/// <summary>
/// 自重启辅助：在后台拉起一个与当前进程同目录、同参数的新实例，然后退出当前进程。
/// 放在 Web 命名空间下，便于 ConfigWebService 直接调用。
/// </summary>
internal static class AppEntry
{
    /// <summary>
    /// 自重启：后台拉起新实例后退出当前进程。
    /// <para>
    /// 关键点：新进程必须与当前进程「脱钩」（detached），否则当前进程退出时，
    /// 宿主（终端/服务管理器）会把作为子进程的新进程一并回收，导致重启后找不到进程。
    /// </para>
    /// - Windows：用 <c>cmd /c start</c> 启动，start 会创建独立进程组；
    /// - Linux/macOS：直接 <c>Process.Start</c>，子进程在父进程退出后由 init 接管。
    /// </summary>
    public static void RestartSelf()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            Console.WriteLine("重启失败：无法定位当前可执行文件路径。");
            return;
        }

        // 复用原始命令行参数（去掉程序名本身）
        var args = Environment.GetCommandLineArgs();
        Console.WriteLine($"触发重启：将启动新进程 {exePath}（参数 {args.Length - 1} 个），然后退出当前进程。");

        try
        {
            StartDetached(exePath, args, AppContext.BaseDirectory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动新进程失败，当前进程未退出: {ex.Message}");
            throw;
        }

        // 给新进程一点点启动时间，再让旧进程退出（旧进程退出后端口才能被新进程绑定）
        Thread.Sleep(500);
        Environment.Exit(0);
    }

    /// <summary>以脱离当前进程组的方式启动新实例。</summary>
    private static void StartDetached(string exePath, string[] args, string workingDir)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // 使用 Shell 启动一个新 cmd，先延迟 1 秒再通过 start 启动子进程，确保子进程脱离父进程树
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // UseShellExecute 必须为 true 才能让内部的 start 正常工作并脱离父进程
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
                // 通过 timeout 等待 1 秒后再执行 start，使用 && 确保前一步成功后再执行。
                // 整体包装在引号内，以防参数中出现空格等特殊字符。
                Arguments = $"/c \"timeout /t 1 && start \"\" /B \"{exePath}\" {string.Join(" ", args.Skip(1))}\""
            };
            Process.Start(startInfo);
            return;
        }

        // Linux/macOS：父进程退出后子进程由 init/systemd 接管，无需特殊处理
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        for (var i = 1; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);
        Process.Start(psi);
    }
}
