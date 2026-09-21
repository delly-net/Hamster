using System.Globalization;
using Hamster.Api.Config;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 当前账套解析：把「读取账套请求头 → 查库 → 校验访问权限」收敛到一处。
/// </summary>
/// <remarks>
/// 账套**不写入 JWT**，而是每次请求由 <c>X-Account-Set-Id</c> 请求头携带、后端逐请求校验，
/// 与「管理员身份不写令牌、逐请求回查数据库」的既有取舍一致：管理员在账套管理页调整关联关系后，
/// 用户在**下一次请求**即生效，无需等待令牌过期（1 天）。
/// <para>
/// 后续业务端点应一律通过本扩展取当前账套，再以该账套 Id 作为数据过滤条件；
/// 未携带请求头时返回的账套为 <c>null</c>，调用方可自行决定「按无账套处理」还是拒绝请求。
/// </para>
/// </remarks>
public static class CurrentAccountSetExtensions
{
    /// <summary>
    /// 解析并校验当前请求的账套。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="users">用户服务。</param>
    /// <param name="accountSets">账套服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 成功时返回账套实体（未携带请求头时为 <c>null</c>）且失败响应为 <c>null</c>；
    /// 失败时账套为 <c>null</c> 并给出失败响应。
    /// </returns>
    public static async Task<(AccountSet? AccountSet, IResult? Failure)> ResolveCurrentAccountSetAsync(
        this HttpContext context,
        IUserService users,
        IAccountSetService accountSets,
        CancellationToken cancellationToken)
    {
        var rawAccountSetId = context.Request.Headers[ConfigConst.ACCOUNT_SET_HEADER].ToString();
        if (string.IsNullOrWhiteSpace(rawAccountSetId))
        {
            // 未指定账套：交由调用方决定是「按无账套处理」还是拒绝请求
            return (null, null);
        }

        if (!int.TryParse(rawAccountSetId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var accountSetId))
        {
            return (null, Results.BadRequest(new { message = "账套标识格式不正确" }));
        }

        var userId = context.User.GetUserId();
        if (userId is null)
        {
            return (null, Results.Unauthorized());
        }

        var user = await users.FindByIdAsync(userId.Value, cancellationToken);
        if (user is null)
        {
            return (null, Results.Unauthorized());
        }

        var accountSet = await accountSets.FindByIdAsync(accountSetId, cancellationToken);

        // 「账套不存在」与「无权访问」返回同一响应，避免被用于探测账套是否存在
        if (accountSet is null ||
            !await accountSets.IsAccessibleAsync(accountSetId, user.Id, user.IsAdmin, cancellationToken))
        {
            return (null, Results.Json(
                new { message = "账套不存在或无权访问" },
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (accountSet, null);
    }
}
