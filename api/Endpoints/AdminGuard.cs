using System.Security.Claims;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 管理端鉴权：<c>/api/admin/**</c> 各端点共用的管理员身份校验。
/// </summary>
/// <remarks>
/// **管理员身份以数据库为准，不信任令牌中的声明**。这样管理员被停用或删除后，
/// 其此前签发的令牌（有效期 1 天）会立即失去管理权限，而不用等令牌自然过期。
/// </remarks>
public static class AdminGuard
{
    /// <summary>非管理员访问时的提示文案。</summary>
    public const string FORBIDDEN_MESSAGE = "需要系统管理员权限";

    /// <summary>
    /// 解析并校验调用者的管理员身份。
    /// </summary>
    /// <param name="principal">当前请求的用户主体。</param>
    /// <param name="users">用户服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>通过校验返回管理员实体且失败响应为 <c>null</c>；否则管理员为 <c>null</c> 并给出失败响应。</returns>
    public static async Task<(User? Admin, IResult? Failure)> ResolveAdminAsync(
        ClaimsPrincipal principal,
        IUserService users,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        if (userId is null)
        {
            return (null, Results.Unauthorized());
        }

        var user = await users.FindByIdAsync(userId.Value, cancellationToken);
        if (user is null)
        {
            // 用户已被删除，令牌随之失效
            return (null, Results.Unauthorized());
        }

        if (!user.IsAdmin || !user.IsActive)
        {
            return (null, Results.Json(
                new { message = FORBIDDEN_MESSAGE },
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (user, null);
    }
}
