using BackDatabase.Config;
using HxPushApp.models.Message;
using HxPushSdk;

namespace BackDatabase.Services;

/// <summary>
/// 备份失败时的消息推送封装（HxPushSdk）。
/// pushAddr/pushKey 未配置时为无操作；推送异常只打日志，不影响备份主流程。
/// </summary>
public sealed class PushNotifier : IDisposable
{
    private readonly EnvConfig _env;
    private readonly HxPushWebApiClient? _client;
    private readonly string _hwid;

    public PushNotifier(EnvConfig env)
    {
        _env = env ?? new EnvConfig();
        _hwid = ResolveHwid(_env.PushHwid);

        if (!_env.IsPushEnabled)
        {
            _client = null;
            return;
        }

        try
        {
            // 字符串构造：SDK 自持 HttpClient，Dispose 时释放
            _client = new HxPushWebApiClient(_env.PushAddr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"初始化 HxPush 客户端失败，推送禁用: {ex.Message}");
            _client = null;
        }
    }

    /// <summary>是否已具备可推送客户端。</summary>
    public bool IsEnabled => _client is not null && _env.IsPushEnabled;

    /// <summary>
    /// 发送备份失败通知。失败不抛出，避免拖垮调度循环。
    /// </summary>
    public void NotifyBackupFailure(
        BackupConfig config,
        string database,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || _client is null)
            return;

        // 取消时不再发推送
        if (cancellationToken.IsCancellationRequested)
            return;

        var confName = Path.GetFileName(config.SourceFile);
        var msg =
            $"[BackDatabase] 备份失败\n" +
            $"时间(UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}\n" +
            $"配置: {confName}\n" +
            $"类型: {config.DbType}\n" +
            $"主机: {config.Host}:{config.Port}\n" +
            $"数据库: {database}\n" +
            $"原因: {Truncate(reason, 800)}";

        try
        {
            var message = new HxPushMsgModel
            {
                ID = Guid.NewGuid().ToString("N"),
                AppKey = _env.PushKey,
                Hwid = _hwid,
                Msg = msg,
                MsgDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsRead = false,
            };

            var ret = _client.SendMessageAsync(message, cancellationToken).GetAwaiter().GetResult();
            Console.WriteLine($"备份失败推送已发送: code={ret.code}, msg={ret.msg}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 程序退出过程中取消推送，正常
        }
        catch (Exception ex)
        {
            // 推送失败不影响备份重试/调度
            Console.WriteLine($"备份失败推送异常: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
    }

    /// <summary>
    /// Hwid 优先使用配置的 pushHwid，未配置时回退为机器名，便于在推送端区分来源主机。
    /// </summary>
    private static string ResolveHwid(string? configuredHwid)
    {
        if (!string.IsNullOrWhiteSpace(configuredHwid))
            return configuredHwid.Trim();

        try
        {
            var name = Environment.MachineName?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch
        {
            // ignore
        }

        return "BackDatabase";
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text ?? "";
        return text[..maxLen] + "...";
    }
}
