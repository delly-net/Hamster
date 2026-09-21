using System.Globalization;
using System.Security.Claims;
using System.Text;
using Hamster.Api.Config;
using Hamster.Api.Data.Entities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Hamster.Api.Security;

/// <summary>
/// JWT 令牌签发服务，签名算法为 HMAC-SHA256。
/// </summary>
/// <param name="options">JWT 配置。</param>
public sealed class JwtTokenService(JwtOptions options)
{
    /// <summary>签发结果。</summary>
    /// <param name="Token">令牌字符串。</param>
    /// <param name="ExpiresAt">过期时间（UTC）。</param>
    public readonly record struct TokenResult(string Token, DateTimeOffset ExpiresAt);

    /// <summary>
    /// 为用户签发登录令牌。
    /// </summary>
    /// <param name="user">用户实体。</param>
    /// <returns>令牌与过期时间。</returns>
    public TokenResult CreateToken(User user)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(options.TokenLifetime);
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Key));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Expires = expiresAt.UtcDateTime,
            IssuedAt = DateTime.UtcNow,
            Subject = new ClaimsIdentity(
            [
                new Claim(ConfigConst.CLAIM_USER_ID, user.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ConfigConst.CLAIM_USER_NAME, user.Username),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ]),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
        };

        var token = new JsonWebTokenHandler().CreateToken(descriptor);
        return new TokenResult(token, expiresAt);
    }
}
