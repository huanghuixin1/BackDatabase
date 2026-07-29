using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BackDatabase.Web;

/// <summary>
/// 管理界面访问口令校验器。口令来自 env.conf 的 <c>webPassword</c>。
/// <para>
/// 约定：
/// - 口令为空 → 不校验，行为与旧版本完全一致；
/// - 口令非空 → 所有 /api 接口需先登录，会话仅保存在进程内存，重启即失效；
/// - 连续输错达到上限后短暂锁定，避免本机脚本暴力猜测。
/// </para>
/// </summary>
public sealed class WebAuthGuard
{
    /// <summary>会话 Cookie 名称（HttpOnly，前端不读取）。</summary>
    public const string CookieName = "backdb_session";

    /// <summary>连续输错多少次后锁定。</summary>
    private const int MaxFailures = 5;

    /// <summary>锁定时长。</summary>
    private static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(1);

    /// <summary>单个会话的有效期（每次校验成功后顺延）。</summary>
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);

    private readonly byte[] _passwordBytes;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);
    private readonly object _failureLock = new();
    private int _failures;
    private DateTimeOffset _lockedUntil;

    /// <param name="password">env.conf 中的 webPassword，null 或空表示不启用校验。</param>
    public WebAuthGuard(string? password)
    {
        var value = password ?? "";
        _passwordBytes = Encoding.UTF8.GetBytes(value);
        Required = value.Length > 0;
    }

    /// <summary>是否已配置访问口令；false 时所有请求直接放行。</summary>
    public bool Required { get; }

    /// <summary>会话有效期，供调用方设置 Cookie 过期时间。</summary>
    public TimeSpan Lifetime => SessionLifetime;

    /// <summary>校验会话 token 是否有效；有效则顺延过期时间（滑动过期）。</summary>
    public bool IsValidSession(string? token)
    {
        if (!Required)
            return true;
        if (string.IsNullOrEmpty(token) || !_sessions.TryGetValue(token, out var expiresAt))
            return false;

        var now = DateTimeOffset.UtcNow;
        if (expiresAt <= now)
        {
            _sessions.TryRemove(token, out _);
            return false;
        }

        _sessions[token] = now + SessionLifetime;
        return true;
    }

    /// <summary>校验口令；成功时返回新会话 token。</summary>
    public LoginResult Login(string? password)
    {
        if (!Required)
            return new LoginResult(false, null, "当前未配置访问口令，无需登录。");

        lock (_failureLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (_lockedUntil > now)
            {
                var seconds = (int)Math.Ceiling((_lockedUntil - now).TotalSeconds);
                return new LoginResult(false, null, $"口令错误次数过多，请 {seconds} 秒后重试。");
            }

            // 固定时间比较，避免通过响应耗时逐字节试探口令
            var input = Encoding.UTF8.GetBytes(password ?? "");
            if (!CryptographicOperations.FixedTimeEquals(input, _passwordBytes))
            {
                _failures++;
                if (_failures < MaxFailures)
                    return new LoginResult(false, null, "访问口令不正确。");

                _failures = 0;
                _lockedUntil = now + LockoutWindow;
                return new LoginResult(false, null,
                    $"口令错误次数过多，已锁定 {LockoutWindow.TotalSeconds:0} 秒。");
            }

            _failures = 0;
        }

        return new LoginResult(true, CreateSession(), "登录成功。");
    }

    /// <summary>退出登录，作废指定会话。</summary>
    public void Logout(string? token)
    {
        if (!string.IsNullOrEmpty(token))
            _sessions.TryRemove(token, out _);
    }

    /// <summary>生成随机会话 token，并顺手清理已过期会话。</summary>
    private string CreateSession()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _sessions)
        {
            if (pair.Value <= now)
                _sessions.TryRemove(pair.Key, out _);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _sessions[token] = now + SessionLifetime;
        return token;
    }

    /// <summary>登录结果：是否成功、会话 token（仅成功时非空）、给用户看的提示。</summary>
    public sealed record LoginResult(bool Success, string? Token, string Message);
}
