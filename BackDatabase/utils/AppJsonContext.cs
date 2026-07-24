using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BackDatabase.Config;

namespace BackDatabase.Utils;

/// <summary>
/// System.Text.Json 源生成上下文 + 半裁剪兼容的 Options 工厂。
/// <para>
/// 策略：
/// - 本项目已知类型优先走源生成（编译期安全、更快）；
/// - 通过 <see cref="JsonTypeInfoResolver.Combine"/> 回退到 <see cref="DefaultJsonTypeInfoResolver"/>，
///   配合 csproj 的 <c>JsonSerializerIsReflectionEnabledByDefault=true</c> 与 TrimmerRoot，
///   第三方 DLL 内部反射序列化也能工作。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    WriteIndented = false)]
// 本地配置
[JsonSerializable(typeof(EnvConfig))]
// HxPush 发送消息 / 响应 envelope（本项目侧也会直接用到）
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(JsonElement))]
internal partial class AppJsonContext : JsonSerializerContext
{
    /// <summary>
    /// 供 HxPushWebApiClient 等第三方代码使用的 Options：
    /// 源生成优先，未登记类型回退反射（需 Publish 时开启反射 + root 第三方程序集）。
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "半裁剪有意保留反射回退；HxPush* 已 TrimmerRoot，且 JsonSerializerIsReflectionEnabledByDefault=true。")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "非 NativeAOT；self-contained 裁剪场景下允许反射 JSON 回退。")]
    public static JsonSerializerOptions CreateOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            // 顺序：先源生成，再反射；SDK 里 Serialize(object) 也能落到反射 resolver
            TypeInfoResolver = JsonTypeInfoResolver.Combine(
                Default,
                new DefaultJsonTypeInfoResolver()),
        };
    }
}
