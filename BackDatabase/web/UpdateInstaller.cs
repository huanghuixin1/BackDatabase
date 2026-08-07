using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;

namespace BackDatabase.Web;

internal static class UpdateInstaller
{
    private const long MaxPackageBytes = 500L * 1024 * 1024;

    public static async Task StageAndLaunchAsync(string baseDir, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0 || file.Length > MaxPackageBytes)
            throw new InvalidDataException("更新包为空或超过 500MB。 ");
        if (!string.Equals(Path.GetExtension(file.FileName), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包必须是 zip 文件。 ");

        var updateRoot = Path.Combine(baseDir, ".updates");
        Directory.CreateDirectory(updateRoot);
        var workDir = Path.Combine(updateRoot, Guid.NewGuid().ToString("N"));
        var zipPath = workDir + ".zip";
        Directory.CreateDirectory(workDir);
        await using (var output = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await file.CopyToAsync(output, cancellationToken);

        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var relative = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(relative) || IsProtected(relative)) continue;
                var destination = Path.GetFullPath(Path.Combine(workDir, relative));
                var root = Path.GetFullPath(workDir) + Path.DirectorySeparatorChar;
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("更新包包含非法路径。 ");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
            }

            var sourceDir = ResolveContentRoot(workDir);
            if (!Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).Any())
                throw new InvalidDataException("更新包中没有程序文件。 ");
            LaunchUpdater(baseDir, sourceDir, workDir);
        }
        catch
        {
            try { Directory.Delete(workDir, true); } catch { }
            throw;
        }
        finally
        {
            try { File.Delete(zipPath); } catch { }
        }
    }

    private static bool IsProtected(string relative)
    {
        var path = relative.TrimStart('/');
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(part => part.Equals("config", StringComparison.OrdinalIgnoreCase)
                                 || part.Equals(".updates", StringComparison.OrdinalIgnoreCase)
                                 || part.Equals("logs", StringComparison.OrdinalIgnoreCase))
            || parts.LastOrDefault()?.Equals("env.conf", StringComparison.OrdinalIgnoreCase) == true
            || path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pid", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveContentRoot(string workDir)
    {
        var files = Directory.GetFiles(workDir);
        var dirs = Directory.GetDirectories(workDir);
        return files.Length == 0 && dirs.Length == 1 ? dirs[0] : workDir;
    }

    private static void LaunchUpdater(string baseDir, string sourceDir, string workDir)
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前进程。 ");
        var commandLine = Environment.GetCommandLineArgs();
        var args = commandLine[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? commandLine
            : commandLine.Skip(1).ToArray();
        var scriptPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(Path.GetTempPath(), $"back-update-{Guid.NewGuid():N}.ps1")
            : Path.Combine(Path.GetTempPath(), $"back-update-{Guid.NewGuid():N}.sh");
        var script = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? BuildWindowsScript(baseDir, sourceDir, workDir, processPath, args, scriptPath)
            : BuildLinuxScript(baseDir, sourceDir, workDir, processPath, args, scriptPath);
        File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(true));

        var startInfo = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo("/bin/bash") { UseShellExecute = false, CreateNoWindow = true };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
        }
        startInfo.ArgumentList.Add(scriptPath);
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动更新脚本。 ");
    }

    private static string BuildWindowsScript(string baseDir, string sourceDir, string workDir, string exe, string[] args, string scriptPath)
    {
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        var argList = string.Join(",", args.Select(Q));
        var log = Q(Path.Combine(baseDir, "update.log"));
        var startCommand = args.Length == 0
            ? $"Start-Process -FilePath {Q(exe)} -WorkingDirectory {Q(baseDir)}"
            : $"Start-Process -FilePath {Q(exe)} -ArgumentList @({argList}) -WorkingDirectory {Q(baseDir)}";
        return $"$ErrorActionPreference='Stop'\r\n$logPath={log}\r\nStart-Transcript -Path $logPath -Append | Out-Null\r\ntry {{\r\n  Wait-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue\r\n  $deadline=(Get-Date).AddSeconds(60)\r\n  do {{\r\n    try {{ Copy-Item -Path {Q(sourceDir + "\\*")} -Destination {Q(baseDir)} -Recurse -Force -ErrorAction Stop; $copied=$true }} catch {{ $copied=$false; Start-Sleep -Seconds 1 }}\r\n  }} while (-not $copied -and (Get-Date) -lt $deadline)\r\n  if (-not $copied) {{ throw '程序文件复制失败，可能仍有进程占用。' }}\r\n  {startCommand}\r\n}} catch {{ Write-Host ($_ | Out-String); exit 1 }} finally {{\r\n  Stop-Transcript | Out-Null\r\n  Remove-Item -LiteralPath {Q(workDir)} -Recurse -Force -ErrorAction SilentlyContinue\r\n  Remove-Item -LiteralPath {Q(scriptPath)} -Force -ErrorAction SilentlyContinue\r\n}}\r\n";
    }
    private static string BuildLinuxScript(string baseDir, string sourceDir, string workDir, string exe, string[] args, string scriptPath)
    {
        static string Q(string value) => "'" + value.Replace("'", "'\\''") + "'";
        var argList = string.Join(" ", args.Select(Q));
        return $"#!/usr/bin/env bash\nset -e\nwhile kill -0 {Environment.ProcessId} 2>/dev/null; do sleep 0.2; done\ncp -a {Q(sourceDir + "/.")} {Q(baseDir + "/")}\nchmod +x {Q(exe)} 2>/dev/null || true\ncd {Q(baseDir)}\nnohup {Q(exe)} {argList} >/dev/null 2>&1 &\nrm -rf {Q(workDir)}\nrm -f {Q(scriptPath)}\n";
    }
}