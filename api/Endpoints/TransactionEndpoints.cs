using System.Globalization;
using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 记账端点（任意已登录用户）：在当前账套内记一笔收入、支出或转账。
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
/// 收入、支出与转账**共用同一个端点**，类型由请求体的 <c>type</c> 区分：三者的落库动作完全相同
/// （交易 + 借贷两条等额反向明细），差异只在借方向哪边与几处校验，拆成多个端点只会得到几份
/// 近乎相同的代码。转账在请求体形态上的差异只有一处——<c>counterpartyAccountId</c> 由可选变为必填，
/// 见 <see cref="ResolveTransferCounterpartyAsync"/>。
/// </para>
/// </remarks>
public sealed class TransactionEndpoints : IEndpoint
{
    /// <summary>交易摘要最大长度，与 <see cref="Transaction.Summary"/> 的列长一致。</summary>
    private const int SUMMARY_MAX_LENGTH = 128;

    /// <summary>备注最大长度，与 <see cref="Transaction.Remark"/> 的列长一致。</summary>
    private const int REMARK_MAX_LENGTH = 256;

    /// <summary>分类名最大长度，与 <see cref="Category.Name"/> 的列长一致。</summary>
    private const int CATEGORY_NAME_MAX_LENGTH = 32;

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

    /// <summary>
    /// 可作为转账端点的账户类型文本，用于错误提示。
    /// 由枚举派生而非手写：新增账户类型时提示自动跟上，理由同 <see cref="RECORDABLE_TYPE_HINT"/>。
    /// </summary>
    private static readonly string TRANSFER_ACCOUNT_TYPE_HINT =
        string.Join(" / ", Enum.GetValues<AccountType>().Where(type => type.IsTransferAccount()));

    /// <summary>转账未指定转入账户时的字段错误。</summary>
    private static readonly string[] TRANSFER_COUNTERPARTY_ERROR =
        ["转账必须指定转入账户（counterpartyAccountId），不能像收入/支出那样留空落账本账户"];

    /// <summary>转账传了对手方账户名时的字段错误。</summary>
    private static readonly string[] TRANSFER_NAME_ERROR =
        ["转账的转入账户必须从候选中选定，不支持按账户名新建——自动创建的是往来账户，而转账只允许 " + TRANSFER_ACCOUNT_TYPE_HINT];

    /// <summary>转出与转入为同一账户时的字段错误。</summary>
    private static readonly string[] TRANSFER_SAME_ACCOUNT_ERROR =
        ["转出账户与转入账户不能是同一个账户：两条明细会相互抵消，记了等于没记"];

    /// <summary>时间参数格式错误时的字段错误。</summary>
    private static readonly string[] TIME_ERROR =
        ["时间格式不正确，应为 ISO 8601 时间（如 2026-09-23T02:00:00Z）"];

    /// <summary>分类主键在当前账套内不存在时的字段错误。</summary>
    /// <remarks>
    /// 分类**没有可见性维度**，分不出「不存在」与「无权访问」，
    /// 故此处用 400 而非账户路径上的 404——两者在分类语境下本就是同一件事，
    /// 且 400 可与其余请求体校验的错误**合并返回**，用户一次就能看到全部问题。
    /// </remarks>
    private static readonly string[] CATEGORY_ERROR =
        ["所选分类在当前账套内不存在，请重新选择或改用分类名"];

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
                ICurrencyService currencies,
                ICategoryService categories,
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

                var hasType = TryResolveType(request.Type, out var type);
                if (!hasType)
                {
                    errors["type"] = TYPE_ERROR;
                }

                // 转账的两端都必须是真实存在的账户，故它不接受按名新建对手方：
                // 那条路径会自动创建一个 Contact 往来账户，直接绕开「两端都必须是资金/负债账户」的限制。
                // 在解析账户之前就拦下——这条与账户数据无关，纯请求体形态问题。
                if (hasType && type == TransactionType.Transfer && !string.IsNullOrWhiteSpace(request.CounterpartyName))
                {
                    errors["counterpartyName"] = TRANSFER_NAME_ERROR;
                }

                ValidateAmount(request.Amount, errors);
                ValidateText(request.Summary, SUMMARY_MAX_LENGTH, "summary", "摘要", errors);
                ValidateText(request.Remark, REMARK_MAX_LENGTH, "remark", "备注", errors, required: false);
                // 分类可选（留空即「未分类」），但给了名字就得在列长以内——超长的名字要在建之前拦下
                ValidateText(request.CategoryName, CATEGORY_NAME_MAX_LENGTH, "categoryName", "分类名", errors, required: false);

                // 币种须是**存在的启用币种**：停用币种不接受新记账，
                // 否则「停用」就挡不住新数据继续引用它（与账户新建同一口径）
                if (!await currencies.ExistsActiveAsync(request.CurrencyCode, cancellationToken))
                {
                    errors["currencyCode"] = ["请选择有效的币种"];
                }

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

                // 交易币种必须与目标账户的币种一致——这是「非相同币种账户无法交易」的落点之一。
                // 前端已按币种过滤候选，此处是防绕过：直接构造请求即可提交任意组合
                if (!string.Equals(account.CurrencyCode, request.CurrencyCode!.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["currencyCode"] = [$"所选账户的币种是 {account.CurrencyCode}，与交易币种不一致"],
                    });
                }

                Account? counterparty;

                if (hasType && type == TransactionType.Transfer)
                {
                    // 转账走独立的解析路径：它不接受「留空落账本账户」，也不接受按名新建对手方
                    var (resolved, transferFailure) = await ResolveTransferCounterpartyAsync(
                        request, account, accountSet!, actor!.Id, actor.IsAdmin, accounts, cancellationToken);

                    if (transferFailure is not null)
                    {
                        return transferFailure;
                    }

                    counterparty = resolved;
                }
                else
                {
                    var (resolved, counterpartyFailure) = await ResolveCounterpartyAsync(
                        request, accountSet!, actor!, account.CurrencyCode, accounts, cancellationToken);

                    if (counterpartyFailure is not null)
                    {
                        return counterpartyFailure;
                    }

                    counterparty = resolved;
                }

                // 分类在所有校验之后解析：它可能**按名自动创建**（有副作用），
                // 放在校验闸门之前会让一个注定被拒的请求也在分类表里留下痕迹
                var (category, categoryFailure) = await ResolveCategoryAsync(
                    request, accountSet!.Id, categories, cancellationToken);

                if (categoryFailure is not null)
                {
                    return categoryFailure;
                }

                var transaction = await transactions.RecordUserTransactionAsync(
                    account,
                    counterparty,
                    category,
                    type,
                    request.Amount,
                    hasOccurredAt ? occurredAt : DateTime.UtcNow,
                    request.Summary!.Trim(),
                    string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim(),
                    actor.Id,
                    cancellationToken);

                return Results.Created(
                    $"{ApiPathConst.TRANSACTION_GROUP}/{transaction.Id}",
                    TransactionDto.From(transaction, account, counterparty, category));
            })
            .WithName("RecordTransaction")
            .WithSummary("记一笔收入、支出或转账")
            .WithDescription(
                "在当前账套内记一笔收入、支出或转账，**真实落库**并即时影响所选账户的余额。" +
                "一笔交易由**借贷两条等额反向的明细**构成：收入记「收入账户借方 + 对手方贷方」，" +
                "支出记「支出账户贷方 + 对手方借方」，转账记「转出账户贷方 + 转入账户借方」，" +
                "复式配平（借方合计 == 贷方合计）由此天然成立。" +
                "**type = Transfer 时的专有约束**：counterpartyAccountId **必填**（转账没有「款项来自/去往账套之外」" +
                "这一说），不接受 counterpartyName（按名自动创建的是往来账户，而转账只允许 " + TRANSFER_ACCOUNT_TYPE_HINT + "）；" +
                "accountId 与 counterpartyAccountId 的类型都必须满足转账账户限制，否则 400；两者不能是同一个账户；" +
                "**不支持跨币种转账**，两端币种必须一致（与收支同一口径）。" +
                "**对手方由调用方指定**（counterpartyAccountId 或 counterpartyName）：" +
                "两者皆空即「未指定」，此时落回该账套内**该币种**的系统账本账户（Ledger 类型，不存在时自动创建），" +
                "语义是「款项来自/去往账套之外」；指定了则是一笔**两个真实账户之间的转账**，账本账户完全不参与。" +
                "counterpartyName 命不中既有可见账户时会**自动创建为个人往来账户**（期初金额 0，故不写期初分录）。" +
                "账本账户对任何人不呈现，用户既不需要也无需选择它。" +
                "**分类可选**（categoryId 或 categoryName，两者皆空即「未分类」）：" +
                "与对手方同一取舍——给了主键按主键取，取不到即 400（分类按账套隔离，越界的主键在库里不该存在）；" +
                "只给了名字时，命中既有分类就用它（**含已停用的**，手工指名即归到它上面，不另建同名的），" +
                "否则**自动创建**（这正是「记账时顺手建分类」的入口），名字 32 位以内。" +
                "分类挂在**交易**而非明细上——一笔转账只带一个分类，因为「这笔账因何而发生」是整笔的属性。" +
                $"交易类型只能是 {RECORDABLE_TYPE_HINT}，期初余额（OpeningBalance）由系统在账户创建时自动生成，" +
                "传它会被拒绝。" +
                "amount **恒为正**：增减由 type 表达，不靠金额符号，故负数金额没有语义。" +
                "**currencyCode 必填**，须为存在的启用币种，且**须与两个账户的币种一致**——" +
                "跨币种交易被拒绝（400）：把两种货币的金额裸加总会得到一个没有意义的数。" +
                "occurredAt 为**业务发生时间**（UTC，可补记往日的收支），省略即取当前时刻；" +
                "本系统的业务时间一律按 UTC 存储，无时区后缀的输入也按 UTC 解释。" +
                "目标账户与对手方账户均须为当前用户可见的账户：不可见账户与不存在的账户一律返回 404，不泄露存在性。" +
                "账户余额是派生值（全部明细的有符号汇总），记账后无需任何额外操作即已生效；" +
                "记完的明细可在 `GET /api/entries` 中按时间区间与账户查到。" +
                "本端点**只有写入**，不提供单笔交易查询：需要逐条明细（含对手方档位）请用 `GET /api/entries`，" +
                "为本就存在的查询能力再开一个近似端点只会多出一条会漂移的读取路径。");
    }

    /// <summary>
    /// 解析本次记账的对手方账户。
    /// </summary>
    /// <param name="request">记账请求体。</param>
    /// <param name="accountSet">当前账套。</param>
    /// <param name="actor">当前操作者。</param>
    /// <param name="currencyCode">交易币种（已与目标账户核对一致）。</param>
    /// <param name="accounts">账户服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 对手方账户（**为 <c>null</c> 表示「未指定」**，由服务层落回该币种的系统账本账户）
    /// 与失败响应；成功时失败响应为 <c>null</c>。
    /// </returns>
    /// <remarks>
    /// 三级解析，优先级从高到低：
    /// <list type="number">
    ///   <item>给了主键 → 按主键取。<b>主键比名称精确</b>，故与名称同时给出时以它为准。</item>
    ///   <item>给了名称且命中既有可见账户 → 用它。</item>
    ///   <item>给了名称但不存在 → **自动创建为个人往来账户**（期初金额 0）。</item>
    ///   <item>两者皆无 → 「未指定」，返回 <c>null</c>。</item>
    /// </list>
    /// 与名解析复用 <see cref="IAccountService.FindVisibleByNameAsync"/> 的可见性口径，
    /// 不用「按名全库查」——那会按名命中一个用户看不见的账户，并把它直接写进一笔真实交易。
    /// <para>
    /// **自动创建前不再单独做重名校验**：<see cref="IAccountService.FindVisibleByNameAsync"/> 的可见集合
    /// （公共账户 ∪ 本人个人账户）恰好覆盖了「个人账户重名」可能命中的全部行——若该范围内已有同名账户，
    /// 上一步就已命中并直接返回。再查一次只会让「找到了吗」有两个答案来源。
    /// </para>
    /// </remarks>
    private static async Task<(Account? Account, IResult? Failure)> ResolveCounterpartyAsync(
        TransactionRequest request,
        AccountSet accountSet,
        User actor,
        string currencyCode,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (request.CounterpartyAccountId is { } counterpartyId)
        {
            var byId = await accounts.FindVisibleAsync(
                counterpartyId, accountSet.Id, actor.Id, actor.IsAdmin, cancellationToken);

            if (byId is null)
            {
                return (null, Results.NotFound(new { message = "对手方账户不存在" }));
            }

            return string.Equals(byId.CurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase)
                ? (byId, null)
                : (null, CurrencyMismatch(byId.CurrencyCode, currencyCode, "对手方账户"));
        }

        var name = request.CounterpartyName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            // 未指定对手方：交给服务层落回该币种的系统账本账户
            return (null, null);
        }

        var existing = await accounts.FindVisibleByNameAsync(
            accountSet.Id, actor.Id, actor.IsAdmin, name, cancellationToken);

        if (existing is not null)
        {
            return string.Equals(existing.CurrencyCode, currencyCode, StringComparison.OrdinalIgnoreCase)
                ? (existing, null)
                : (null, CurrencyMismatch(existing.CurrencyCode, currencyCode, "对手方账户"));
        }

        // 不存在即创建为**个人往来账户**：期初金额恒为 0，故不会写出期初分录，
        // 自动创建的账户不会凭空多出一笔期初交易。
        // 归属范围取 Personal（用户临时输入的对手方，多半只是「这个人/这家店」，不该让全账套共用）
        var created = await accounts.CreateAsync(
            accountSet.Id,
            name,
            AccountScope.Personal,
            AccountType.Contact,
            0,
            currencyCode,
            actor.Id,
            cancellationToken);

        return (created, null);
    }

    /// <summary>
    /// 解析本次记账的分类。
    /// </summary>
    /// <param name="request">记账请求体。</param>
    /// <param name="accountSetId">当前账套主键。</param>
    /// <param name="categories">分类服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分类（**为 <c>null</c> 表示「未分类」**，是合法状态）与失败响应；成功时失败响应为 <c>null</c>。</returns>
    /// <remarks>
    /// 三级解析，优先级从高到低：
    /// <list type="number">
    ///   <item>给了主键 → 按主键取。<b>主键比名称精确</b>，故与名称同时给出时以它为准（与对手方同一取舍）；
    ///   取不到则 400——分类按账套隔离，跨账套的主键在库里不该存在。</item>
    ///   <item>给了名称且命中既有分类 → 用它。</item>
    ///   <item>给了名称但不存在 → **自动创建**（这就是「手工输入即建分类」的落点）。</item>
    ///   <item>两者皆无 → 「未分类」，返回 <c>null</c>。</item>
    /// </list>
    /// <para>
    /// **按名解析不限于启用分类**（<see cref="ICategoryService.FindByNameAsync"/> 刻意连停用的一并返回）：
    /// 用户既然一字不差地打出了这个名字，意图就是归到那个分类上；
    /// 此时若因它被停用而另建一个同名分类，历史流水就裂成了两份。
    /// 停用挡的是「从候选里被选中」，不是「被手工指名」。
    /// </para>
    /// <para>
    /// 这里**不需要**账户路径那样的可见性判定：分类没有归属人、也没有可见性维度
    /// （见 <see cref="Category"/>），账套内的分类对所有成员一视同仁。
    /// </para>
    /// </remarks>
    private static async Task<(Category? Category, IResult? Failure)> ResolveCategoryAsync(
        TransactionRequest request,
        int accountSetId,
        ICategoryService categories,
        CancellationToken cancellationToken)
    {
        if (request.CategoryId is { } categoryId)
        {
            var byId = await categories.FindAsync(accountSetId, categoryId, cancellationToken);

            return byId is null
                ? (null, CategoryProblem(CATEGORY_ERROR))
                : (byId, null);
        }

        var name = request.CategoryName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return (null, null);
        }

        var existing = await categories.FindByNameAsync(accountSetId, name, cancellationToken);

        // 不存在即创建：记账时顺手建分类是这个能力的**主用途**，不是兜底。
        // 不预先判重名：FindByNameAsync 已覆盖同一范围（账套 + 不区分大小写），命中就返回了
        return existing is not null
            ? (existing, null)
            : (await categories.CreateAsync(accountSetId, name, cancellationToken), null);
    }

    /// <summary>构造分类校验失败的字段级 400。</summary>
    /// <param name="errors">字段错误文案。</param>
    /// <returns>400 响应。</returns>
    private static IResult CategoryProblem(string[] errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["categoryId"] = errors });

    /// <summary>构造「账户币种与交易币种不一致」的字段级错误响应。</summary>
    /// <param name="actual">账户实际所属的币种代码。</param>
    /// <param name="expected">交易声明的币种代码。</param>
    /// <param name="label">出错方在提示中的中文称谓。</param>
    /// <returns>400 响应。</returns>
    private static IResult CurrencyMismatch(string actual, string expected, string label) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["currencyCode"] = [$"{label}的币种是 {actual}，与交易币种 {expected} 不一致"],
        });

    /// <summary>
    /// 解析转账的转入账户。
    /// </summary>
    /// <param name="request">记账请求体。</param>
    /// <param name="fromAccount">转出账户（已取得，即请求体的 <c>accountId</c>）。</param>
    /// <param name="accountSet">当前账套。</param>
    /// <param name="actorId">当前操作者主键。</param>
    /// <param name="actorIsAdmin">当前操作者是否系统管理员。</param>
    /// <param name="accounts">账户服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>转入账户与失败响应；成功时失败响应为 <c>null</c>。</returns>
    /// <remarks>
    /// **与 <see cref="ResolveCounterpartyAsync"/> 刻意分开**：那条路径支持「留空落该币种系统账本账户」
    /// 与「按名自动创建个人往来账户」，两者对转账都无意义甚至有害——留空会让转账落成「转给账套之外」，
    /// 按名新建出来的往来账户则直接绕开「两端都必须是资金/负债账户」的限制。两条路径的准入条件不同，
    /// 合并成一个方法只会得到一串按类型分支的 if。
    /// <para>
    /// 转入账户的可见性口径与转出账户完全一致（<see cref="IAccountService.FindVisibleAsync"/>）：
    /// 不可见账户与不存在的账户一律返回 404，不泄露存在性（沿用 #35/#38 口径）。
    /// </para>
    /// <para>
    /// 账户类型判定用 <see cref="AccountTypeExtensions.IsTransferAccount"/>：两端的错误文案由它派生，
    /// 端点里不手写类型清单。
    /// </para>
    /// </remarks>
    private static async Task<(Account? Counterparty, IResult? Failure)> ResolveTransferCounterpartyAsync(
        TransactionRequest request,
        Account fromAccount,
        AccountSet accountSet,
        int actorId,
        bool actorIsAdmin,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        var toAccountId = request.CounterpartyAccountId;
        if (toAccountId is null)
        {
            return (null, Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["counterpartyAccountId"] = TRANSFER_COUNTERPARTY_ERROR,
            }));
        }

        // 自转自的账是两条明细相互抵消的空交易，记了等于没记（收入/支出只在前端拦，转账后端一并拦）
        if (toAccountId.Value == fromAccount.Id)
        {
            return (null, Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["counterpartyAccountId"] = TRANSFER_SAME_ACCOUNT_ERROR,
            }));
        }

        var toAccount = await accounts.FindVisibleAsync(
            toAccountId.Value, accountSet.Id, actorId, actorIsAdmin, cancellationToken);

        if (toAccount is null)
        {
            return (null, Results.NotFound(new { message = "转入账户不存在" }));
        }

        if (!fromAccount.Type.IsTransferAccount())
        {
            return (null, TransferAccountTypeProblem(fromAccount, "accountId", "转出账户"));
        }

        if (!toAccount.Type.IsTransferAccount())
        {
            return (null, TransferAccountTypeProblem(toAccount, "counterpartyAccountId", "转入账户"));
        }

        // 跨币种转账被拒绝——与收支同一口径：把两种货币的金额裸加总会得到一个没有意义的数。
        // 前端已按币种过滤两端候选，此处是防绕过：直接构造请求即可提交任意组合
        if (!string.Equals(toAccount.CurrencyCode, fromAccount.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            return (null, CurrencyMismatch(toAccount.CurrencyCode, fromAccount.CurrencyCode, "转入账户"));
        }

        return (toAccount, null);
    }

    /// <summary>
    /// 构造「账户类型不能作为转账端点」的字段级 400。
    /// </summary>
    /// <param name="account">类型不合规的账户。</param>
    /// <param name="field">请求体中该账户对应的字段名（<c>accountId</c> / <c>counterpartyAccountId</c>）。</param>
    /// <param name="label">该端点的中文称谓（转出账户 / 转入账户）。</param>
    /// <returns>400 响应。</returns>
    private static IResult TransferAccountTypeProblem(Account account, string field, string label) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [$"{label}「{account.Name}」的类型是 {account.Type}，转账只允许 {TRANSFER_ACCOUNT_TYPE_HINT}"],
        });

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
    /// 增减方向由交易类型表达、不靠金额符号，故这里**只接受正数**——
    /// 「−100 的支出」是自相矛盾的输入，应当报错而不是被悄悄解释成一笔收入。
    /// 小数位超过两位则拒绝，避免「提交 1.005 却因入库存两位小数而悄悄变成 1.01」这类无声偏差。
    /// </remarks>
    private static void ValidateAmount(decimal amount, Dictionary<string, string[]> errors)
    {
        if (amount <= 0)
        {
            errors["amount"] = ["金额必须大于 0（金额恒为正，增减由交易类型表达，不用金额符号）"];
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
/// 交易类型：<c>Income</c>（收入）、<c>Expense</c>（支出）或 <c>Transfer</c>（转账）。
/// **不含 <c>OpeningBalance</c>**：期初余额由系统在账户创建时自动生成，传它会被拒绝
/// （见 <c>TransactionTypeExtensions.IsUserRecordable</c>）。
/// </param>
/// <param name="AccountId">
/// 主账户主键，须为当前用户可见的账户：收入时它是收入账户（余额增加），
/// 支出时是支出账户、转账时是**转出账户**（余额减少）。
/// </param>
/// <param name="Amount">金额，**必须大于 0**，两位小数以内；增减方向由 <paramref name="Type"/> 表达。</param>
/// <param name="OccurredAt">
/// 业务发生时间（ISO 8601，UTC），可补记往日的收支；**省略即取当前时刻**。
/// </param>
/// <param name="Summary">交易摘要，必填，128 位以内。</param>
/// <param name="Remark">备注，可选，256 位以内。</param>
/// <param name="CurrencyCode">
/// 交易币种（ISO 4217，如 <c>CNY</c>），必填且须为存在的启用币种。
/// **须与 <paramref name="AccountId"/> 所属账户的币种一致**（见 <c>Account.CurrencyCode</c>）。
/// </param>
/// <param name="CounterpartyAccountId">
/// 对手方账户主键，可选。用户从候选账户中**选中**时传它。
/// 与 <paramref name="CounterpartyName"/> 同时给出时**以本字段为准**（主键比名称精确）。
/// <para>
/// <c>type = Transfer</c> 时它**必填且语义为「转入账户」**：转账的两端都是真实账户，
/// 没有「款项来自/去往账套之外」这一说，故不接受留空；两端类型都须满足
/// <c>AccountTypeExtensions.IsTransferAccount</c>，且不能是同一个账户。
/// </para>
/// </param>
/// <param name="CounterpartyName">
/// 对手方账户名称，可选。用户**手工输入**（未命中候选）时传它；不存在则自动创建为个人往来账户。
/// 与 <paramref name="CounterpartyAccountId"/> 均为空即「未指定对手方」，此时落回该币种的系统账本账户。
/// <para>
/// <c>type = Transfer</c> 时**不接受本字段**（400）：按名自动创建的是往来账户，
/// 而转账只允许资金账户与负债账户，接受它等于绕开该限制。
/// </para>
/// </param>
/// <param name="CategoryId">
/// 分类主键，可选。用户从候选中**选中**时传它。
/// 与 <paramref name="CategoryName"/> 同时给出时**以本字段为准**（主键比名称精确）；
/// 取不到（不属于当前账套）则 400。
/// <para>
/// 与 <paramref name="CategoryName"/> 均为空即「未分类」，是合法状态。
/// </para>
/// </param>
/// <param name="CategoryName">
/// 分类名，可选。用户**手工输入**（未命中候选）时传它；**不存在则自动创建**，
/// 这就是「记账时顺手建分类」的入口。32 位以内，与 <c>Category.Name</c> 同长。
/// <para>
/// 按名解析**不限于启用分类**：手工指名一个已停用的分类会归到它上面，而不是另建一个同名的。
/// </para>
/// </param>
public sealed record TransactionRequest(
    string? Type,
    int AccountId,
    decimal Amount,
    string? OccurredAt,
    string? Summary,
    string? Remark,
    string? CurrencyCode,
    int? CounterpartyAccountId,
    string? CounterpartyName,
    int? CategoryId,
    string? CategoryName);

/// <summary>交易（对外暴露）。</summary>
/// <param name="Id">交易主键。</param>
/// <param name="AccountSetId">所属账套主键。</param>
/// <param name="Type">交易类型，取值 <c>Income</c> / <c>Expense</c> / <c>Transfer</c>。</param>
/// <param name="OccurredAt">业务发生时间（UTC，ISO 8601）。</param>
/// <param name="Summary">交易摘要。</param>
/// <param name="Remark">备注；无备注时为 <c>null</c>。</param>
/// <param name="AccountId">
/// 本次记账的主账户主键（用户选定的那个账户）：收入/支出时是收入/支出账户，转账时是**转出账户**。
/// </param>
/// <param name="AccountName">主账户名称（转账时为转出账户名称）。</param>
/// <param name="CurrencyCode">交易币种代码，恒为大写。</param>
/// <param name="CounterpartyName">
/// 对手方账户名称（转账时为**转入账户名称**）；**未指定对手方**（落系统账本账户）时为 <c>null</c>。
/// </param>
/// <param name="CategoryId">分类主键；**未分类**时为 <c>null</c>。</param>
/// <param name="CategoryName">分类名；**未分类**时为 <c>null</c>。与 <paramref name="CategoryId"/> 同生同灭。</param>
/// <param name="CreatedAt">落库时间（UTC，ISO 8601）。</param>
/// <remarks>
/// 枚举一律**以字符串**对外，前端据此映射中文标签，前后端不共同维护数值对照表。
/// <para>
/// 不含明细：本 DTO 只描述「记了哪一笔」；需要逐条明细（含对手方档位）请用 <c>GET /api/entries</c>。
/// </para>
/// <para>
/// <see cref="CounterpartyName"/> 仅在与账本账户配对时才是 <c>null</c>：账本账户是系统内部账户、
/// 不对任何用户呈现，把它放进响应只会多一个用户无法理解也无法操作的字段；
/// 而用户指定了来源/目标账户时，对手方是**他自己填的那个账户**，回传名称才让「记成了哪一笔」完整。
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
    string CurrencyCode,
    string? CounterpartyName,
    int? CategoryId,
    string? CategoryName,
    DateTime CreatedAt)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="transaction">交易实体。</param>
    /// <param name="account">本次记账的主账户（调用方已取得，避免为取名再查一次库）。</param>
    /// <param name="counterparty">
    /// 对手方账户；**为 <c>null</c> 或为系统账本账户时，出参的对手方名称为 <c>null</c>**。
    /// 转账的对手方是转入账户，必然是一个非系统账户，故名称恒有值。
    /// </param>
    /// <param name="category">
    /// 分类；**为 <c>null</c> 即「未分类」**，此时出参的两个分类字段均为 <c>null</c>。
    /// 由调用方把刚存下的分类实体传进来（它可能正是本次按名新建的），避免为取一个名字再查一次库。
    /// </param>
    /// <returns>交易 DTO。</returns>
    /// <remarks>
    /// <see cref="CategoryId"/> 与 <see cref="CategoryName"/> 由参数 <paramref name="category"/>
    /// 同时给出或同时为 <c>null</c>：分类名是给界面直接显示的，主键是给后续改分类用的，
    /// 前端拿到两者就能既显示、又不必为改名再查一次。
    /// </remarks>
    public static TransactionDto From(
        Transaction transaction,
        Account account,
        Account? counterparty = null,
        Category? category = null) => new(
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
        account.CurrencyCode,
        // 账本账户与非系统账户在这里是同一行代码：账本账户对用户不可见，回传它的名称
        // 等于泄露一个界面上不存在的账户。判据是 IsSystem 而非类型，与账本识别口径一致
        counterparty is { IsSystem: false } ? counterparty.Name : null,
        // 分类恒取自传入的实体而非 transaction.CategoryId 直接回传：两者本应一致，
        // 但从实体取能保证「回传的名字」与「落库的主键」出自同一条记录
        category?.Id,
        category?.Name,
        DateTime.SpecifyKind(transaction.CreatedAt, DateTimeKind.Utc));
}
