using System.Text.Json;
using BackDatabase.Config;

namespace BackDatabase.Utils;

/// <summary>
/// 读取程序根目录下的 env.conf（JSON），映射为 <see cref="EnvConfig"/>。
/// 文件不存在或解析失败时返回默认空配置（推送关闭），不阻断启动。
/// 使用 <see cref="AppJsonContext"/> 源生成，兼容 PublishTrimmed。
/// </summary>
public static class EnvConfigLoader
{
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
            // 去掉 // 与 /* */ 注释、以及行尾无引号内的说明，保持简单：只读标准 JSON。
            // 若需要注释，可用 env 中纯 JSON；样例文件本身无注释。
            var json = File.ReadAllText(path);

            // 裁剪发布下禁止反射反序列化，必须走源生成 TypeInfo
            var env = JsonSerializer.Deserialize(json, AppJsonContext.Default.EnvConfig) ?? new EnvConfig();
            env.PushAddr = (env.PushAddr ?? "").Trim();
            env.PushKey = (env.PushKey ?? "").Trim();
            env.PushHwid = (env.PushHwid ?? "").Trim();
            env.PushGroup = (env.PushGroup ?? "").Trim();

            if (env.IsPushEnabled)
            {
                var hwid = string.IsNullOrWhiteSpace(env.PushHwid) ? "(空，将用机器名)" : env.PushHwid;
                var group = string.IsNullOrWhiteSpace(env.PushGroup) ? "default" : env.PushGroup;
                Console.WriteLine(
                    $"已加载 env.conf：消息推送已启用 -> {env.PushAddr}, pushHwid={hwid}, pushGroup={group}");
            }
            else
            {
                Console.WriteLine(
                    $"已加载 env.conf，但推送未启用：pushAddr={(string.IsNullOrWhiteSpace(env.PushAddr) ? "空" : "已填")}, " +
                    $"pushKey={(string.IsNullOrWhiteSpace(env.PushKey) ? "空" : "已填")}");
            }

            return env;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"解析 env.conf 失败，推送未启用: {ex.Message}");
            return new EnvConfig();
        }
    }
}
