using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BackDatabase.Services.Strategies;

/// <summary>本机 mysqldump 客户端的类型种类。命令行选项兼容性依客户端而异，
/// 与 conf 里的 dbType 无关（同一台机器只有一个 mysqldump）。</summary>
public enum MySqlDumpClientKind
{
    /// <summary>无法探测（工具缺失或输出格式异常）。调用方应避免使用任何版本相关参数。</summary>
    Unknown,

    /// <summary>MySQL 5.x 客户端（只认 --skip-ssl，不认 --ssl-mode / --column-statistics）。</summary>
    MySql5,

    /// <summary>MySQL 8.0+ 客户端（认 --ssl-mode / --column-statistics，不再认 --skip-ssl）。</summary>
    MySql8,

    /// <summary>MariaDB 客户端（认 --skip-ssl，不认 --ssl-mode；不查询 COLUMN_STATISTICS）。</summary>
    MariaDb,
}

/// <summary>
/// 探测本机 PATH 中 mysqldump 的版本类型，进程内只探测一次并缓存结果。
/// 探测失败返回 <see cref="MySqlDumpClientKind.Unknown"/>，不影响备份主流程（备份时会再报错）。
/// </summary>
public sealed class MySqlDumpClientDetector
{
    private readonly object _lock = new();
    private MySqlDumpClientKind? _kind;

    public MySqlDumpClientKind Detect()
    {
        if (_kind.HasValue)
            return _kind.Value;
        lock (_lock)
        {
            _kind ??= Probe();
            return _kind.Value;
        }
    }

    private static MySqlDumpClientKind Probe()
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "mysqldump";
            process.StartInfo.ArgumentList.Add("--version");
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            if (!process.Start())
                return MySqlDumpClientKind.Unknown;

            var output = process.StandardOutput.ReadToEnd() + Environment.NewLine + process.StandardError.ReadToEnd();
            process.WaitForExit();

            // MariaDB 的 --version 输出形如 “mysqldump  Ver 10.5.23-MariaDB ...”
            if (output.Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"检测到 mysqldump 客户端类型: MariaDB");
                return MySqlDumpClientKind.MariaDb;
            }

            // MySQL 输出形如 “mysqldump  Ver 8.0.36 for Win64 ...”；5.x 输出 Ver 5.7.x
            var match = Regex.Match(output, @"Ver\s+(\d+)", RegexOptions.IgnoreCase);
            if (!match.Success)
                return MySqlDumpClientKind.Unknown;

            var major = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var kind = major >= 8 ? MySqlDumpClientKind.MySql8 : MySqlDumpClientKind.MySql5;
            Console.WriteLine($"检测到 mysqldump 客户端类型: {kind}");
            return kind;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"无法探测 mysqldump 客户端版本: {ex.Message}");
            return MySqlDumpClientKind.Unknown;
        }
    }
}