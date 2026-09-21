using System.Security.Cryptography;

namespace Hamster.Api.Security;

/// <summary>
/// 密码哈希工具：PBKDF2（HMAC-SHA256）+ 随机盐，仅依赖 .NET 内置实现。
/// </summary>
/// <remarks>
/// 存储格式为 <c>pbkdf2$sha256$迭代次数$Base64(盐)$Base64(哈希)</c>，迭代次数随哈希串一同保存，
/// 便于将来提升强度时兼容校验历史密码。
/// </remarks>
public static class PasswordHasher
{
    /// <summary>算法标识（存储格式的前两段）。</summary>
    private const string ALGORITHM_PREFIX = "pbkdf2$sha256";

    /// <summary>盐长度（字节）。</summary>
    private const int SALT_BYTES = 16;

    /// <summary>派生哈希长度（字节）。</summary>
    private const int HASH_BYTES = 32;

    /// <summary>迭代次数。</summary>
    private const int ITERATIONS = 100_000;

    /// <summary>存储串分段数量。</summary>
    private const int SEGMENT_COUNT = 5;

    /// <summary>
    /// 计算密码哈希。
    /// </summary>
    /// <param name="password">明文密码。</param>
    /// <returns>可直接入库的哈希串。</returns>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SALT_BYTES);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, ITERATIONS, HashAlgorithmName.SHA256, HASH_BYTES);

        return $"{ALGORITHM_PREFIX}${ITERATIONS}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// 校验密码是否与存储的哈希匹配。
    /// </summary>
    /// <param name="password">待校验的明文密码。</param>
    /// <param name="storedHash">数据库中的哈希串。</param>
    /// <returns>匹配返回 <c>true</c>；哈希串格式非法时返回 <c>false</c>。</returns>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        var segments = storedHash.Split('$');
        if (segments.Length != SEGMENT_COUNT ||
            !string.Equals($"{segments[0]}${segments[1]}", ALGORITHM_PREFIX, StringComparison.Ordinal) ||
            !int.TryParse(segments[2], out var iterations) ||
            iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(segments[3]);
            expectedHash = Convert.FromBase64String(segments[4]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expectedHash.Length == 0)
        {
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expectedHash.Length);

        // 恒定时间比较，避免通过响应耗时侧信道推断哈希
        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }
}
