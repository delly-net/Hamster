using System.Security.Claims;
using System.Text.RegularExpressions;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Security;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 认证端点：注册、登录与当前用户查询，登录成功后签发有效期 1 天的 JWT。
/// </summary>
public sealed class AuthEndpoints : IEndpoint
{
    private const int USERNAME_MIN_LENGTH = 3;
    private const int USERNAME_MAX_LENGTH = 32;
    private const int PASSWORD_MIN_LENGTH = 6;
    private const int PASSWORD_MAX_LENGTH = 128;

    /// <summary>注册成功后的提示文案（登录页直接展示）。</summary>
    private const string REGISTER_PENDING_ACTIVATION_MESSAGE = "注册成功，请等待管理员激活后登录";

    /// <summary>账号未激活的提示文案。</summary>
    private const string INACTIVE_ACCOUNT_MESSAGE = "账号尚未激活，请联系管理员激活";

    /// <summary>重置失败的统一文案。</summary>
    private const string INVALID_RESET_LINK_MESSAGE = "重置链接无效或已过期";

    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.AUTH_GROUP).WithTags("认证");

        group.MapPost("/register", async (
                RegisterRequest request,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var errors = ValidateCredentials(request.Username, request.Password);
                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var username = request.Username!.Trim();
                if (await users.FindByUsernameAsync(username, cancellationToken) is not null)
                {
                    return Results.Conflict(new { message = "该用户名已被注册" });
                }

                // 注册仅建号：新用户未激活，须管理员激活后才能登录，故此处不签发令牌
                var user = await users.RegisterAsync(username, request.Password!, cancellationToken);
                return Results.Created(
                    $"{ApiPathConst.AUTH_GROUP}/me",
                    new RegisterResponse(REGISTER_PENDING_ACTIVATION_MESSAGE, UserDto.From(user)));
            })
            .WithName("Register")
            .WithSummary("注册")
            .WithDescription($"用户名 {USERNAME_MIN_LENGTH}-{USERNAME_MAX_LENGTH} 位字母/数字/下划线，密码至少 {PASSWORD_MIN_LENGTH} 位；注册后账号处于未激活状态，需管理员激活后方可登录。");

        group.MapPost("/login", async (
                LoginRequest request,
                IUserService users,
                JwtTokenService tokens,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["username"] = ["用户名与密码不能为空"],
                    });
                }

                var user = await users.AuthenticateAsync(request.Username, request.Password, cancellationToken);
                if (user is null)
                {
                    // 不区分「用户不存在」与「密码错误」，避免被用于枚举用户名
                    return Results.Json(
                        new { message = "用户名或密码错误" },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                // 激活状态必须**在密码校验通过之后**才告知：否则任何人都能借这条分支
                // 探出「某用户名已注册但未激活」，白拿一份用户名枚举能力
                if (!user.IsActive)
                {
                    return Results.Json(
                        new { message = INACTIVE_ACCOUNT_MESSAGE },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                return Results.Ok(BuildResponse(user, tokens));
            })
            .WithName("Login")
            .WithSummary("登录")
            .WithDescription("校验用户名与密码，成功返回有效期 1 天的 JWT 令牌；账号未激活时返回 403。");

        group.MapPost("/reset-password", async (
                ResetPasswordRequest request,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                // 三类失败（用户不存在 / 令牌错误 / 令牌过期）统一返回同一文案，
                // 且不区分状态码，避免重置接口被用于探测用户名或爆破令牌
                if (string.IsNullOrWhiteSpace(request.Username) ||
                    string.IsNullOrWhiteSpace(request.Token) ||
                    string.IsNullOrWhiteSpace(request.NewPassword))
                {
                    return InvalidResetLink();
                }

                var passwordErrors = ValidatePassword(request.NewPassword);
                if (passwordErrors.Count > 0)
                {
                    return Results.ValidationProblem(passwordErrors);
                }

                var user = await users.FindByUsernameAsync(request.Username!, cancellationToken);
                if (user?.ResetTokenHash is null || user.ResetTokenExpiresAt is null)
                {
                    return InvalidResetLink();
                }

                if (user.ResetTokenExpiresAt.Value <= DateTime.UtcNow ||
                    !ResetTokenService.Matches(request.Token, user.ResetTokenHash))
                {
                    return InvalidResetLink();
                }

                // 改密与清空令牌在同一个更新语句内完成，令牌一次性
                await users.ResetPasswordAsync(user.Id, request.NewPassword!, cancellationToken);
                return Results.Ok(new { message = "密码已重置，请使用新密码登录" });
            })
            .WithName("ResetPassword")
            .WithSummary("重置密码")
            .WithDescription("凭管理员生成的专属重置链接（15 分钟内有效、一次性）设置新密码；需同时提供用户名与链接中的令牌。");

        group.MapGet("/me", async (
                ClaimsPrincipal principal,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var userId = principal.GetUserId();
                if (userId is null)
                {
                    return Results.Unauthorized();
                }

                var user = await users.FindByIdAsync(userId.Value, cancellationToken);
                return user is null ? Results.Unauthorized() : Results.Ok(UserDto.From(user));
            })
            .RequireAuthorization()
            .WithName("GetCurrentUser")
            .WithSummary("当前登录用户")
            .WithDescription("依据请求头中的 Bearer 令牌返回当前用户信息。");
    }

    /// <summary>校验用户名与密码格式，返回按字段聚合的错误信息。</summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">密码。</param>
    /// <returns>错误字典；无错误时为空。</returns>
    private static Dictionary<string, string[]> ValidateCredentials(string? username, string? password)
    {
        var errors = new Dictionary<string, string[]>();

        var trimmedUsername = username?.Trim() ?? string.Empty;
        if (trimmedUsername.Length is < USERNAME_MIN_LENGTH or > USERNAME_MAX_LENGTH ||
            !UsernamePattern.IsMatch(trimmedUsername))
        {
            errors["username"] =
            [
                $"用户名需为 {USERNAME_MIN_LENGTH}-{USERNAME_MAX_LENGTH} 位字母、数字或下划线",
            ];
        }

        var passwordErrors = ValidatePassword(password);
        if (passwordErrors.Count > 0)
        {
            foreach (var (field, messages) in passwordErrors)
            {
                errors[field] = messages;
            }
        }

        return errors;
    }

    /// <summary>校验密码强度，返回按字段聚合的错误信息。</summary>
    /// <param name="password">密码。</param>
    /// <returns>错误字典；无错误时为空。</returns>
    private static Dictionary<string, string[]> ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < PASSWORD_MIN_LENGTH)
        {
            return new Dictionary<string, string[]> { ["password"] = [$"密码至少 {PASSWORD_MIN_LENGTH} 位"] };
        }

        if (password.Length > PASSWORD_MAX_LENGTH)
        {
            return new Dictionary<string, string[]> { ["password"] = [$"密码不能超过 {PASSWORD_MAX_LENGTH} 位"] };
        }

        return [];
    }

    /// <summary>重置失败的统一响应：文案与状态码均不区分具体原因。</summary>
    /// <returns>400 响应。</returns>
    private static IResult InvalidResetLink() => Results.Json(
        new { message = INVALID_RESET_LINK_MESSAGE },
        statusCode: StatusCodes.Status400BadRequest);

    /// <summary>签发令牌并组装响应体。</summary>
    /// <param name="user">用户实体。</param>
    /// <param name="tokens">令牌服务。</param>
    /// <returns>登录响应。</returns>
    private static AuthResponse BuildResponse(User user, JwtTokenService tokens)
    {
        var result = tokens.CreateToken(user);
        return new AuthResponse(result.Token, result.ExpiresAt, UserDto.From(user));
    }
}

/// <summary>注册请求体。</summary>
/// <param name="Username">用户名。</param>
/// <param name="Password">密码明文。</param>
public sealed record RegisterRequest(string? Username, string? Password);

/// <summary>登录请求体。</summary>
/// <param name="Username">用户名。</param>
/// <param name="Password">密码明文。</param>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>注册响应体：注册不再直接签发令牌，需等待管理员激活。</summary>
/// <param name="Message">面向用户的提示文案。</param>
/// <param name="User">新建的用户信息。</param>
public sealed record RegisterResponse(string Message, UserDto User);

/// <summary>登录成功响应体。</summary>
/// <param name="Token">JWT 令牌。</param>
/// <param name="ExpiresAt">令牌过期时间（UTC）。</param>
/// <param name="User">用户信息。</param>
public sealed record AuthResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);

/// <summary>重置密码请求体。</summary>
/// <param name="Username">用户名（与令牌构成双重校验）。</param>
/// <param name="Token">重置链接中的令牌明文。</param>
/// <param name="NewPassword">新密码明文。</param>
public sealed record ResetPasswordRequest(string? Username, string? Token, string? NewPassword);

/// <summary>对外暴露的用户信息，不含任何凭据字段。</summary>
/// <param name="Id">用户主键。</param>
/// <param name="Username">用户名。</param>
/// <param name="CreatedAt">注册时间（UTC）。</param>
/// <param name="IsAdmin">是否系统管理员。</param>
/// <param name="IsActive">是否已激活。</param>
public sealed record UserDto(int Id, string Username, DateTime CreatedAt, bool IsAdmin, bool IsActive)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="user">用户实体。</param>
    /// <returns>用户 DTO。</returns>
    /// <remarks>
    /// 从 Sqlite 读回的时间为 <see cref="DateTimeKind.Unspecified"/>，此处显式标记为 UTC，
    /// 保证序列化输出带 Z 后缀、语义不产生歧义。
    /// </remarks>
    public static UserDto From(User user) => new(
        user.Id,
        user.Username,
        DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc),
        user.IsAdmin,
        user.IsActive);
}
