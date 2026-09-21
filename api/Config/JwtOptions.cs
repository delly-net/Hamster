using System.Security.Cryptography;

namespace Hamster.Api.Config;

/// <summary>
/// JWT 认证配置项，对应 appsettings.json 的 <c>Jwt</c> 节点。
/// </summary>
public sealed class JwtOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Jwt";

    /// <summary>随机生成的签名密钥长度（字节）。</summary>
    private const int GENERATED_KEY_BYTES = 32;

    /// <summary>默认令牌有效期（天）。</summary>
    private const int DEFAULT_EXPIRE_DAYS = 1;

    /// <summary>HMAC-SHA256 要求的最小密钥长度（字节）。</summary>
    private const int MIN_KEY_BYTES = 32;

    /// <summary>签名密钥（Base64 或任意字符串）。</summary>
    public string Key { get; private set; } = string.Empty;

    /// <summary>令牌签发者。</summary>
    public string Issuer { get; set; } = "Hamster.Api";

    /// <summary>令牌接收方。</summary>
    public string Audience { get; set; } = "Hamster.Ui";

    /// <summary>令牌有效期（天），默认 1 天。</summary>
    public int ExpireDays { get; set; } = DEFAULT_EXPIRE_DAYS;

    /// <summary>签名密钥是否为本次启动随机生成（未配置 HAMSTER_JWT_KEY）。</summary>
    public bool IsKeyGenerated { get; private set; }

    /// <summary>密钥长度是否低于 HMAC-SHA256 建议的 256 位。</summary>
    public bool IsKeyTooShort => !IsKeyGenerated &&
                                 System.Text.Encoding.UTF8.GetByteCount(Key) < MIN_KEY_BYTES;

    /// <summary>令牌有效期。</summary>
    public TimeSpan TokenLifetime => TimeSpan.FromDays(ExpireDays > 0 ? ExpireDays : DEFAULT_EXPIRE_DAYS);

    /// <summary>
    /// 从配置与环境变量解析 JWT 配置，环境变量优先。
    /// 密钥缺省时随机生成，保证开箱即用（进程重启后旧令牌自然失效）。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <returns>JWT 配置实例。</returns>
    public static JwtOptions From(IConfiguration configuration)
    {
        var options = new JwtOptions();
        configuration.GetSection(SectionName).Bind(options);

        var envKey = Environment.GetEnvironmentVariable(ConfigConst.JWT_KEY_ENV);
        var configuredKey = string.IsNullOrWhiteSpace(envKey)
            ? configuration.GetSection(SectionName)["Key"]
            : envKey;

        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            options.Key = configuredKey.Trim();
            options.IsKeyGenerated = false;
        }
        else
        {
            options.Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(GENERATED_KEY_BYTES));
            options.IsKeyGenerated = true;
        }

        if (options.ExpireDays <= 0)
        {
            options.ExpireDays = DEFAULT_EXPIRE_DAYS;
        }

        return options;
    }
}
