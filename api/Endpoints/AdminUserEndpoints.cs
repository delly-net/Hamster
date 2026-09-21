using System.Security.Claims;
using Hamster.Api.Config;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Security;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 管理员用户管理端点：用户列表、激活 / 取消激活、生成密码重置链接、删除。
/// </summary>
/// <remarks>
/// 管理员身份校验统一走 <see cref="AdminGuard"/>（以数据库为准，不信任令牌声明）。
/// </remarks>
public sealed class AdminUserEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.ADMIN_USERS_GROUP)
            .WithTags("用户管理")
            .RequireAuthorization();

        group.MapGet("", async (
                ClaimsPrincipal principal,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var all = await users.ListAsync(cancellationToken);
                return Results.Ok(all.Select(AdminUserDto.From).ToArray());
            })
            .WithName("ListUsers")
            .WithSummary("用户列表")
            .WithDescription("返回全部用户及其激活状态与角色，供管理员在用户管理页操作。");

        group.MapPost("/{id:int}/activate", async (
                int id,
                ClaimsPrincipal principal,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                return await users.SetActiveAsync(id, true, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("ActivateUser")
            .WithSummary("激活用户")
            .WithDescription("将用户置为已激活，激活后该用户即可登录。");

        group.MapPost("/{id:int}/deactivate", async (
                int id,
                ClaimsPrincipal principal,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var (admin, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                // 停用自己会让当前管理员立即失去管理权限，其余管理员也可能已不存在，
                // 从而把系统锁死——前端按钮已禁用，这里才是真正的防线
                if (admin!.Id == id)
                {
                    return Results.BadRequest(new { message = "不能停用当前登录的管理员账号" });
                }

                return await users.SetActiveAsync(id, false, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("DeactivateUser")
            .WithSummary("取消激活")
            .WithDescription("将用户置为未激活，取消激活后该用户将无法登录（已签发令牌在有效期内仍可用，本次不做令牌撤销）。");

        group.MapPost("/{id:int}/reset-link", async (
                int id,
                ClaimsPrincipal principal,
                IUserService users,
                PublicUrlOptions publicUrl,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var user = await users.FindByIdAsync(id, cancellationToken);
                if (user is null)
                {
                    return NotFound();
                }

                var token = ResetTokenService.Create();
                await users.SetResetTokenAsync(user.Id, token.TokenHash, token.ExpiresAt, cancellationToken);

                // 明文令牌只在此响应中出现一次，库中仅有哈希
                return Results.Ok(new ResetLinkDto(
                    publicUrl.BuildResetUrl(user.Username, token.Token),
                    DateTime.SpecifyKind(token.ExpiresAt, DateTimeKind.Utc)));
            })
            .WithName("CreateResetLink")
            .WithSummary("生成密码重置链接")
            .WithDescription($"为该用户生成专属重置链接，有效期 {ResetTokenService.TOKEN_LIFETIME_MINUTES} 分钟、一次性；用户打开后需填写用户名并设置新密码。");

        group.MapDelete("/{id:int}", async (
                int id,
                ClaimsPrincipal principal,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var (admin, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                // 同上：删除自己会使系统失去当前管理员
                if (admin!.Id == id)
                {
                    return Results.BadRequest(new { message = "不能删除当前登录的管理员账号" });
                }

                return await users.DeleteAsync(id, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("DeleteUser")
            .WithSummary("删除用户")
            .WithDescription("删除用户，被删除用户的令牌随后续请求立即失效。");
    }

    /// <summary>用户不存在时的响应。</summary>
    /// <returns>404 响应。</returns>
    private static IResult NotFound() => Results.NotFound(new { message = "用户不存在" });
}

/// <summary>用户管理页展示用的用户信息，不含任何凭据字段。</summary>
/// <param name="Id">用户主键。</param>
/// <param name="Username">用户名。</param>
/// <param name="CreatedAt">注册时间（UTC）。</param>
/// <param name="IsAdmin">是否系统管理员。</param>
/// <param name="IsActive">是否已激活。</param>
public sealed record AdminUserDto(int Id, string Username, DateTime CreatedAt, bool IsAdmin, bool IsActive)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="user">用户实体。</param>
    /// <returns>用户 DTO。</returns>
    public static AdminUserDto From(User user) => new(
        user.Id,
        user.Username,
        DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc),
        user.IsAdmin,
        user.IsActive);
}

/// <summary>密码重置链接响应体。</summary>
/// <param name="ResetUrl">供用户打开的完整链接（含明文令牌，仅此一次返回）。</param>
/// <param name="ExpiresAt">链接过期时间（UTC）。</param>
public sealed record ResetLinkDto(string ResetUrl, DateTime ExpiresAt);
