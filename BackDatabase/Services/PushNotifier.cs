using System.Diagnostics.CodeAnalysis;
using BackDatabase.Config;
using BackDatabase.Utils;
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
    private readonly HttpClient? _httpClient;
    private readonly HxPushWebApiClient? _client;
    private readonly string _hwid;
    private readonly string _group;
    private readonly string _disableReason;

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "半裁剪：CreateOptions 含反射回退；HxPush* 已 TrimmerRoot，且 JsonSerializerIsReflectionEnabledByDefault=true。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "非 NativeAOT；self-contained 裁剪场景下允许反射 JSON 回退。")]
    public PushNotifier(EnvConfig env)
    {
        _env = env ?? new EnvConfig();
        _hwid = ResolveHwid(_env.PushHwid);
        // SDK 字段名为 MsgGroup；env 用 pushGroup，空则 default
        _group = string.IsNullOrWhiteSpace(_env.PushGroup) ? "default" : _env.PushGroup.Trim();

        if (!_env.IsPushEnabled)
        {
            _client = null;
            _httpClient = null;
            _disableReason = string.IsNullOrWhiteSpace(_env.PushAddr) && string.IsNullOrWhiteSpace(_env.PushKey)
                ? "未配置 pushAddr/pushKey（请在 exe 同目录放置 env.conf）"
                : string.IsNullOrWhiteSpace(_env.PushAddr)
                    ? "pushAddr 为空"
                    : "pushKey 为空";
            Console.WriteLine($"消息推送未启用: {_disableReason}");
            return;
        }

        try
        {
            // 半裁剪：源生成 + 反射回退的 Options，兼容 HxPushSdk 内部反射序列化
            _httpClient = new HttpClient();
            var baseUri = new Uri(_env.PushAddr.TrimEnd('/') + "/", UriKind.Absolute);
            _client = new HxPushWebApiClient(_httpClient, baseUri, AppJsonContext.CreateOptions());
            _disableReason = "";
            Console.WriteLine(
                $"消息推送已初始化: addr={_client.BaseAddress}, appKey={MaskKey(_env.PushKey)}, hwid={_hwid}, group={_group}");
        }
        catch (Exception ex)
        {
            _client = null;
            _httpClient?.Dispose();
            _httpClient = null;
            _disableReason = $"初始化 HxPush 客户端失败: {ex.Message}";
            Console.WriteLine($"{_disableReason}");
            Console.WriteLine(ex.ToString());
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
        var confName = Path.GetFileName(config.SourceFile);

        if (!IsEnabled || _client is null)
        {
            // 关键：以前这里直接 return，用户看不到“为什么没推送”
            Console.WriteLine(
                $"[推送跳过] 备份失败未推送。原因={_disableReason}；配置={confName}；库={database}；错误={Truncate(reason, 200)}");
            return;
        }

        // 取消时不再发推送
        if (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"[推送跳过] 程序正在退出，取消推送。配置={confName}；库={database}");
            return;
        }

        var schedule = FormatBackupSchedule(config);
        // failAtLocal / failAtUtc 若将来要重新放进 msg，按 AGENTS 顺序规则追加，不要插到现有行中间打乱顺序

        // 失败推送正文行序由用户约定，见 AGENTS.md「备份失败推送正文顺序」。
        // 增减字段可以，禁止擅自重排：备份失败 → 数据库 → 备份计划 → 配置 → 主机 → 类型 → 原因
        var msg =
            $"备份失败\n" +
            $"数据库: {database}\n" +
            $"备份计划: {schedule}\n" +
            $"配置: {confName}\n" +
            $"主机: {config.Host}:{config.Port}\n" +
            $"类型: {config.DbType}\n" +
            $"原因: {Truncate(reason, 800)}";

        Console.WriteLine(
            $"[推送中] 正在发送备份失败通知 -> {_client.BaseAddress} appKey={MaskKey(_env.PushKey)} hwid={_hwid} group={_group} schedule={schedule}");

        try
        {
            var message = new HxPushMsgModel
            {
                ID = Guid.NewGuid().ToString("N"),
                AppKey = _env.PushKey,
                Hwid = _hwid,
                // 对应 SDK/服务端分组字段 MsgGroup
                MsgGroup = _group,
                Msg = msg,
                MsgDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsRead = false,
            };

            var ret = _client.SendMessageAsync(message, cancellationToken).GetAwaiter().GetResult();
            Console.WriteLine($"[推送成功] code={ret.code}, msg={ret.msg}, otherData={ret.otherData}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("[推送取消] 程序退出过程中取消推送");
        }
        catch (HxPushHttpException ex)
        {
            // HTTP 层：404/500、连不上等
            Console.WriteLine(
                $"[推送失败-HTTP] status={(int)ex.StatusCode} ({ex.StatusCode}), body={Truncate(ex.ResponseBody, 500)}");
            Console.WriteLine(ex.ToString());
        }
        catch (HxPushApiException ex)
        {
            // 业务层：AppKey 不存在、参数不合法等
            Console.WriteLine($"[推送失败-API] code={ex.Code}, msg={ex.Message}");
            Console.WriteLine(ex.ToString());
        }
        catch (Exception ex)
        {
            // 网络超时、DNS、连接被拒等
            Console.WriteLine($"[推送失败] {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null)
                Console.WriteLine($"[推送失败-内部] {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            Console.WriteLine(ex.ToString());
        }
    }

    public void Dispose()
    {
        // 注入的 HttpClient 由我们持有；SDK 在注入模式下不会 Dispose 它
        _client?.Dispose();
        _httpClient?.Dispose();
    }

    /// <summary>
    /// 把 conf 的 backtime 格式化为可读计划：间隔分钟 或 每日 UTC 定点。
    /// </summary>
    private static string FormatBackupSchedule(BackupConfig config)
    {
        if (config.IntervalMinutes is { } minutes)
        {
            // 整数分钟不带小数，其它保留原 double 文本
            var text = minutes % 1 == 0
                ? ((long)minutes).ToString()
                : minutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"每隔 {text} 分钟";
        }

        if (config.DailyAtUtc is { } daily)
            return $"每天 {daily.Hour:D2}:{daily.Minute:D2} (UTC)";

        return "未配置/无效";
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

    /// <summary>日志里脱敏 AppKey，避免整串密钥落盘。</summary>
    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "(empty)";
        if (key.Length <= 4)
            return "****";
        return key[..2] + "****" + key[^2..];
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text ?? "";
        return text[..maxLen] + "...";
    }
}
