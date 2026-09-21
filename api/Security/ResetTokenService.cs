using System.Security.Cryptography;
using System.Text;

namespace Hamster.Api.Security;

/// <summary>
/// 密码重置令牌的生成与校验。
/// </summary>
/// <remarks>
/// 令牌是 32 字节 CSPRNG 随机串（Base64Url 编码），熵足够高、不存在被字典攻击的风险，
/// 故入库时只做一次 SHA-256 而非 <see cref="PasswordHasher"/> 那样的慢哈希——
/// 目标仅是避免数据库被读走后令牌可直接使用。校验走恒定时间比较。
/// </remarks>
public static class ResetTokenService
{
    /// <summary>令牌随机字节数。</summary>
    private const int TOKEN_BYTES = 32;

    /// <summary>重置链接有效期（分钟）。</summary>
    public const int TOKEN_LIFETIME_MINUTES = 15;

    /// <summary>生成结果。</summary>
    /// <param name="Token">明文令牌，仅用于拼装重置链接，不落库。</param>
    /// <param name="TokenHash">入库用的 SHA-256 哈希（Base64）。</param>
    /// <param name="ExpiresAt">过期时间（UTC）。</param>
    public readonly record struct TokenResult(string Token, string TokenHash, DateTime ExpiresAt);

    /// <summary>
    /// 生成一个新的重置令牌。
    /// </summary>
    /// <returns>明文令牌、入库哈希与过期时间。</returns>
    public static TokenResult Create()
    {
        var token = Base64UrlEncode(RandomNumberGenerator.GetBytes(TOKEN_BYTES));
        return new TokenResult(token, HashToken(token), DateTime.UtcNow.AddMinutes(TOKEN_LIFETIME_MINUTES));
    }

    /// <summary>
    /// 校验明文令牌是否与入库哈希匹配。
    /// </summary>
    /// <param name="token">链接中携带的明文令牌。</param>
    /// <param name="storedHash">数据库中的哈希。</param>
    /// <returns>匹配返回 <c>true</c>；任一侧为空或格式非法时返回 <c>false</c>。</returns>
    public static bool Matches(string? token, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Convert.FromBase64String(HashToken(token));
        if (actual.Length != expected.Length)
        {
            return false;
        }

        // 恒定时间比较，避免通过响应耗时侧信道推断哈希
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>计算令牌的 SHA-256 哈希（Base64）。</summary>
    /// <param name="token">明文令牌。</param>
    /// <returns>Base64 编码的哈希。</returns>
    private static string HashToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Base64Url 编码（无填充），保证令牌可安全放进 URL 查询串而不被转义。
    /// </summary>
    /// <param name="bytes">原始字节。</param>
    /// <returns>Base64Url 字符串。</returns>
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
