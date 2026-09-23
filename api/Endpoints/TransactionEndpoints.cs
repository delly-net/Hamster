using System.Globalization;
using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 记账端点（任意已登录用户）：在当前账套内记一笔收入或支出。
/// </summary>
/// <remarks>
/// **交易一律挂在账套下**：本端点先解析当前账套（请求头 <c>X-Account-Set-Id</c>），
/// 未指定账套即拒绝请求——否则会退化成「往任意账套写账」的越权缺口。
/// <para>
/// **可见性判定在本层、不在服务层**：<see cref="ITransactionService"/> 不能注入
/// <see cref="IAccountService"/>（后者已注入前者，反向注入会构成循环依赖），
/// 故目标账户由本端点经 <see cref="IAccountService.FindVisibleAsync"/> 取好后传入。
/// 这与 <see cref="EntryQueryService"/> 可以放心依赖 <see cref="IAccountService"/> 的方向
/// 刚好相反，勿把判定挪进服务层。
/// </para>
/// <para>
/// 收入与支出**共用同一个端点**，类型由请求体的 <c>type</c> 区分：两者的落库动作完全相同
/// （交易 + 借贷两条等额反向明细），差异只在借方向哪边，拆成两个端点只会得到两份近乎相同的代码。
/// </para>
/// </remarks>
public sealed class TransactionEndpoints : IEndpoint
{
    /// <summary>交易摘要最大长度，与 <see cref="Transaction.Summary"/> 的列长一致。</summary>
    private const int SUMMARY_MAX_LENGTH = 128;

    /// <summary>备注最大长度，与 <see cref="Transaction.Remark"/> 的列长一致。</summary>
    private const int REMARK_MAX_LENGTH = 256;

    /// <summary>金额绝对值上限，防止超出 decimal(18,2) 的表示范围（与账户端点同一口径）。</summary>
    private const decimal AMOUNT_ABS_LIMIT = 999_999_999_999.99M;

    /// <summary>
    /// 可由用户记账的交易类型文本，用于错误提示。
    /// 由枚举派生而非手写：新增类型时提示自动跟上，不会出现「代码放行、文案说不能」这类两处漂移。
    /// </summary>
    private static readonly string RECORDABLE_TYPE_HINT =
        string.Join(" / ", Enum.GetValues<TransactionType>().Where(type => type.IsUserRecordable()));

    /// <summary>交易类型校验失败时的字段错误。</summary>
    private static readonly string[] TYPE_ERROR =
        [$"交易类型只能是 {RECORDABLE_TYPE_HINT}（期初余额由系统自动生成，不接受手工记账）"];

    /// <summary>时间参数格式错误时的字段错误。</summary>
    private static readonly string[] TIME_ERROR =
        ["时间格式不正确，应为 ISO 8601 时间（如 2026-09-23T02:00:00Z）"];

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.TRANSACTION_GROUP)
            .WithTags("记账")
            .RequireAuthorization();

        group.MapPost("", async (
                TransactionRequest request,
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

                var errors = new Dictionary<string, string[]>();

                if (!TryResolveType(request.Type, out var type))
                {
                    errors["type"] = TYPE_ERROR;
                }

                ValidateAmount(request.Amount, errors);
                ValidateText(request.Summary, SUMMARY_MAX_LENGTH, "summary", "摘要", errors);
                ValidateText(request.Remark, REMARK_MAX_LENGTH, "remark", "备注", errors, required: false);

                // 时间绑成 string 再自行解析：直接绑 DateTime? 时非法输入只会得到框架的空白 400，
                // 与全站「字段级中文错误」的约定不符；未传则取当前 UTC 时刻（记一笔刚刚发生的账）。
                var hasOccurredAt = TryParseTime(request.OccurredAt, out var occurredAt, errors);

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                // 目标账户必须是当前用户可见的账户。账本账户、他人个人账户、停用后不可见的账户
                // 与「根本不存在的账户」一律得到同一个 404，不泄露存在性（沿用 #35/#38 口径）。
                var account = await accounts.FindVisibleAsync(
                    request.AccountId, accountSet!.Id, actor!.Id, actor.IsAdmin, cancellationToken);

                if (account is null)
                {
                    return Results.NotFound(new { message = "账户不存在" });
                }

                var transaction = await transactions.RecordIncomeExpenseAsync(
                    account,
                    type,
                    request.Amount,
                    hasOccurredAt ? occurredAt : DateTime.UtcNow,
                    request.Summary!.Trim(),
                    string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim(),
                    actor.Id,
                    cancellationToken);

                return Results.Created(
                    $"{ApiPathConst.TRANSACTION_GROUP}/{transaction.Id}",
                    TransactionDto.From(transaction, account));
            })
            .WithName("RecordTransaction")
            .WithSummary("记一笔收入或支出")
            .WithDescription(
                "在当前账套内记一笔收入或支出，**真实落库**并即时影响所选账户的余额。" +
                "一笔交易由**借贷两条等额反向的明细**构成：收入记「目标账户借方 + 系统账本账户贷方」，" +
                "支出记「目标账户贷方 + 系统账本账户借方」，复式配平（借方合计 == 贷方合计）由此天然成立。" +
                "对手方为该账套的系统账本账户（Ledger 类型，不存在时自动创建），与期初余额同一口径——" +
                "账本账户对任何人不呈现，用户既不需要也无需选择它。" +
                $"交易类型只能是 {RECORDABLE_TYPE_HINT}，期初余额（OpeningBalance）由系统在账户创建时自动生成，" +
                "传它会被拒绝。" +
                "amount **恒为正**：增减由 type 表达，不靠金额符号，故负数金额没有语义。" +
                "occurredAt 为**业务发生时间**（UTC，可补记往日的收支），省略即取当前时刻；" +
                "本系统的业务时间一律按 UTC 存储，无时区后缀的输入也按 UTC 解释。" +
                "目标账户须为当前用户可见的账户：不可见账户与不存在的账户一律返回 404，不泄露存在性。" +
                "账户余额是派生值（全部明细的有符号汇总），记账后无需任何额外操作即已生效；" +
                "记完的明细可在 `GET /api/entries` 中按时间区间与账户查到。" +
                "本端点**只有写入**，不提供单笔交易查询：需要逐条明细（含对手方档位）请用 `GET /api/entries`，" +
                "为本就存在的查询能力再开一个近似端点只会多出一条会漂移的读取路径。");
    }

    /// <summary>
    /// 解析本次请求的「操作者 + 当前账套」。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="principal">当前请求的用户主体。</param>
    /// <param name="users">用户服务。</param>
    /// <param name="accountSets">账套服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部通过时失败响应为 <c>null</c>；否则操作者与账套为 <c>null</c> 并给出失败响应。</returns>
    /// <remarks>
    /// 与 <see cref="AccountEndpoints"/> / <see cref="EntryEndpoints"/> 的同名方法同构。
    /// 用户身份以数据库为准（不信任令牌中的声明）。账套未携带请求头时
    /// <c>ResolveCurrentAccountSetAsync</c> 返回的是 <c>(null, null)</c>——
    /// **这不是失败而是「无账套」**，此处显式转成 400：交易必然落在某个账套内，无账套即无记账对象。
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

    /// <summary>校验金额，把错误写入错误字典。</summary>
    /// <param name="amount">原始金额。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    /// <remarks>
    /// 收入/支出的方向由交易类型表达、不靠金额符号，故这里**只接受正数**——
    /// 「−100 的支出」是自相矛盾的输入，应当报错而不是被悄悄解释成一笔收入。
    /// 小数位超过两位则拒绝，避免「提交 1.005 却因入库存两位小数而悄悄变成 1.01」这类无声偏差。
    /// </remarks>
    private static void ValidateAmount(decimal amount, Dictionary<string, string[]> errors)
    {
        if (amount <= 0)
        {
            errors["amount"] = ["金额必须大于 0（收入与支出的方向由交易类型表达，不用金额符号）"];
        }
        else if (decimal.Round(amount, 2) != amount)
        {
            errors["amount"] = ["金额最多两位小数"];
        }
        else if (amount > AMOUNT_ABS_LIMIT)
        {
            errors["amount"] = [$"金额不能超过 {AMOUNT_ABS_LIMIT}"];
        }
    }

    /// <summary>校验文本字段的长度，把错误写入错误字典。</summary>
    /// <param name="raw">原始文本。</param>
    /// <param name="maxLength">允许的最大长度。</param>
    /// <param name="field">字段名，用于错误定位。</param>
    /// <param name="label">字段的中文名，用于错误文案。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    /// <param name="required">是否必填；<c>false</c> 时空值合法（备注）。</param>
    private static void ValidateText(
        string? raw,
        int maxLength,
        string field,
        string label,
        Dictionary<string, string[]> errors,
        bool required = true)
    {
        var trimmed = raw?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            if (required)
            {
                errors[field] = [$"{label}不能为空"];
            }

            return;
        }

        if (trimmed.Length > maxLength)
        {
            errors[field] = [$"{label}不能超过 {maxLength} 位"];
        }
    }

    /// <summary>解析业务发生时间。</summary>
    /// <param name="raw">原始文本；为空表示未指定。</param>
    /// <param name="value">解析结果（UTC）。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    /// <returns>指定了该时间且解析成功时返回 <c>true</c>。</returns>
    /// <remarks>
    /// 无时区后缀的输入按 UTC 解释（<c>AssumeUniversal</c>）：本系统的业务时间一律 UTC 存储，
    /// 把「裸时间」当成服务器本地时区会让同一份输入在不同部署上落到不同时刻。
    /// 与 <see cref="EntryEndpoints"/> 的时间参数解析同一口径。
    /// </remarks>
    private static bool TryParseTime(string? raw, out DateTime value, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = default;
            return false;
        }

        if (!DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out value))
        {
            errors["occurredAt"] = TIME_ERROR;
            return false;
        }

        return true;
    }

    /// <summary>按**名称**解析交易类型，并确认该类型可由用户记账。</summary>
    /// <param name="raw">原始类型文本。</param>
    /// <param name="type">解析结果。</param>
    /// <returns>解析成功且类型可由用户记账时返回 <c>true</c>。</returns>
    /// <remarks>
    /// 与 <c>AccountEndpoints.TryResolveType</c> 同构：先按名称白名单解析，再判可用性。
    /// </remarks>
    private static bool TryResolveType(string? raw, out TransactionType type) =>
        TryParseByName(raw, out type) && type.IsUserRecordable();

    /// <summary>按**名称**解析枚举，大小写不敏感。</summary>
    /// <typeparam name="TEnum">目标枚举类型。</typeparam>
    /// <param name="raw">原始字符串。</param>
    /// <param name="value">解析结果。</param>
    /// <returns>解析成功返回 <c>true</c>。</returns>
    /// <remarks>
    /// 刻意不用 <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/>：它对纯数字串同样返回成功
    /// （<c>"3"</c> 会被解析为 <c>Expense</c>），把「非法输入」与「合法枚举名」两种意图混在一起。
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

/// <summary>记账请求体。</summary>
/// <param name="Type">
/// 交易类型：<c>Income</c>（收入）或 <c>Expense</c>（支出）。
/// **不含 <c>OpeningBalance</c>**：期初余额由系统在账户创建时自动生成，传它会被拒绝
/// （见 <c>TransactionTypeExtensions.IsUserRecordable</c>）。
/// </param>
/// <param name="AccountId">目标账户主键，须为当前用户可见的账户（收入使其余额增加、支出使其减少）。</param>
/// <param name="Amount">金额，**必须大于 0**，两位小数以内；方向由 <paramref name="Type"/> 表达。</param>
/// <param name="OccurredAt">
/// 业务发生时间（ISO 8601，UTC），可补记往日的收支；**省略即取当前时刻**。
/// </param>
/// <param name="Summary">交易摘要，必填，128 位以内。</param>
/// <param name="Remark">备注，可选，256 位以内。</param>
public sealed record TransactionRequest(
    string? Type,
    int AccountId,
    decimal Amount,
    string? OccurredAt,
    string? Summary,
    string? Remark);

/// <summary>交易（对外暴露）。</summary>
/// <param name="Id">交易主键。</param>
/// <param name="AccountSetId">所属账套主键。</param>
/// <param name="Type">交易类型，取值 <c>Income</c> / <c>Expense</c>。</param>
/// <param name="OccurredAt">业务发生时间（UTC，ISO 8601）。</param>
/// <param name="Summary">交易摘要。</param>
/// <param name="Remark">备注；无备注时为 <c>null</c>。</param>
/// <param name="AccountId">本次记账的目标账户主键（用户选定的那个账户）。</param>
/// <param name="AccountName">目标账户名称。</param>
/// <param name="CreatedAt">落库时间（UTC，ISO 8601）。</param>
/// <remarks>
/// 枚举一律**以字符串**对外，前端据此映射中文标签，前后端不共同维护数值对照表。
/// <para>
/// 不含明细与对手方：本 DTO 只描述「记了哪一笔」。对手方恒为该账套的系统账本账户，
/// 它不对任何用户呈现，把它放进响应只会多一个用户无法理解也无法操作的字段；
/// 需要逐条明细（含对手方档位）请用 <c>GET /api/entries</c>。
/// </para>
/// </remarks>
public sealed record TransactionDto(
    int Id,
    int AccountSetId,
    string Type,
    DateTime OccurredAt,
    string Summary,
    string? Remark,
    int AccountId,
    string AccountName,
    DateTime CreatedAt)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="transaction">交易实体。</param>
    /// <param name="account">本次记账的目标账户（调用方已取得，避免为取名再查一次库）。</param>
    /// <returns>交易 DTO。</returns>
    public static TransactionDto From(Transaction transaction, Account account) => new(
        transaction.Id,
        transaction.AccountSetId,
        transaction.Type.ToString(),
        // 从 Sqlite 读回的时间为 DateTimeKind.Unspecified，显式标记为 UTC，
        // 保证序列化输出带 Z 后缀、语义不产生歧义（与 AccountDto.From 一致）
        DateTime.SpecifyKind(transaction.OccurredAt, DateTimeKind.Utc),
        transaction.Summary,
        transaction.Remark,
        account.Id,
        account.Name,
        DateTime.SpecifyKind(transaction.CreatedAt, DateTimeKind.Utc));
}
