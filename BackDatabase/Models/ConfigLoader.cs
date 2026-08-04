using System.Globalization;
using System.Text.RegularExpressions;

namespace BackDatabase.Config;

/// <summary>
/// 解析 config 目录下的 .conf 文件。
/// 格式兼容原 Go 使用的 robfig/config：键=值，# 或 ; 为注释。
/// 示例行：backtime=2 # 每隔多少分钟备份一次
/// </summary>
public static class ConfigLoader
{
    /// <summary>
    /// 匹配「键 = 值」行。
    /// 组1=键名，组2=值（后面可能还有 # 行内注释，解析时再裁剪）。
    /// </summary>
    private static readonly Regex KeyValue = new(
        @"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*(?:#.*)?$",
        RegexOptions.Compiled);

    /// <summary>
    /// 加载目录下所有 .conf 文件，每个文件对应一个 <see cref="BackupConfig"/>。
    /// 某个文件解析失败只打印错误，不影响其它文件。
    /// </summary>
    /// <param name="configDir">配置目录绝对路径</param>
    public static IReadOnlyList<BackupConfig> LoadAll(string configDir)
    {
        if (!Directory.Exists(configDir))
        {
            // 目录不存在时自动创建，方便首次部署
            Directory.CreateDirectory(configDir);
            Console.WriteLine($"配置目录不存在，已创建: {configDir}");
            return Array.Empty<BackupConfig>();
        }

        var results = new List<BackupConfig>();
        // 只认 *.conf，样例文件 *.conf.example 不会被加载
        foreach (var file in Directory.EnumerateFiles(configDir, "*.conf")
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                results.Add(ParseFile(file));
                Console.WriteLine($"已加载配置: {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载配置失败 {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return results;
    }

    /// <summary>
    /// 解析单个 .conf 文件为 <see cref="BackupConfig"/>。
    /// </summary>
    public static BackupConfig ParseFile(string path)
    {
        // 键名不区分大小写，兼容 User / user 等写法
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            // 空行、整行注释直接跳过
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            var m = KeyValue.Match(line);
            if (!m.Success)
                continue;

            // 再裁一次行内 # 注释（值里允许空格，但不能含未转义的 #）
            var value = m.Groups[2].Value.Trim();
            var hash = value.IndexOf('#');
            if (hash >= 0)
                value = value[..hash].Trim();

            map[m.Groups[1].Value] = value;
        }

        // 本地小函数：取配置项，空则用默认值
        string Get(string key, string defaultValue = "") =>
            map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : defaultValue;

        var dbType = Get("dbType", "mysql").ToLowerInvariant();
        var dbsStr = Get("dbs");
        // dbs=ss,hhx → ["ss", "hhx"]
        var databases = dbsStr
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToArray();

        // maxfiles 非法或 <=0 时使用默认 180（与 Go 版一致）
        var maxFiles = 180;
        if (int.TryParse(Get("maxfiles", "180"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mf) && mf > 0)
            maxFiles = mf;

        // ---------- 解析 backtime ----------
        // 1) 能解析为 float 且 >0 → 间隔分钟模式
        // 2) 否则按 HH:mm 解析 → 每日 UTC 定点模式
        double? intervalMinutes = null;
        (int Hour, int Minute)? dailyAtUtc = null;
        var backtime = Get("backtime", "60");
        if (double.TryParse(backtime, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
        {
            intervalMinutes = minutes;
        }
        else
        {
            var parts = backtime.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length >= 2
                && int.TryParse(parts[0], out var hour)
                && int.TryParse(parts[1], out var minute)
                && hour is >= 0 and <= 23
                && minute is >= 0 and <= 59)
            {
                dailyAtUtc = (hour, minute);
            }
            else
            {
                throw new FormatException($"backtime 无效: {backtime}（应为数字分钟或 HH:mm）");
            }
        }

        return new BackupConfig
        {
            SourceFile = path,
            DbType = dbType,
            Host = Get("host", "127.0.0.1"),
            // 未写 port 时按数据库类型给默认端口
            Port = Get("port", dbType == "pgsql" ? "5432" : "3306"),
            User = Get("user", "root"),
            Password = Get("pwd"),
            Databases = databases,
            SaveDirRelative = Get("savedir", "/"),
            MaxFiles = maxFiles,
            IntervalMinutes = intervalMinutes,
            DailyAtUtc = dailyAtUtc,
            DbSchedules = ParseDbSchedules(Get("dbtimes", ""), databases),
        };
    }

    /// <summary>
    /// 解析 dbtimes 配置项，格式：db1:60,db2:02:00,db3:30
    /// 每个 entry 形如「库名:计划」，计划同 backtime 规则（数字=分钟 / HH:mm=每日定点 UTC）。
    /// 仅保留在 databases 列表里的库；忽略解析失败的 entry（不抛错，保持其它库可用）。
    /// </summary>
    private static IReadOnlyDictionary<string, DbSchedule> ParseDbSchedules(string raw, IReadOnlyList<string> databases)
    {
        var result = new Dictionary<string, DbSchedule>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        var dbSet = new HashSet<string>(databases, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = entry.IndexOf(':');
            if (colon <= 0 || colon >= entry.Length - 1)
                continue; // 没有 ":" 或分隔位置错误，跳过

            var db = entry[..colon].Trim();
            var time = entry[(colon + 1)..].Trim();
            if (!dbSet.Contains(db))
                continue; // 不在 dbs 列表里的库，忽略

            var schedule = ParseSingleSchedule(time);
            if (schedule.IsInvalid)
                continue; // 计划无效，跳过该库（沿用任务级默认）

            result[db] = schedule;
        }

        return result;
    }

    /// <summary>把单个时间字符串解析为计划。数字→间隔分钟；HH:mm→每日定点。</summary>
    internal static DbSchedule ParseSingleSchedule(string time)
    {
        if (double.TryParse(time, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
            return new DbSchedule(minutes, null);

        var parts = time.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length >= 2
            && int.TryParse(parts[0], out var hour)
            && int.TryParse(parts[1], out var minute)
            && hour is >= 0 and <= 23
            && minute is >= 0 and <= 59)
            return new DbSchedule(null, (hour, minute));

        return default; // 无效
    }
}
