using System.Text.Json.Serialization;

namespace BackDatabase.Config;

/// <summary>
/// 程序级环境配置（对应 exe 同目录下的 env.conf，JSON 格式）。
/// 与 config/*.conf 的备份任务配置分离：env 管全局能力（如消息推送），conf 管各库备份。
/// </summary>
public sealed class EnvConfig
{
    /// <summary>
    /// HxPush 服务根地址，例如 http://127.0.0.1:5212 或 ws://host:5212/ws。
    /// 为空则禁用推送。
    /// </summary>
    [JsonPropertyName("pushAddr")]
    public string PushAddr { get; set; } = "";

    /// <summary>
    /// HxPush AppKey（须已在服务端登记）。
    /// 为空则禁用推送。
    /// </summary>
    [JsonPropertyName("pushKey")]
    public string PushKey { get; set; } = "";

    /// <summary>
    /// 推送设备 ID（Hwid），用于在推送端区分来源主机。
    /// 为空则回退为本机机器名。
    /// </summary>
    [JsonPropertyName("pushHwid")]
    public string PushHwid { get; set; } = "";

    /// <summary>
    /// 推送消息分组，对应 HxPushMsgModel.MsgGroup。
    /// 为空时回退为 default（与 SDK/服务端默认一致）。
    /// </summary>
    [JsonPropertyName("pushGroup")]
    public string PushGroup { get; set; } = "";

    /// <summary>
    /// Web 配置管理界面的访问口令。
    /// 为空表示不校验（与旧版本行为一致）；非空时所有配置接口需先登录。
    /// 首尾空白有意义，加载时不做 Trim。
    /// </summary>
    [JsonPropertyName("webPassword")]
    public string WebPassword { get; set; } = "";

    /// <summary>推送地址与 AppKey 均非空时才启用消息推送。</summary>
    [JsonIgnore]
    public bool IsPushEnabled =>
        !string.IsNullOrWhiteSpace(PushAddr) && !string.IsNullOrWhiteSpace(PushKey);

    /// <summary>配置了访问口令时才启用 Web 界面登录校验。</summary>
    [JsonIgnore]
    public bool IsWebAuthEnabled => !string.IsNullOrEmpty(WebPassword);
}
