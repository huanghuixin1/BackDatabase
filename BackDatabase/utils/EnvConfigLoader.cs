using System.Text.Json;
using BackDatabase.Config;

namespace BackDatabase.Utils;

/// <summary>
/// 读取程序根目录下的 env.conf（JSON），映射为 <see cref="EnvConfig"/>。
/// 文件不存在或解析失败时返回默认空配置（推送关闭），不阻断启动。
/// </summary>
public static class EnvConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 从 baseDir/env.conf 加载环境配置。
    /// </summary>
    /// <param name="baseDir">程序根目录（通常为 AppContext.BaseDirectory）</param>
    public static EnvConfig Load(string baseDir)
    {
        var path = Path.Combine(baseDir, "env.conf");
        if (!File.Exists(path))
        {
            Console.WriteLine($"未找到 env.conf（{path}），全局推送未启用。可参考 env.conf.example 创建。");
            return new EnvConfig();
        }

        try
        {
            var json = File.ReadAllText(path);
            var env = JsonSerializer.Deserialize<EnvConfig>(json, JsonOptions) ?? new EnvConfig();
            env.PushAddr = (env.PushAddr ?? "").Trim();
            env.PushKey = (env.PushKey ?? "").Trim();
            env.PushHwid = (env.PushHwid ?? "").Trim();

            if (env.IsPushEnabled)
                Console.WriteLine($"已加载 env.conf：消息推送已启用 -> {env.PushAddr}");
            else
                Console.WriteLine("已加载 env.conf：pushAddr/pushKey 未配全，消息推送未启用。");

            return env;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"解析 env.conf 失败，推送未启用: {ex.Message}");
            return new EnvConfig();
        }
    }
}
