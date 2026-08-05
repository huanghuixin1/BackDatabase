using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace BackDatabase.Web;

internal static class AppEntry
{
    private const string RestartParentPidArgumentPrefix = "--backdatabase-restart-parent-pid=";
    private static int restartRequested;

    public static void RestartSelf()
    {
        if (Interlocked.Exchange(ref restartRequested, 1) != 0)
        {
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            Console.WriteLine("重启失败：无法定位当前可执行文件路径。");
            Interlocked.Exchange(ref restartRequested, 0);
            return;
        }

        var processArgs = Environment.GetCommandLineArgs();
        try
        {
            StartDetached(exePath, processArgs, AppContext.BaseDirectory, Environment.ProcessId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动新进程失败，当前进程未退出: {ex.Message}");
            Interlocked.Exchange(ref restartRequested, 0);
            throw;
        }

        Environment.Exit(0);
    }

    private static void StartDetached(string exePath, string[] processArgs, string workingDir, int parentPid)
    {
        var restartArgument = RestartParentPidArgumentPrefix + parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir,
                Arguments = $"/c \"start \"\" /B \"{exePath}\" {string.Join(" ", processArgs.Skip(1))} {restartArgument}\""
            };
            Process.Start(startInfo);
            return;
        }

        var startInfoLinux = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        for (var i = 1; i < processArgs.Length; i++)
        {
            startInfoLinux.ArgumentList.Add(processArgs[i]);
        }
        startInfoLinux.ArgumentList.Add(restartArgument);
        Process.Start(startInfoLinux);
    }

    public static string[] WaitForRestartParentIfRequested(string[] args)
    {
        var forwardedArgs = new List<string>(args.Length);
        int? parentPid = null;

        foreach (var arg in args)
        {
            if (arg.StartsWith(RestartParentPidArgumentPrefix, StringComparison.Ordinal) &&
                int.TryParse(arg[RestartParentPidArgumentPrefix.Length..], out var parsedPid) &&
                parsedPid > 0)
            {
                parentPid = parsedPid;
                continue;
            }

            forwardedArgs.Add(arg);
        }

        if (parentPid is null || parentPid == Environment.ProcessId)
        {
            return forwardedArgs.ToArray();
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid.Value);
                if (parent.HasExited)
                {
                    break;
                }
            }
            catch (ArgumentException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            Thread.Sleep(100);
        }

        return forwardedArgs.ToArray();
    }
}
