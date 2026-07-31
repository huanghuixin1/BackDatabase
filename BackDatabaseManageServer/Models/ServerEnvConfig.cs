using System.Text.Json;

namespace BackDatabaseManageServer.Models;

public sealed class ServerEnvConfig
{
    public string WebPassword { get; init; } = "";
}

public static class ServerEnvConfigLoader
{
    public static ServerEnvConfig Load(string baseDirectory)
    {
        var path = Path.Combine(baseDirectory, "env.conf");
        if (!File.Exists(path))
            return new ServerEnvConfig();

        try
        {
            return JsonSerializer.Deserialize<ServerEnvConfig>(
                       File.ReadAllText(path),
                       new JsonSerializerOptions(JsonSerializerDefaults.Web))
                   ?? new ServerEnvConfig();
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"env.conf 格式错误，已按未配置口令处理: {ex.Message}");
            return new ServerEnvConfig();
        }
    }
}
