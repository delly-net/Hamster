using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Hamster.Api.Config;
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

    private static readonly Regex UsernamePattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.AUTH_GROUP).WithTags("认证");

        group.MapPost("/register", async (
                RegisterRequest request,
                IUserService users,
                JwtTokenService tokens,
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

                var user = await users.RegisterAsync(username, request.Password!, cancellationToken);
                return Results.Created(
                    $"{ApiPathConst.AUTH_GROUP}/me",
                    BuildResponse(user, tokens));
            })
            .WithName("Register")
            .WithSummary("注册")
            .WithDescription($"用户名 {USERNAME_MIN_LENGTH}-{USERNAME_MAX_LENGTH} 位字母/数字/下划线，密码至少 {PASSWORD_MIN_LENGTH} 位；注册成功即签发登录令牌。");

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

                return Results.Ok(BuildResponse(user, tokens));
            })
            .WithName("Login")
            .WithSummary("登录")
            .WithDescription("校验用户名与密码，成功返回有效期 1 天的 JWT 令牌。");

        group.MapGet("/me", async (
                ClaimsPrincipal principal,
                IUserService users,
                CancellationToken cancellationToken) =>
            {
                var rawUserId = principal.FindFirst(ConfigConst.CLAIM_USER_ID)?.Value;
                if (!int.TryParse(rawUserId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
                {
                    return Results.Unauthorized();
                }

                var user = await users.FindByIdAsync(userId, cancellationToken);
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

        if (string.IsNullOrEmpty(password) || password.Length < PASSWORD_MIN_LENGTH)
        {
            errors["password"] = [$"密码至少 {PASSWORD_MIN_LENGTH} 位"];
        }
        else if (password.Length > PASSWORD_MAX_LENGTH)
        {
            errors["password"] = [$"密码不能超过 {PASSWORD_MAX_LENGTH} 位"];
        }

        return errors;
    }

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

/// <summary>登录/注册成功响应体。</summary>
/// <param name="Token">JWT 令牌。</param>
/// <param name="ExpiresAt">令牌过期时间（UTC）。</param>
/// <param name="User">用户信息。</param>
public sealed record AuthResponse(string Token, DateTimeOffset ExpiresAt, UserDto User);

/// <summary>对外暴露的用户信息，不含任何凭据字段。</summary>
/// <param name="Id">用户主键。</param>
/// <param name="Username">用户名。</param>
/// <param name="CreatedAt">注册时间（UTC）。</param>
public sealed record UserDto(int Id, string Username, DateTime CreatedAt)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="user">用户实体。</param>
    /// <returns>用户 DTO。</returns>
    /// <remarks>
    /// 从 Sqlite 读回的时间为 <see cref="DateTimeKind.Unspecified"/>，此处显式标记为 UTC，
    /// 保证序列化输出带 Z 后缀、语义不产生歧义。
    /// </remarks>
    public static UserDto From(User user) =>
        new(user.Id, user.Username, DateTime.SpecifyKind(user.CreatedAt, DateTimeKind.Utc));
}
