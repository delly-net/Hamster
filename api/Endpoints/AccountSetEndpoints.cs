using System.Security.Claims;
using Hamster.Api.Config;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 账套端点（任意已登录用户）：查询自己可访问的账套，以及解析当前账套。
/// </summary>
/// <remarks>
/// 管理员无需任何关联即可看到全部账套，判定在 <see cref="IAccountSetService.ListForUserAsync"/> 内。
/// </remarks>
public sealed class AccountSetEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.ACCOUNT_SET_GROUP)
            .WithTags("账套")
            .RequireAuthorization();

        group.MapGet("/mine", async (
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                CancellationToken cancellationToken) =>
            {
                var userId = principal.GetUserId();
                if (userId is null)
                {
                    return Results.Unauthorized();
                }

                // 以数据库中的用户为准取 IsAdmin：令牌里的管理员声明不可信（见 AdminGuard 注释）
                var user = await users.FindByIdAsync(userId.Value, cancellationToken);
                if (user is null)
                {
                    return Results.Unauthorized();
                }

                var accountSetsOfUser = await accountSets.ListForUserAsync(user.Id, user.IsAdmin, cancellationToken);
                return Results.Ok(accountSetsOfUser.Select(accountSet => AccountSetDto.From(accountSet)).ToArray());
            })
            .WithName("ListMyAccountSets")
            .WithSummary("我可访问的账套")
            .WithDescription("普通用户返回被关联的账套；管理员返回全部账套（无需显式关联）。");

        group.MapGet("/current", async (
                HttpContext context,
                IUserService users,
                IAccountSetService accountSets,
                CancellationToken cancellationToken) =>
            {
                var (accountSet, failure) = await context.ResolveCurrentAccountSetAsync(
                    users,
                    accountSets,
                    cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                // 未携带账套请求头时返回 null 而非报错：前端据此决定是否弹出账套选择弹窗
                return Results.Ok(accountSet is null ? null : AccountSetDto.From(accountSet));
            })
            .WithName("GetCurrentAccountSet")
            .WithSummary("当前账套")
            .WithDescription($"依请求头 {ConfigConst.ACCOUNT_SET_HEADER} 解析并校验当前账套：未携带该头时返回 null；账套不存在或当前用户无权访问返回 403。");
    }
}

/// <summary>账套信息（对外暴露，不含内部关联明细）。</summary>
/// <param name="Id">账套主键。</param>
/// <param name="Name">账套名称。</param>
/// <param name="Remark">备注；无备注时为 <c>null</c>。</param>
/// <param name="MemberCount">关联用户数；仅管理员列表提供，其余场景为 <c>null</c>（避免出现「0 个关联用户」的误导）。</param>
/// <param name="CreatedAt">创建时间（UTC）。</param>
public sealed record AccountSetDto(int Id, string Name, string? Remark, int? MemberCount, DateTime CreatedAt)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="accountSet">账套实体。</param>
    /// <param name="memberCount">关联用户数；非列表场景省略。</param>
    /// <returns>账套 DTO。</returns>
    /// <remarks>
    /// 从 Sqlite 读回的时间为 <see cref="DateTimeKind.Unspecified"/>，此处显式标记为 UTC，
    /// 保证序列化输出带 Z 后缀、语义不产生歧义。
    /// </remarks>
    public static AccountSetDto From(AccountSet accountSet, int? memberCount = null) => new(
        accountSet.Id,
        accountSet.Name,
        accountSet.Remark,
        memberCount,
        DateTime.SpecifyKind(accountSet.CreatedAt, DateTimeKind.Utc));
}
