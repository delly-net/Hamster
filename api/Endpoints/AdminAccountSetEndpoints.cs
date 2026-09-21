using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 管理员账套管理端点：账套列表、新建、改名/改备注、删除，以及账套与用户的关联维护。
/// </summary>
/// <remarks>
/// 账套与用户为多对多关系。管理员**无需关联即可访问全部账套**（判定见 <c>AccountSetService.ListForUserAsync</c>），
/// 故此处的关联维护实质是「给普通用户分配可访问的账套」。
/// 管理员身份校验统一走 <see cref="AdminGuard"/>（以数据库为准，不信任令牌声明）。
/// </remarks>
public sealed class AdminAccountSetEndpoints : IEndpoint
{
    private const int NAME_MAX_LENGTH = 64;
    private const int REMARK_MAX_LENGTH = 256;

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.ADMIN_ACCOUNT_SETS_GROUP)
            .WithTags("账套管理")
            .RequireAuthorization();

        group.MapGet("", async (
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var all = await accountSets.ListAllAsync(cancellationToken);
                return Results.Ok(all
                    .Select(item => AccountSetDto.From(item.AccountSet, item.MemberCount))
                    .ToArray());
            })
            .WithName("ListAccountSets")
            .WithSummary("账套列表")
            .WithDescription("返回全部账套及其关联用户数，供管理员在账套管理页操作。");

        group.MapPost("", async (
                AccountSetRequest request,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var errors = Validate(request);
                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var name = request.Name!.Trim();
                if (await accountSets.IsNameTakenAsync(name, null, cancellationToken))
                {
                    return Results.Conflict(new { message = "该账套名称已存在" });
                }

                var created = await accountSets.CreateAsync(name, request.Remark, cancellationToken);
                return Results.Created($"{ApiPathConst.ADMIN_ACCOUNT_SETS_GROUP}/{created.Id}", AccountSetDto.From(created));
            })
            .WithName("CreateAccountSet")
            .WithSummary("新建账套")
            .WithDescription($"账套名称必填且全局唯一（{NAME_MAX_LENGTH} 位以内、不区分大小写），备注可省略。");

        group.MapPut("/{id:int}", async (
                int id,
                AccountSetRequest request,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var errors = Validate(request);
                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var name = request.Name!.Trim();
                // 排除自身：不改名时沿用原名不应被判为重名
                if (await accountSets.IsNameTakenAsync(name, id, cancellationToken))
                {
                    return Results.Conflict(new { message = "该账套名称已存在" });
                }

                return await accountSets.UpdateAsync(id, name, request.Remark, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("UpdateAccountSet")
            .WithSummary("修改账套")
            .WithDescription("修改账套名称与备注，名称规则同新建。");

        group.MapDelete("/{id:int}", async (
                int id,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                return await accountSets.DeleteAsync(id, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("DeleteAccountSet")
            .WithSummary("删除账套")
            .WithDescription("删除账套并同时清除其全部用户关联；已删除账套的 Id 此后不再被当前账套解析接受。");

        group.MapGet("/{id:int}/members", async (
                int id,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                if (await accountSets.FindByIdAsync(id, cancellationToken) is null)
                {
                    return NotFound();
                }

                var memberIds = await accountSets.ListMemberIdsAsync(id, cancellationToken);
                return Results.Ok(memberIds.ToArray());
            })
            .WithName("ListAccountSetMembers")
            .WithSummary("账套关联用户")
            .WithDescription("返回该账套已关联的用户主键列表，供管理页回显勾选状态。");

        group.MapPut("/{id:int}/members", async (
                int id,
                AccountSetMembersRequest request,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                if (await accountSets.FindByIdAsync(id, cancellationToken) is null)
                {
                    return NotFound();
                }

                // 覆盖式保存：请求体即该账套关联用户的完整目标集合，天然幂等
                await accountSets.ReplaceMembersAsync(id, request.UserIds ?? [], cancellationToken);
                return Results.NoContent();
            })
            .WithName("ReplaceAccountSetMembers")
            .WithSummary("设置账套关联用户")
            .WithDescription("以请求体中的用户集合**整体替换**该账套的关联用户；不存在的用户主键会被忽略。");
    }

    /// <summary>账套不存在时的响应。</summary>
    /// <returns>404 响应。</returns>
    private static IResult NotFound() => Results.NotFound(new { message = "账套不存在" });

    /// <summary>校验账套名称与备注，返回按字段聚合的错误信息。</summary>
    /// <param name="request">请求体。</param>
    /// <returns>错误字典；无错误时为空。</returns>
    private static Dictionary<string, string[]> Validate(AccountSetRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            errors["name"] = ["账套名称不能为空"];
        }
        else if (name.Length > NAME_MAX_LENGTH)
        {
            errors["name"] = [$"账套名称不能超过 {NAME_MAX_LENGTH} 位"];
        }

        if (request.Remark?.Trim().Length > REMARK_MAX_LENGTH)
        {
            errors["remark"] = [$"备注不能超过 {REMARK_MAX_LENGTH} 位"];
        }

        return errors;
    }
}

/// <summary>新建 / 修改账套请求体。</summary>
/// <param name="Name">账套名称。</param>
/// <param name="Remark">备注，可省略。</param>
public sealed record AccountSetRequest(string? Name, string? Remark);

/// <summary>设置账套关联用户请求体。</summary>
/// <param name="UserIds">该账套关联用户的**完整目标集合**（覆盖式保存）。</param>
public sealed record AccountSetMembersRequest(int[]? UserIds);
