using System.Text.Json;
using System.Text.Json.Serialization;
using BackDatabase.Config;
using HxPushApp.models.Message;
using HxPushModel.HttpRequest;

namespace BackDatabase.Utils;

/// <summary>
/// System.Text.Json 源生成上下文。
/// 裁剪（PublishTrimmed）后禁用反射序列化，所有会被 Serialize/Deserialize 的类型都必须登记在此。
/// <para>
/// 编译期防护约定：
/// 1. 业务代码优先 <c>JsonSerializer.Deserialize(json, AppJsonContext.Default.Xxx)</c>；
/// 2. 不要写 <c>JsonSerializer.Deserialize&lt;T&gt;(json)</c> 反射泛型重载；
/// 3. 项目已开 <c>EnableTrimAnalyzer</c>，并把 IL2026/IL3050 当错误——漏用反射会直接编不过。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    WriteIndented = false)]
// 本地配置
[JsonSerializable(typeof(EnvConfig))]
// HxPush 发送消息 / 响应 envelope
[JsonSerializable(typeof(HxPushMsgModel))]
[JsonSerializable(typeof(HxHttpResModel))]
// SDK 内部可能对列表/字典再序列化
[JsonSerializable(typeof(List<HxPushMsgModel>))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
// HxHttpResModel.msg 为 object，裁剪下用 JsonElement 承接动态 JSON
[JsonSerializable(typeof(JsonElement))]
internal partial class AppJsonContext : JsonSerializerContext
{
    /// <summary>
    /// 供 HxPushWebApiClient 使用的 Options：TypeInfoResolver 指向本源生成上下文，避免反射。
    /// </summary>
    public static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            TypeInfoResolver = Default,
        };
    }
}
