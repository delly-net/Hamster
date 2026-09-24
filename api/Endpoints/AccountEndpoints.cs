using System.Globalization;
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

    /// <summary>
    /// 可由用户指定的账户类型文本，用于错误提示。
    /// 由枚举派生而非手写：新增类型时提示自动跟上，不会出现「代码放行、文案说不能」这类两处漂移。
    /// </summary>
    private static readonly string ASSIGNABLE_TYPE_HINT =
        string.Join(" / ", Enum.GetValues<AccountType>().Where(type => type.IsUserAssignable()));

    /// <summary>账户类型校验失败时的字段错误。文案统一在这里写一次，新建与修改两条路径共用。</summary>
    private static readonly string[] TYPE_ERROR =
        [$"账户类型只能是 {ASSIGNABLE_TYPE_HINT}（账本账户由系统自动创建，不接受手工指定）"];

    /// <summary>
    /// 期初时间允许的「未来」容差。
    /// </summary>
    /// <remarks>
    /// 前端把期初时间默认填成**当前时间**，而本机时钟与服务器时钟必有偏差（NTP 校时窗口内的秒级
    /// 偏差、用户手工改过系统时间都会造成），严格的「晚于此刻即拒」会让「默认值直接提交」这种
    /// 最常见用法偶发 400。故留一段容差；真正的笔误（如填成 2030 年）远超容差，仍会被拦下。
    /// <para>
    /// 前端 <c>AccountView.vue</c> 的 <c>OPENING_AT_FUTURE_TOLERANCE_MS</c> 是同一口径的另一份声明，
    /// 改这里必须同步改那处。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan OPENING_AT_FUTURE_TOLERANCE = TimeSpan.FromMinutes(5);

    /// <summary>期初时间解析失败时的字段错误。</summary>
    private static readonly string[] OPENING_AT_ERROR = ["期初时间格式不正确"];

    /// <summary>期初时间晚于当前时间时的字段错误。</summary>
    private static readonly string[] OPENING_AT_FUTURE_ERROR = ["期初时间不能晚于当前时间"];

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
                ITransactionService transactions,
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

                // 余额 = 该账户全部明细的有符号汇总。期初余额已是一笔落库的期初分录（+期初金额），
                // 故**不再额外叠加 initial_balance**——叠加会把期初金额重复计一次。
                // 一次性取回全部账户的汇总，避免逐账户查询的 N+1。
                var balances = await transactions.SumSignedAmountsAsync(
                    accountSet.Id,
                    [.. visible.Select(item => item.Account.Id)],
                    cancellationToken);

                return Results.Ok(visible
                    .Select(item => AccountDto.From(
                        item.Account,
                        item.OwnerUsername,
                        balances.GetValueOrDefault(item.Account.Id)))
                    .ToArray());
            })
            .WithName("ListAccounts")
            .WithSummary("账户列表")
            .WithDescription(
                "返回当前账套内当前用户可见的账户，按主键升序。管理员可见账套内全部账户（含他人个人账户）；" +
                "普通用户只见公共账户与自己创建的个人账户。默认只返回启用的账户，includeInactive=true 时含已停用的。" +
                "**账本账户（Ledger）对任何人不呈现**（管理员同样看不到）：它由系统在期初入账时自动创建，" +
                "只作复式配平的对手方，界面呈现它只会多出一个无法理解也无法操作的汇总项。" +
                "balance 为派生值（该账户全部交易明细的有符号汇总），不是数据库中的列。");

        group.MapPost("", async (
                AccountRequest request,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IAccountService accounts,
                ICurrencyService currencies,
                ITransactionService transactions,
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

                if (!TryResolveType(request.Type, out var type))
                {
                    errors["type"] = TYPE_ERROR;
                }

                ValidateBalance(request.InitialBalance, errors);

                // 期初时间可选：不传即「未指定」，退回账户建档时刻（与本次改动前的行为一致）
                var openingAt = ValidateOpeningAt(request.OpeningAt, errors);

                // 币种须是**存在的启用币种**：停用币种不接受新绑定，否则「停用」就挡不住新数据继续引用它。
                // 此处只判存在性，不把币种名称回填进错误文案——币种可被管理员改名，嵌进文案的旧名会随之过期
                if (!await currencies.ExistsActiveAsync(request.CurrencyCode, cancellationToken))
                {
                    errors["currencyCode"] = ["请选择有效的币种"];
                }

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
                    openingAt,
                    request.CurrencyCode!,
                    actor!.Id,
                    cancellationToken);

                // 余额照样走汇总、不因「刚建好必然等于期初金额」而直接回填 initialBalance：
                // 派生口径只留一条，任何捷径都会在边界（如期初金额为 0）上与原口径分叉
                var balances = await transactions.SumSignedAmountsAsync(
                    accountSet.Id,
                    [created.Id],
                    cancellationToken);

                return Results.Created(
                    $"{ApiPathConst.ACCOUNT_GROUP}/{created.Id}",
                    AccountDto.From(
                        created,
                        ownerUserId is null ? null : actor.Username,
                        balances.GetValueOrDefault(created.Id)));
            })
            .WithName("CreateAccount")
            .WithSummary("新建账户")
            .WithDescription(
                "在当前账套内新建账户。个人账户的归属人强制为当前登录者（请求体无需也无法指定归属人）；" +
                "名称在「账套 + 归属范围」内不区分大小写唯一，重复返回 409。" +
                $"账户类型只能是 {ASSIGNABLE_TYPE_HINT}：账本账户由系统自动创建，不接受手工指定。" +
                "**currencyCode 必填**，须为存在的启用币种（见 GET /api/currencies）；" +
                "币种是账户的计价单位，**一经创建不可修改**，需要换币种应停用后重新创建。" +
                "期初金额会同时落成一笔期初交易（借/贷各一条明细），对手方为**该币种**的期初账本账户" +
                "（每个币种各有一个，不存在时自动创建）；期初金额为 0 时不写分录。" +
                "**openingAt 可选**，是这笔期初余额的业务时刻，直接落成那笔期初交易的 occurredAt；" +
                "不传时取账户创建时刻。它晚于当前时间会被拒绝；期初金额为 0 时不写分录，openingAt 也随之无落点。");

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

                return await accounts.UpdateAsync(account, name, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("UpdateAccount")
            .WithSummary("修改账户名称")
            .WithDescription(
                "修改账户名称，**本端点只改名称**。归属范围、归属人与所属账套一经创建不可修改" +
                "（个人 → 公共等于把私有数据公开给全账套，故不提供该能力）。" +
                "**账户类型同样不可修改**：类型是账户的分类身份，既有流水都按它归类，换类型等于给历史流水换一套解释；" +
                "类型不可改还顺带消灭了「先建资金账户再改成账本账户」这条绕过路径——" +
                "请求体因此只有 name，传 type 也不会被读取。" +
                "**期初金额亦不可修改**：它已落成一笔期初交易，调整余额应记一笔余额调整交易，" +
                "而非改写既成的期初。");

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

    /// <summary>校验并解析期初时间。空表示「未指定」，此时不报错、返回 <c>null</c>。</summary>
    /// <param name="raw">请求体中的期初时间文本（ISO 8601，可空）。</param>
    /// <param name="errors">字段级错误字典。</param>
    /// <returns>可用的期初时间（UTC）；未指定或非法时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 解析口径与 <c>TransactionEndpoints.TryParseTime</c> **同构**：<c>InvariantCulture</c> +
    /// <c>AdjustToUniversal | AssumeUniversal</c>，即不带时区后缀的文本按 UTC 解释。
    /// 本助手与它各自持有一份是既有模式（同 <c>TryResolveType</c>）——两条业务线的错误字段名不同，
    /// 强行合并只会让「错误落在哪个字段」这件事绕一圈。
    /// </remarks>
    private static DateTime? ValidateOpeningAt(string? raw, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            errors["openingAt"] = OPENING_AT_ERROR;
            return null;
        }

        // 期初是账户记账的起点。未来时间的期初会让账户「当下余额就已经是期初金额」
        // ——余额是全部明细的有符号汇总、与业务时间无关，故未来期初没有任何「还没发生」的缓冲，
        // 语义上直接相悖。见 OPENING_AT_FUTURE_TOLERANCE 关于容差的说明。
        if (parsed > DateTime.UtcNow + OPENING_AT_FUTURE_TOLERANCE)
        {
            errors["openingAt"] = OPENING_AT_FUTURE_ERROR;
            return null;
        }

        return parsed;
    }

    /// <summary>按**名称**解析账户类型，并确认该类型可由用户指定。</summary>
    /// <param name="raw">原始类型文本。</param>
    /// <param name="type">解析结果。</param>
    /// <returns>解析成功且类型可由用户指定时返回 <c>true</c>。</returns>
    /// <remarks>
    /// 校验两件事：文本必须是枚举名（见 <see cref="TryParseByName{TEnum}"/>），
    /// 且该类型必须可由用户指定（见 <see cref="AccountTypeExtensions.IsUserAssignable"/>）。
    /// **现仅服务新建路径**：修改端点已不接收类型（类型一经创建不可修改），
    /// 故不必再考虑「靠改类型把既有账户变成账本账户」那条路径——它已随类型不可改而消失。
    /// </remarks>
    private static bool TryResolveType(string? raw, out AccountType type) =>
        TryParseByName(raw, out type) && type.IsUserAssignable();

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
/// <param name="Type">
/// 账户类型：<c>Fund</c> / <c>Liability</c> / <c>Contact</c>。
/// **不含 <c>Ledger</c>**：账本账户由系统自动创建，传它会被拒绝（见 <c>AccountTypeExtensions.IsUserAssignable</c>）。
/// </param>
/// <param name="InitialBalance">期初金额，两位小数以内，负债账户可为负。</param>
/// <param name="CurrencyCode">
/// 币种代码（ISO 4217，如 <c>CNY</c>），必填且须为存在的启用币种（见 <c>GET /api/currencies</c>）。
/// 大小写不敏感，入库统一大写。
/// </param>
/// <param name="OpeningAt">
/// 期初时间（ISO 8601，可空）。它是这笔期初余额的**业务时刻**，落为期初交易的 <c>occurredAt</c>。
/// 不传或传空串表示「未指定」，退回账户创建时刻（与新增本字段前的行为一致）；
/// 解析口径与记账端点的 <c>occurredAt</c> 一致（无时区后缀按 UTC 解释）；
/// 晚于当前时间超过容差会被拒绝（见 <c>OPENING_AT_FUTURE_TOLERANCE</c>）。
/// </param>
/// <remarks>
/// <see cref="OpeningAt"/> **只在期初金额非 0 时有落点**：期初金额为 0 时不写期初分录，
/// 期初时间也随之无处可落。账户表刻意不存「期初时间」列——它已经活在期初分录的
/// <c>occurred_at</c> 上，另立一列就是同一事实两处存储（见 <c>Account</c> 的类头注释）。
/// </remarks>
public sealed record AccountRequest(
    string? Name,
    string? Scope,
    string? Type,
    decimal InitialBalance,
    string? CurrencyCode,
    string? OpeningAt);

/// <summary>修改账户请求体。</summary>
/// <param name="Name">账户名称。</param>
/// <remarks>
/// 刻意不含归属范围与归属人：两者一经创建不可修改。
/// 也刻意不含期初金额：它已落成一笔期初交易，改写它等于篡改既成事实（见更新端点的说明）。
/// 更刻意不含账户类型：类型是账户的分类身份，既有流水都按它归类，故一经创建同样不可修改；
/// 字段不在这里，**请求里带上它也不会被读取**。
/// <para>
/// 同样不含币种：币种是账户的计价单位，既有流水都按它记账，中途改币种等于给历史金额换一套计价单位
/// （一笔「100」在人民币下是一百元、在美元下是一百美元）。需要换币种时应停用后重新创建。
/// </para>
/// </remarks>
public sealed record AccountUpdateRequest(string? Name);

/// <summary>账户信息（对外暴露）。</summary>
/// <param name="Id">账户主键。</param>
/// <param name="AccountSetId">所属账套主键。</param>
/// <param name="Name">账户名称。</param>
/// <param name="Scope">归属范围，取值 <c>Personal</c> / <c>Public</c>。</param>
/// <param name="Type">
/// 账户类型，取值 <c>Fund</c> / <c>Liability</c> / <c>Contact</c>。
/// <c>Ledger</c> 是合法枚举值但**不会出现在本端点的返回中**——账本账户对任何人不呈现。
/// </param>
/// <param name="OwnerUserId">归属人主键；公共账户为 <c>null</c>。</param>
/// <param name="OwnerUsername">归属人用户名；公共账户为 <c>null</c>。</param>
/// <param name="InitialBalance">期初金额。创建后不可修改。</param>
/// <param name="Balance">余额（派生值，只读）。</param>
/// <param name="CurrencyCode">
/// 币种代码（如 <c>CNY</c>），**恒为大写**。创建后不可修改。
/// 前端据此把账户按币种分组/过滤，并用 <c>GET /api/currencies</c> 下发的名称渲染中文标签——
/// 本 DTO 不回传币种中文名，避免同一份对照表在前后端各存一份。
/// </param>
/// <param name="IsSystem">是否为系统自动创建的内置账户（当前即期初账本账户）。</param>
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
    string CurrencyCode,
    bool IsSystem,
    bool IsActive,
    DateTime CreatedAt)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="account">账户实体。</param>
    /// <param name="ownerUsername">归属人用户名；公共账户传 <c>null</c>。</param>
    /// <param name="balance">
    /// 余额，由 <c>ITransactionService.SumSignedAmountsAsync</c> 汇总得出。
    /// </param>
    /// <returns>账户 DTO。</returns>
    /// <remarks>
    /// 枚举以**字符串**对外暴露（而非默认的数字）：前端据此映射中文标签，且新增类型时
    /// 不必让前后端共同维护一份数值对照表。
    /// <para>
    /// <see cref="Balance"/> 是**派生值**，数据库中没有余额列：
    /// 等于该账户全部交易明细的有符号汇总（借方为正、贷方为负），
    /// 其中期初分录本身就是「+期初金额」，故**不再叠加 <see cref="InitialBalance"/>**。
    /// 期初金额为 0 的账户不写期初分录，其汇总恒为 0，与期初金额一致。
    /// </para>
    /// <para>
    /// 余额由调用方算好后传入（而非在此处查询），是为了让列表端点能用**一次**分组查询
    /// 取回整页账户的余额，避免逐行查询的 N+1。
    /// </para>
    /// </remarks>
    public static AccountDto From(Account account, string? ownerUsername, decimal balance) => new(
        account.Id,
        account.AccountSetId,
        account.Name,
        account.Scope.ToString(),
        account.Type.ToString(),
        account.OwnerUserId,
        ownerUsername,
        account.InitialBalance,
        balance,
        account.CurrencyCode,
        account.IsSystem,
        account.IsActive,
        // 从 Sqlite 读回的时间为 DateTimeKind.Unspecified，显式标记为 UTC，
        // 保证序列化输出带 Z 后缀、语义不产生歧义（与 AccountSetDto.From 一致）
        DateTime.SpecifyKind(account.CreatedAt, DateTimeKind.Utc));
}
