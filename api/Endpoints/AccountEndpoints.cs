using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 账户端点（任意已登录用户）：在当前账套内查询、新建、修改账户，以及停用/启用。
/// </summary>
/// <remarks>
/// **账户一律挂在账套下**：每个端点先解析当前账套（请求头 <c>X-Account-Set-Id</c>），
/// 未指定账套即拒绝请求——否则会退化成「查询全库账户」的越权缺口。
/// <para>
/// 可见性判定（谁能看见、谁能改）收敛在 <see cref="IAccountService"/> 内，本层只负责
/// 参数校验与错误响应；管理员的超级权限来自账套层的放行（管理员对任意存在的账套均可访问），
/// 而非绕过账套。
/// </para>
/// <para>
/// 删除为**软删除**：以 <c>/deactivate</c> 与 <c>/activate</c> 取代 DELETE，
/// 账户是流水的挂靠对象，物理删除会让历史流水指向不存在的账户。
/// </para>
/// </remarks>
public sealed class AccountEndpoints : IEndpoint
{
    /// <summary>账户名称最大长度。</summary>
    private const int NAME_MAX_LENGTH = 64;

    /// <summary>期初金额绝对值上限，防止超出 decimal(18,2) 的表示范围。</summary>
    private const decimal BALANCE_ABS_LIMIT = 999_999_999_999.99M;

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.ACCOUNT_GROUP)
            .WithTags("账户")
            .RequireAuthorization();

        group.MapGet("", async (
                bool? includeInactive,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var visible = await accounts.ListByAccountSetAsync(
                    accountSet!.Id,
                    actor!.Id,
                    actor.IsAdmin,
                    includeInactive ?? false,
                    cancellationToken);

                return Results.Ok(visible
                    .Select(item => AccountDto.From(item.Account, item.OwnerUsername))
                    .ToArray());
            })
            .WithName("ListAccounts")
            .WithSummary("账户列表")
            .WithDescription(
                "返回当前账套内当前用户可见的账户，按主键升序。管理员可见账套内全部账户（含他人个人账户）；" +
                "普通用户只见公共账户与自己创建的个人账户。默认只返回启用的账户，includeInactive=true 时含已停用的。");

        group.MapPost("", async (
                AccountRequest request,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var errors = new Dictionary<string, string[]>();
                ValidateName(request.Name, errors);

                if (!TryParseByName(request.Scope, out AccountScope scope))
                {
                    errors["scope"] = ["归属范围只能是 Personal（个人）或 Public（公共）"];
                }

                if (!TryParseByName(request.Type, out AccountType type))
                {
                    errors["type"] = ["账户类型只能是 Ledger / Fund / Liability / Contact"];
                }

                ValidateBalance(request.InitialBalance, errors);

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var name = request.Name!.Trim();
                // 个人账户的归属人恒为创建者；公共账户无归属人
                var ownerUserId = scope == AccountScope.Personal ? actor!.Id : (int?)null;
                if (await accounts.IsNameTakenAsync(accountSet!.Id, scope, ownerUserId, name, null, cancellationToken))
                {
                    return Results.Conflict(new { message = "该范围内已存在同名账户" });
                }

                var created = await accounts.CreateAsync(
                    accountSet.Id,
                    name,
                    scope,
                    type,
                    request.InitialBalance,
                    actor!.Id,
                    cancellationToken);

                return Results.Created(
                    $"{ApiPathConst.ACCOUNT_GROUP}/{created.Id}",
                    AccountDto.From(created, ownerUserId is null ? null : actor.Username));
            })
            .WithName("CreateAccount")
            .WithSummary("新建账户")
            .WithDescription(
                "在当前账套内新建账户。个人账户的归属人强制为当前登录者（请求体无需也无法指定归属人）；" +
                "名称在「账套 + 归属范围」内不区分大小写唯一，重复返回 409。");

        group.MapPut("/{id:int}", async (
                int id,
                AccountUpdateRequest request,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var errors = new Dictionary<string, string[]>();
                ValidateName(request.Name, errors);

                if (!TryParseByName(request.Type, out AccountType type))
                {
                    errors["type"] = ["账户类型只能是 Ledger / Fund / Liability / Contact"];
                }

                ValidateBalance(request.InitialBalance, errors);

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var account = await accounts.FindVisibleAsync(
                    id, accountSet!.Id, actor!.Id, actor.IsAdmin, cancellationToken);

                if (account is null)
                {
                    return NotFound();
                }

                var name = request.Name!.Trim();
                // 归属范围与归属人不可变，查重沿用账户自身的取值
                if (await accounts.IsNameTakenAsync(
                        accountSet.Id, account.Scope, account.OwnerUserId, name, id, cancellationToken))
                {
                    return Results.Conflict(new { message = "该范围内已存在同名账户" });
                }

                return await accounts.UpdateAsync(account, name, type, request.InitialBalance, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("UpdateAccount")
            .WithSummary("修改账户")
            .WithDescription(
                "修改账户名称、类型与期初金额。归属范围、归属人与所属账套一经创建不可修改" +
                "（个人 → 公共等于把私有数据公开给全账套，故不提供该能力）。");

        group.MapPost("/{id:int}/deactivate", async (
                int id,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var account = await accounts.FindVisibleAsync(
                    id, accountSet!.Id, actor!.Id, actor.IsAdmin, cancellationToken);

                if (account is null)
                {
                    return NotFound();
                }

                return await accounts.SetActiveAsync(account, false, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("DeactivateAccount")
            .WithSummary("停用账户（软删除）")
            .WithDescription("停用后默认不出现在账户列表中，可用 includeInactive=true 查看并重新启用；数据行保留。");

        group.MapPost("/{id:int}/activate", async (
                int id,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IAccountService accounts,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var account = await accounts.FindVisibleAsync(
                    id, accountSet!.Id, actor!.Id, actor.IsAdmin, cancellationToken);

                if (account is null)
                {
                    return NotFound();
                }

                return await accounts.SetActiveAsync(account, true, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("ActivateAccount")
            .WithSummary("启用账户")
            .WithDescription("恢复此前停用的账户，使其重新出现在默认账户列表中。");
    }

    /// <summary>账户不存在、不属于当前账套或对当前用户不可见时的响应。</summary>
    /// <returns>404 响应。</returns>
    private static IResult NotFound() => Results.NotFound(new { message = "账户不存在" });

    /// <summary>
    /// 解析本次请求的「操作者 + 当前账套」。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="principal">当前请求的用户主体。</param>
    /// <param name="users">用户服务。</param>
    /// <param name="accountSets">账套服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 全部通过时返回操作者与账套且失败响应为 <c>null</c>；否则操作者与账套为 <c>null</c> 并给出失败响应。
    /// </returns>
    /// <remarks>
    /// 用户身份以数据库为准（不信任令牌中的声明），与 <see cref="AdminGuard"/> 的取舍一致。
    /// 账套未携带请求头时 <c>ResolveCurrentAccountSetAsync</c> 返回的是 <c>(null, null)</c>——
    /// **这不是失败而是「无账套」**，此处显式转成 400：账户一律挂在账套下，无账套即无可操作对象。
    /// </remarks>
    private static async Task<(User? Actor, AccountSet? AccountSet, IResult? Failure)> ResolveContextAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        IUserService users,
        IAccountSetService accountSets,
        CancellationToken cancellationToken)
    {
        var (accountSet, failure) = await context.ResolveCurrentAccountSetAsync(
            users,
            accountSets,
            cancellationToken);

        if (failure is not null)
        {
            return (null, null, failure);
        }

        // 未携带账套请求头不能当作「按无账套处理」：账户必须落在某个账套内
        if (accountSet is null)
        {
            return (null, null, Results.BadRequest(new { message = "请先选择账套" }));
        }

        var userId = principal.GetUserId();
        if (userId is null)
        {
            return (null, null, Results.Unauthorized());
        }

        var actor = await users.FindByIdAsync(userId.Value, cancellationToken);
        if (actor is null)
        {
            return (null, null, Results.Unauthorized());
        }

        return (actor, accountSet, null);
    }

    /// <summary>校验账户名称，把错误写入错误字典。</summary>
    /// <param name="name">原始名称。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    private static void ValidateName(string? name, Dictionary<string, string[]> errors)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            errors["name"] = ["账户名称不能为空"];
        }
        else if (trimmed.Length > NAME_MAX_LENGTH)
        {
            errors["name"] = [$"账户名称不能超过 {NAME_MAX_LENGTH} 位"];
        }
    }

    /// <summary>校验期初金额，把错误写入错误字典。</summary>
    /// <param name="balance">原始金额。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    /// <remarks>
    /// 允许为负：负债账户的期初金额天然是负数。小数位超过两位则拒绝，
    /// 避免「提交 1.005 却因入库存两位小数而悄悄变成 1.01」这类无声偏差。
    /// </remarks>
    private static void ValidateBalance(decimal balance, Dictionary<string, string[]> errors)
    {
        if (decimal.Round(balance, 2) != balance)
        {
            errors["initialBalance"] = ["期初金额最多两位小数"];
        }
        else if (Math.Abs(balance) > BALANCE_ABS_LIMIT)
        {
            errors["initialBalance"] = [$"期初金额绝对值不能超过 {BALANCE_ABS_LIMIT}"];
        }
    }

    /// <summary>按**名称**解析枚举，大小写不敏感。</summary>
    /// <typeparam name="TEnum">目标枚举类型。</typeparam>
    /// <param name="raw">原始字符串。</param>
    /// <param name="value">解析结果。</param>
    /// <returns>解析成功返回 <c>true</c>。</returns>
    /// <remarks>
    /// 刻意不用 <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/>：它对纯数字串同样返回成功
    /// （<c>"2"</c> 会被解析为 <c>Public</c>），把「非法输入」与「合法枚举名」两种意图混在一起。
    /// 这里按名称白名单校验，非枚举名一律拒绝。
    /// </remarks>
    private static bool TryParseByName<TEnum>(string? raw, out TEnum value)
        where TEnum : struct, Enum
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            value = default;
            return false;
        }

        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>新建账户请求体。</summary>
/// <param name="Name">账户名称。</param>
/// <param name="Scope">归属范围：<c>Personal</c>（个人，仅本人可用）或 <c>Public</c>（公共）。</param>
/// <param name="Type">账户类型：<c>Ledger</c> / <c>Fund</c> / <c>Liability</c> / <c>Contact</c>。</param>
/// <param name="InitialBalance">期初金额，两位小数以内，负债账户可为负。</param>
public sealed record AccountRequest(string? Name, string? Scope, string? Type, decimal InitialBalance);

/// <summary>修改账户请求体。</summary>
/// <param name="Name">账户名称。</param>
/// <param name="Type">账户类型。</param>
/// <param name="InitialBalance">期初金额。</param>
/// <remarks>刻意不含归属范围与归属人：两者一经创建不可修改。</remarks>
public sealed record AccountUpdateRequest(string? Name, string? Type, decimal InitialBalance);

/// <summary>账户信息（对外暴露）。</summary>
/// <param name="Id">账户主键。</param>
/// <param name="AccountSetId">所属账套主键。</param>
/// <param name="Name">账户名称。</param>
/// <param name="Scope">归属范围，取值 <c>Personal</c> / <c>Public</c>。</param>
/// <param name="Type">账户类型，取值 <c>Ledger</c> / <c>Fund</c> / <c>Liability</c> / <c>Contact</c>。</param>
/// <param name="OwnerUserId">归属人主键；公共账户为 <c>null</c>。</param>
/// <param name="OwnerUsername">归属人用户名；公共账户为 <c>null</c>。</param>
/// <param name="InitialBalance">期初金额。</param>
/// <param name="Balance">余额（派生值，只读）。</param>
/// <param name="IsActive">是否启用；<c>false</c> 表示已停用（软删除）。</param>
/// <param name="CreatedAt">创建时间（UTC）。</param>
public sealed record AccountDto(
    int Id,
    int AccountSetId,
    string Name,
    string Scope,
    string Type,
    int? OwnerUserId,
    string? OwnerUsername,
    decimal InitialBalance,
    decimal Balance,
    bool IsActive,
    DateTime CreatedAt)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="account">账户实体。</param>
    /// <param name="ownerUsername">归属人用户名；公共账户传 <c>null</c>。</param>
    /// <returns>账户 DTO。</returns>
    /// <remarks>
    /// 枚举以**字符串**对外暴露（而非默认的数字）：前端据此映射中文标签，且新增类型时
    /// 不必让前后端共同维护一份数值对照表。
    /// <para>
    /// <see cref="Balance"/> 是**派生值**，当前等于期初金额——数据库中没有余额列，
    /// 流水表尚未落地。流水表落地后，在此处叠加该账户的流水汇总即可；
    /// 无需改动任何调用方，也不会出现「余额列忘了同步」的静默错账。
    /// </para>
    /// </remarks>
    public static AccountDto From(Account account, string? ownerUsername = null) => new(
        account.Id,
        account.AccountSetId,
        account.Name,
        account.Scope.ToString(),
        account.Type.ToString(),
        account.OwnerUserId,
        ownerUsername,
        account.InitialBalance,
        account.InitialBalance,
        account.IsActive,
        // 从 Sqlite 读回的时间为 DateTimeKind.Unspecified，显式标记为 UTC，
        // 保证序列化输出带 Z 后缀、语义不产生歧义（与 AccountSetDto.From 一致）
        DateTime.SpecifyKind(account.CreatedAt, DateTimeKind.Utc));
}
