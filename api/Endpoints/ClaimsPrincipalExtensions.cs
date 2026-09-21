using System.Globalization;
using System.Security.Claims;
using Hamster.Api.Config;

namespace Hamster.Api.Endpoints;

/// <summary>
/// <see cref="ClaimsPrincipal"/> 扩展：从 JWT 声明中取出当前用户主键。
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// 读取当前用户主键。
    /// </summary>
    /// <param name="principal">当前请求的用户主体。</param>
    /// <returns>用户主键；声明缺失或非法时返回 <c>null</c>。</returns>
    public static int? GetUserId(this ClaimsPrincipal principal)
    {
        var rawUserId = principal.FindFirst(ConfigConst.CLAIM_USER_ID)?.Value;
        return int.TryParse(rawUserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            ? userId
            : null;
    }
}
