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

    /// <summary>标签名最大长度，与 <see cref="Tag.Name"/> 的列长一致。</summary>
    private const int TAG_NAME_MAX_LENGTH = 32;

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

    /// <summary>改账时未给出发生时间时的字段错误。</summary>
    /// <remarks>
    /// 与新建的「省略即取当前时刻」刻意不同：改账是全量替换（空备注即清空备注），
    /// 把缺时间解释成「保持原值」会让同一份契约里并存两套语义。
    /// </remarks>
    private static readonly string[] TIME_REQUIRED_ERROR =
        ["时间不能为空；需要保留原来的时间，请把它原样传回来"];

    /// <summary>试图修改期初余额交易时的字段错误。</summary>
    /// <remarks>
    /// 与 <see cref="TYPE_ERROR"/> 分开：那条说的是「传了不能记的类型」，这条说的是
    /// 「这笔账的类型不支持修改」。文案也不同——用户看到的是他自己那笔期初分录，不是一次非法输入。
    /// </remarks>
    private static readonly string[] OPENING_BALANCE_ERROR =
        ["期初余额由系统在账户创建时生成，不支持修改"];

    /// <summary>分类主键在当前账套内不存在时的字段错误。</summary>
    /// <remarks>
    /// 分类**没有可见性维度**，分不出「不存在」与「无权访问」，
    /// 故此处用 400 而非账户路径上的 404——两者在分类语境下本就是同一件事，
    /// 且 400 可与其余请求体校验的错误**合并返回**，用户一次就能看到全部问题。
    /// </remarks>
    private static readonly string[] CATEGORY_ERROR =
        ["所选分类在当前账套内不存在，请重新选择或改用分类名"];

    /// <summary>标签主键在当前账套内不存在时的字段错误。</summary>
    /// <remarks>
    /// 与 <see cref="CATEGORY_ERROR"/> 同一取舍：标签同样**没有可见性维度**，
    /// 分不出「不存在」与「无权访问」，故用 400 而非账户路径上的 404。
    /// 文案里的「改用标签名」不是客套——手工输入标签名会**自动创建**，
    /// 是用户面对一个失效标签时真正可用的退路。
    /// </remarks>
    private static readonly string[] TAG_ERROR =
        ["所选标签在当前账套内不存在，请重新选择或改用标签名"];

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
                ITagService tags,
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
                // 标签可选（一个不给即「没有标签」），同样只校验名字的列长
                ValidateTagNames(request.TagNames, errors);

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
                        request.CounterpartyAccountId, account, accountSet!, actor!.Id, actor.IsAdmin, accounts, cancellationToken);

                    if (transferFailure is not null)
                    {
                        return transferFailure;
                    }

                    counterparty = resolved;
                }
                else
                {
                    var (resolved, counterpartyFailure) = await ResolveCounterpartyAsync(
                        request.CounterpartyAccountId, request.CounterpartyName, accountSet!, actor!, account.CurrencyCode, accounts, cancellationToken);

                    if (counterpartyFailure is not null)
                    {
                        return counterpartyFailure;
                    }

                    counterparty = resolved;
                }

                // 分类在所有校验之后解析：它可能**按名自动创建**（有副作用），
                // 放在校验闸门之前会让一个注定被拒的请求也在分类表里留下痕迹
                var (category, categoryFailure) = await ResolveCategoryAsync(
                    request.CategoryId, request.CategoryName, accountSet!.Id, categories, cancellationToken);

                if (categoryFailure is not null)
                {
                    return categoryFailure;
                }

                // 标签同理，也在所有校验之后解析：按名给的新标签会被**自动创建**，
                // 放在校验闸门之前会让一个注定被拒的请求在标签表里留下痕迹
                var (tagList, tagFailure) = await ResolveTagsAsync(
                    request.TagIds, request.TagNames, accountSet.Id, tags, cancellationToken);

                if (tagFailure is not null)
                {
                    return tagFailure;
                }

                var transaction = await transactions.RecordUserTransactionAsync(
                    account,
                    counterparty,
                    category,
                    tagList,
                    type,
                    request.Amount,
                    hasOccurredAt ? occurredAt : DateTime.UtcNow,
                    request.Summary!.Trim(),
                    string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim(),
                    actor.Id,
                    cancellationToken);

                return Results.Created(
                    $"{ApiPathConst.TRANSACTION_GROUP}/{transaction.Id}",
                    TransactionDto.From(transaction, account, counterparty, category, tagList));
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
                "**标签可选且可有多个**（tagIds / tagNames，两者皆空即「没有标签」）：" +
                "tagIds 是用户从候选中选中的，任一主键取不到即 400（标签按账套隔离）；" +
                "tagNames 是用户手工输入的，命中既有标签就用它（**含已停用的**），否则**自动创建**（名字 32 位以内）。" +
                "**两者是合并关系而非二选一**（与分类的「主键优先」刻意不同）：" +
                "「从候选里选了一个、又手打了一个」是最常见的用法，按「有主键就忽略名字」处理会让手打的那个静默丢失；" +
                "两者指向同一个标签时按主键去重，只挂一条。" +
                "标签落在**子表**（hamster_transaction_tag）里而非交易表的一列上——" +
                "多值标注塞进一个字符串列，既无法按标签精确筛选，改名后历史也会停在旧名字上；" +
                "标签与交易头、两条明细**同一个事务**写入，故不存在「账记下了、标签没挂上」的半成品。" +
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

        group.MapPut("/{id:int}", async (
                int id,
                TransactionUpdateRequest request,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IAccountService accounts,
                ICategoryService categories,
                ITagService tags,
                ITransactionService transactions,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                // 先判「在不在 + 类型允不允许」，再判「形态认不认识」——**两个问题各有各的响应码，
                // 不能合成一次查询**：期初余额交易没有主账户方向，形态解析（FindEditableAsync）
                // 只能给它 null，于是「不可改」会与「不存在」挤进同一个 404；
                // 而期初行用户在明细页**看得见**它，说它「不存在」是撒谎。
                // 故类型这一关先过，且它必须由**交易头**来判（形态还没解析，拿不到 Type）。
                var transaction = await transactions.FindAsync(id, accountSet!.Id, cancellationToken);

                if (transaction is null)
                {
                    return Results.NotFound(new { message = "账目不存在" });
                }

                // 期初余额交易不接受修改：它的金额恒等于目标账户的期初余额、每账户至多一条。
                // 前端同样不给入口（见 ui 的 isEntryEditable），此处是防绕过；
                // 服务层在写入口上还判一次（见 UpdateUserTransactionAsync），那是最后一道。
                if (!transaction.Type.IsUserRecordable())
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["type"] = OPENING_BALANCE_ERROR,
                    });
                }

                // 待改写的交易及其两个端点账户。不属于当前账套、明细不是「借贷各一条」、
                // 或明细指向的账户已不在库里时一律得到 null，回到 404，不泄露存在性（沿用 #35/#38 口径）
                var editable = await transactions.FindEditableAsync(id, accountSet.Id, cancellationToken);

                if (editable is null)
                {
                    return Results.NotFound(new { message = "账目不存在" });
                }

                var errors = new Dictionary<string, string[]>();

                ValidateAmount(request.Amount, errors);
                ValidateText(request.Summary, SUMMARY_MAX_LENGTH, "summary", "摘要", errors);
                ValidateText(request.Remark, REMARK_MAX_LENGTH, "remark", "备注", errors, required: false);
                // 分类可选（留空即「未分类」），但给了名字就得在列长以内——超长的名字要在建之前拦下
                ValidateText(request.CategoryName, CATEGORY_NAME_MAX_LENGTH, "categoryName", "分类名", errors, required: false);
                // 标签可选：一个不给即「把这笔交易的标签清空」——**是覆盖而非保留**，与备注同一语义
                ValidateTagNames(request.TagNames, errors);

                // 发生时间在改账时**必填**，与新建时「省略即取当前时刻」刻意不同：
                // 本次请求是**全量替换**（空备注即清空备注），若把缺时间解释成「保持原值」，
                // 同一份契约里就会并存两套语义。要保留原时间就把它原样传回来。
                if (!TryParseTime(request.OccurredAt, out var occurredAt, errors))
                {
                    // 格式错误时 TryParseTime 已写过文案，不覆盖它；只有「压根没给时间」才落到这里。
                    // **不能写成 errors["occurredAt"] ??= …**：字典索引器先读后写，
                    // 键不存在时那一次读会抛 KeyNotFoundException（500），而不是给出这条 400 文案
                    if (!errors.ContainsKey("occurredAt"))
                    {
                        errors["occurredAt"] = TIME_REQUIRED_ERROR;
                    }
                }

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                // 新的主账户必须是当前用户可见的账户——与记账同一口径（不可见与不存在同为 404）
                var account = await accounts.FindVisibleAsync(
                    request.AccountId, accountSet.Id, actor!.Id, actor.IsAdmin, cancellationToken);

                if (account is null)
                {
                    return Results.NotFound(new { message = "账户不存在" });
                }

                // 可编辑性闸门：这笔交易的两个端点都必须落在「我这一侧」。
                // 主账户明细的账户必须可见；对手方**可见或是系统账本账户**——
                // 账本账户对任何人的可见性判定都是 null（它对谁都不呈现），却是每一笔用户收支的
                // 合法对手方，按「必须可见」一刀切会把绝大多数收支交易判成不可编辑。
                // 对手方是他人个人账户时拒绝：那笔账的另一半在别人名下，改它等于替别人改账。
                // 与「交易不存在」同响应，不泄露存在性。
                var primaryVisible = await accounts.FindVisibleAsync(
                    editable.PrimaryAccount.Id, accountSet.Id, actor.Id, actor.IsAdmin, cancellationToken);

                var counterpartyVisible = editable.CounterpartyAccount.IsSystem
                    || await accounts.FindVisibleAsync(
                        editable.CounterpartyAccount.Id,
                        accountSet.Id,
                        actor.Id,
                        actor.IsAdmin,
                        cancellationToken) is not null;

                if (primaryVisible is null || !counterpartyVisible)
                {
                    return Results.NotFound(new { message = "账目不存在" });
                }

                // 币种随**新的主账户**走：交易表没有币种列，币种由账户决定。
                // 请求体刻意不带 currencyCode——编辑语境下账户已定，再带一个币种字段
                // 只会多出「币种字段与主账户打架」的 400，是噪音。
                // 对手方的解析与新建**共用同一份实现**（按主键 / 按名 / 按名自动创建），
                // 两条路径的准入条件本就相同，各写一份必然在某一处漂移
                Account? counterparty;

                if (editable.Transaction.Type == TransactionType.Transfer)
                {
                    // 转账同样走独立路径：不接受留空、也不接受按名新建对手方
                    var (resolved, transferFailure) = await ResolveTransferCounterpartyAsync(
                        request.CounterpartyAccountId, account, accountSet, actor.Id, actor.IsAdmin, accounts, cancellationToken);

                    if (transferFailure is not null)
                    {
                        return transferFailure;
                    }

                    counterparty = resolved;
                }
                else
                {
                    var (resolved, counterpartyFailure) = await ResolveCounterpartyAsync(
                        request.CounterpartyAccountId,
                        request.CounterpartyName,
                        accountSet,
                        actor,
                        account.CurrencyCode,
                        accounts,
                        cancellationToken);

                    if (counterpartyFailure is not null)
                    {
                        return counterpartyFailure;
                    }

                    counterparty = resolved;
                }

                // 分类在所有校验之后解析：它可能**按名自动创建**（有副作用），
                // 放在校验闸门之前会让一个注定被拒的请求也在分类表里留下痕迹
                var (category, categoryFailure) = await ResolveCategoryAsync(
                    request.CategoryId, request.CategoryName, accountSet.Id, categories, cancellationToken);

                if (categoryFailure is not null)
                {
                    return categoryFailure;
                }

                var (tagList, tagFailure) = await ResolveTagsAsync(
                    request.TagIds, request.TagNames, accountSet.Id, tags, cancellationToken);

                if (tagFailure is not null)
                {
                    return tagFailure;
                }

                // 改写**整笔交易**（交易头 + 借贷两条明细），而非只改这一行：
                // 复式记账的两条明细恒等额反向，只改一条即账不平。
                // 余额无需任何额外动作——它是明细的派生值，改完下次查询即为新值
                var updated = await transactions.UpdateUserTransactionAsync(
                    editable.Transaction,
                    account,
                    counterparty,
                    category,
                    tagList,
                    request.Amount,
                    occurredAt,
                    request.Summary!.Trim(),
                    string.IsNullOrWhiteSpace(request.Remark) ? null : request.Remark.Trim(),
                    cancellationToken);

                // 取回后到改写前被并发删除（受影响行数为 0）：与一开始就找不到同响应
                if (updated is null)
                {
                    return Results.NotFound(new { message = "账目不存在" });
                }

                return Results.Ok(TransactionDto.From(updated, account, counterparty, category, tagList));
            })
            .WithName("UpdateTransaction")
            .WithSummary("修改一笔已记账的收入、支出或转账")
            .WithDescription(
                "改写一笔已存在的交易。**改的是整笔交易，不是单独一行明细**：" +
                "一笔交易由借贷两条等额反向的明细构成，故两条明细**一并改写**、方向保持不变，" +
                "复式配平（借方合计 == 贷方合计）在改完后仍然成立。" +
                "**账户余额不需要任何额外操作**：余额是全部明细的有符号汇总（派生值，库里没有余额列），" +
                "明细一改，新旧账户的余额下次查询即为新值。" +
                "**交易类型不可改**：它决定两条明细的方向，且「收支互改」在语义上是两笔不同的账——" +
                "故本请求体里**根本没有 type 字段**（不是「收了不理会」，那样用户会以为改成功了）。" +
                "**期初余额（OpeningBalance）的交易不可修改**（400）：它的金额恒等于账户的期初余额、" +
                "且每账户至多一条，改它会让这两条不变量同时失效。" +
                "**可改字段**：accountId（主账户，收入/支出时是收入/支出账户，转账时是**转出账户**）、" +
                "amount、occurredAt、summary、remark、categoryId / categoryName、" +
                "counterpartyAccountId / counterpartyName（语义与新建完全一致：转账时必填且须为 " +
                TRANSFER_ACCOUNT_TYPE_HINT + "，收支留空即落该币种账本账户，按名命不中会新建个人往来账户）。" +
                "**请求体不含 currencyCode**：交易表没有币种列，币种由主账户决定，" +
                "对手方账户的币种必须与主账户一致（否则 400，与新建同一口径）。" +
                "**标签（tagIds / tagNames）是整体替换**：本字段代表这笔交易改完之后**应有的全部标签**，" +
                "原有关联中不在此列的一律被摘掉；两者都为空即「清空标签」——与备注同一语义（覆盖而非保留），" +
                "要保留原标签就把它们原样传回来。解析口径与新建逐条一致（按名命不中会新建、" +
                "命中含已停用、两者合并去重）。" +
                "occurredAt 在本端点**必填**（与新建的「省略即取当前时刻」不同）：本次是全量替换，" +
                "缺字段的含义只能是错误，要保留原时间就原样传回。" +
                "**accountId 与 counterpartyAccountId 沿用新建的全部约束**（账户类型、同账户、可见性、币种）：" +
                "改一笔账与记一笔账在这几点上是同一件事，共用同一份判定。" +
                "**准入条件**：交易须属于当前账套，且其**两条明细挂靠的账户都必须在操作者这一侧**" +
                "（主账户账户须可见；对手方须可见或为系统账本账户）；否则与「交易不存在」同响应 404，不泄露存在性——" +
                "对手方是他人个人账户时，那笔账的另一半在别人名下，改它等于替别人改账。" +
                "**只做 UPDATE，不删旧插新**：交易主键与两条明细主键**保持不变**（明细主键是明细查询的" +
                "稳定排序键，换它会让翻页行序漂移），交易也不新增任何行。" +
                "改完的明细可在 `GET /api/entries` 中按时间区间与账户查到（同一 transactionId 的两行会同步变化）。");
    }

    /// <summary>
    /// 解析本次记账的对手方账户。
    /// </summary>
    /// <param name="counterpartyAccountId">对手方账户主键；未给出时为 <c>null</c>。</param>
    /// <param name="counterpartyName">对手方账户名称；未给出时为 <c>null</c>。</param>
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
    /// <para>
    /// 收两个**散字段**而非整个请求体：记账与改账的请求体是两个不同的记录
    /// （改账的记录里没有 <c>type</c> / <c>currencyCode</c>），共用本方法才不会得到两份解析实现——
    /// 而两份「按主键 / 按名 / 按名自动创建」的解析必然在某一处漂移。
    /// </para>
    /// </remarks>
    private static async Task<(Account? Account, IResult? Failure)> ResolveCounterpartyAsync(
        int? counterpartyAccountId,
        string? counterpartyName,
        AccountSet accountSet,
        User actor,
        string currencyCode,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (counterpartyAccountId is { } counterpartyId)
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

        var name = counterpartyName?.Trim();
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
        // 归属范围取 Personal（用户临时输入的对手方，多半只是「这个人/这家店」，不该让全账套共用）。
        // 期初时间传 null：期初金额为 0 时期初时间本就没有落点，且这条路径上没有用户可选的期初时间。
        var created = await accounts.CreateAsync(
            accountSet.Id,
            name,
            AccountScope.Personal,
            AccountType.Contact,
            0,
            null,
            currencyCode,
            actor.Id,
            cancellationToken);

        return (created, null);
    }

    /// <summary>
    /// 解析本次记账的分类。
    /// </summary>
    /// <param name="categoryId">分类主键；未给出时为 <c>null</c>。</param>
    /// <param name="categoryName">分类名；未给出时为 <c>null</c>。</param>
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
    /// <para>
    /// 收两个**散字段**而非整个请求体，理由同 <see cref="ResolveCounterpartyAsync"/>：
    /// 记账与改账共用同一份三级解析。
    /// </para>
    /// </remarks>
    private static async Task<(Category? Category, IResult? Failure)> ResolveCategoryAsync(
        int? categoryId,
        string? categoryName,
        int accountSetId,
        ICategoryService categories,
        CancellationToken cancellationToken)
    {
        if (categoryId is { } givenId)
        {
            var byId = await categories.FindAsync(accountSetId, givenId, cancellationToken);

            return byId is null
                ? (null, CategoryProblem(CATEGORY_ERROR))
                : (byId, null);
        }

        var name = categoryName?.Trim();
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

    /// <summary>
    /// 解析本次记账要挂的标签集合——<c>tagIds</c> 与 <c>tagNames</c> **合并**成一个去重后的集合。
    /// </summary>
    /// <param name="tagIds">标签主键集合；用户从候选中**选中**时传它。未给出时为 <c>null</c>。</param>
    /// <param name="tagNames">标签名集合；用户**手工输入**时传它。未给出时为 <c>null</c>。</param>
    /// <param name="accountSetId">当前账套主键。</param>
    /// <param name="tags">标签服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 标签集合（**空集合即「没有标签」**，是合法状态——记账时标签可选）与失败响应；
    /// 成功时失败响应为 <c>null</c>。
    /// </returns>
    /// <remarks>
    /// **与分类的「主键优先」刻意不同：这里两条来路是合并而非二选一。** 分类至多一个，
    /// 「按主键取还是按名建」必须有先后；而一笔交易可以有多个标签，
    /// 「从候选里选了『出差』、又手打了一个『报销』」是**最常见的用法**，
    /// 按「有主键就忽略名字」处理会让手打的那个标签静默丢失。
    /// <para>
    /// 去重发生在**两处**，且各自都不能省：
    /// <list type="bullet">
    ///   <item>本方法内按主键去重（<c>seen</c>）：同一次请求里选了同一个标签两次、
    ///   或「选中的标签」与「手打的名字」指向同一个标签，都归成一条。</item>
    ///   <item>按名解析时用 <c>byName</c> 记下**本次请求刚创建/命中的标签**：
    ///   手打两个同名标签（如 <c>["出差","出差"]</c>）时，第二次必须复用第一次的结果，
    ///   否则会在标签表里建出两行同名标签——查重口径是「账套内不区分大小写」，
    ///   同一个请求里建出两份正是它要杜绝的。</item>
    /// </list>
    /// </para>
    /// <para>
    /// **顺序 = 首次出现的顺序**（先 <paramref name="tagIds"/> 后 <paramref name="tagNames"/>）：
    /// 关联行按插入顺序落库，查询侧按关联行主键升序取回，故界面上的标签次序与用户提交的一致。
    /// </para>
    /// <para>
    /// **按名解析不限于启用标签**（<see cref="ITagService.FindByNameAsync"/> 刻意连停用的一并返回）：
    /// 与分类同一口径——用户既然一字不差地打出了这个名字，意图就是归到那个标签上；
    /// 此时若因它被停用而另建一个同名标签，历史流水就裂成了两份。
    /// 停用挡的是「从候选里被选中」，不是「被手工指名」。
    /// </para>
    /// <para>
    /// 这里**不需要**账户路径那样的可见性判定：标签没有归属人、也没有可见性维度
    /// （见 <see cref="Tag"/>），账套内的标签对所有成员一视同仁。
    /// </para>
    /// </remarks>
    private static async Task<(IReadOnlyCollection<Tag>? Tags, IResult? Failure)> ResolveTagsAsync(
        int[]? tagIds,
        string[]? tagNames,
        int accountSetId,
        ITagService tags,
        CancellationToken cancellationToken)
    {
        var resolved = new List<Tag>();
        var seen = new HashSet<int>();

        foreach (var id in tagIds ?? [])
        {
            var byId = await tags.FindAsync(accountSetId, id, cancellationToken);

            if (byId is null)
            {
                return (null, TagProblem(TAG_ERROR));
            }

            if (seen.Add(byId.Id))
            {
                resolved.Add(byId);
            }
        }

        // 本次请求内按名命中的标签，归一化名称 → 实体。**必须在循环外持有**：
        // 它的作用正是跨「同一个名字出现多次」复用结果（见上方 remarks）
        var byName = new Dictionary<string, Tag>(StringComparer.Ordinal);

        foreach (var raw in tagNames ?? [])
        {
            var name = raw?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                // 空串/纯空白项直接跳过：它既不是一次「选中」也不是一个「名字」。
                // 不报错是因为锚点在于「列长」与「个数」没有语义约束——前端也不会发出这种项，
                // 走到这里只可能是手写请求，静默忽略比给一条用户看不懂的 400 更合适
                continue;
            }

            var normalized = name.ToLowerInvariant();

            if (!byName.TryGetValue(normalized, out var tag))
            {
                // 不存在即创建：记账时顺手建标签是这个能力的**主用途**，不是兜底。
                // 不预先判重名：FindByNameAsync 已覆盖同一范围（账套 + 不区分大小写），命中就返回了
                tag = await tags.FindByNameAsync(accountSetId, name, cancellationToken)
                    ?? await tags.CreateAsync(accountSetId, name, cancellationToken);
                byName[normalized] = tag;
            }

            if (seen.Add(tag.Id))
            {
                resolved.Add(tag);
            }
        }

        return (resolved, null);
    }

    /// <summary>构造标签校验失败的字段级 400。</summary>
    /// <param name="errors">字段错误文案。</param>
    /// <returns>400 响应。</returns>
    /// <remarks>
    /// 错误落在 <c>tagIds</c> 而不是 <c>tagNames</c>：文案说的是「所选标签不存在」，
    /// 手工输入的名字不存在会走自动创建、根本不报错，故这条只可能因主键失效而触发。
    /// </remarks>
    private static IResult TagProblem(string[] errors) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["tagIds"] = errors });

    /// <summary>校验标签名集合的每一项，把错误写入错误字典。</summary>
    /// <param name="names">标签名集合；可为 <c>null</c>。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    /// <remarks>
    /// **只校验列长，不校验个数**：用户已确认单笔交易的标签数量不设上限
    /// （标签是多值标注，人为设一个数字只会让用户在想多标的时候改不了账）。
    /// <para>
    /// 空串与纯空白项**不报错**：它们会被 <see cref="ResolveTagsAsync"/> 跳过，
    /// 报一条「标签名不能为空」只会让用户对着一个自己没输入过的项莫名其妙。
    /// </para>
    /// </remarks>
    private static void ValidateTagNames(string[]? names, Dictionary<string, string[]> errors)
    {
        foreach (var raw in names ?? [])
        {
            if ((raw?.Trim().Length ?? 0) > TAG_NAME_MAX_LENGTH)
            {
                errors["tagNames"] = [$"标签名不能超过 {TAG_NAME_MAX_LENGTH} 位"];
                return;
            }
        }
    }

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
    /// <param name="toAccountId">转入账户主键；未给出时为 <c>null</c>。</param>
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
        int? toAccountId,
        Account fromAccount,
        AccountSet accountSet,
        int actorId,
        bool actorIsAdmin,
        IAccountService accounts,
        CancellationToken cancellationToken)
    {
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
/// <param name="TagIds">
/// 要挂到这笔交易上的标签主键集合，可选；用户从候选中**选中**时传它。
/// 其中任一主键在当前账套内取不到即 400（标签按账套隔离，越界的主键在库里不该存在）。
/// </param>
/// <param name="TagNames">
/// 要挂到这笔交易上的标签名集合，可选；用户**手工输入**（未命中候选）时传它，
/// **不存在则自动创建**，这就是「记账时顺手建标签」的入口。每项 32 位以内，与 <c>Tag.Name</c> 同长。
/// <para>
/// **与 <paramref name="TagIds"/> 是合并关系而非二选一**（与分类的「主键优先」刻意不同）：
/// 「从候选里选了一个、又手打了一个」是最常见的用法，按「有主键就忽略名字」处理会让手打的那个静默丢失。
/// 两者指向同一个标签时按主键去重，只挂一条。
/// </para>
/// <para>
/// 按名解析**不限于启用标签**：手工指名一个已停用的标签会归到它上面，而不是另建一个同名的。
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
    string? CategoryName,
    int[]? TagIds,
    string[]? TagNames);

/// <summary>改账请求体。</summary>
/// <param name="AccountId">
/// 主账户主键，须为当前用户可见的账户：收入时它是收入账户（余额增加），
/// 支出时是支出账户、转账时是**转出账户**（余额减少）。与新建时含义相同。
/// </param>
/// <param name="Amount">金额，**必须大于 0**，两位小数以内；增减方向由**交易类型**表达（类型不可改）。</param>
/// <param name="OccurredAt">
/// 业务发生时间（ISO 8601，UTC）。**必填**——本请求是全量替换，
/// 缺字段的含义只能是错误；要保留原来的时间就把它原样传回来（与新建的「省略即取当前时刻」刻意不同）。
/// </param>
/// <param name="Summary">交易摘要，必填，128 位以内。</param>
/// <param name="Remark">备注，可选，256 位以内；**留空即清空备注**（覆盖而非保留）。</param>
/// <param name="CounterpartyAccountId">
/// 对手方账户主键，可选。语义与新建完全一致：交易类型为 <c>Transfer</c> 时它**必填且为转入账户**
/// （两端都须满足转账账户限制、不能是同一个账户）；收支可留空，留空即落回主账户**所属币种**的
/// 系统账本账户（按新主账户的币种取，故改到别的币种账户上也不会跨币种）。
/// <para>与 <paramref name="CounterpartyName"/> 同时给出时**以本字段为准**（主键比名称精确）。</para>
/// </param>
/// <param name="CounterpartyName">
/// 对手方账户名称，可选。用户**手工输入**（未命中候选）时传它；不存在则自动创建为个人往来账户。
/// 与新建同一口径：交易类型为 <c>Transfer</c> 时不接受本字段（400）。
/// </param>
/// <param name="CategoryId">
/// 分类主键，可选。与 <paramref name="CategoryName"/> 同时给出时以本字段为准；
/// 取不到（不属于当前账套）则 400。两者皆空即「未分类」，是合法状态。
/// </param>
/// <param name="CategoryName">分类名，可选；不存在则**自动创建**，32 位以内。</param>
/// <param name="TagIds">
/// 标签主键集合，可选；语义与新建完全一致（取不到即 400）。
/// **整体替换**：本字段代表这笔交易改完之后**应有的全部标签**，
/// 原有关联中不在此列的一律被摘掉。
/// </param>
/// <param name="TagNames">
/// 标签名集合，可选；不存在则**自动创建**，每项 32 位以内。与 <paramref name="TagIds"/> 合并。
/// <para>
/// 两者**都为空即「清空标签」**——是覆盖而非保留（与 <c>Remark</c> 同一语义）：
/// 要保留原标签就把它们原样传回来。这里刻意不做「没传就保持原值」的区分，
/// 否则同一份契约里会并存「全量替换」与「缺省保留」两套语义，
/// 而改账的其余字段（备注、分类）都是前者。
/// </para>
/// </param>
/// <remarks>
/// **刻意不含 <c>Type</c>**：交易类型不可改——它决定两条明细的方向，而「收支互改」在语义上
/// 是两笔不同的账。Minimal API 对多余字段静默忽略，故把不可改字段从契约中**整个删掉**，
/// 才不会出现「用户以为改了、其实被忽略」（同 <c>AccountUpdateRequest</c> 的取舍）。
/// <para>
/// **刻意不含 <c>CurrencyCode</c>**：交易表没有币种列，币种由主账户决定；
/// 新建时该字段是「先选币种再过滤账户候选」的输入，编辑时账户已定，
/// 再带一个币种字段只会多出「币种字段与主账户打架」的 400，是噪音。
/// </para>
/// <para>
/// 与 <see cref="TransactionRequest"/> 是**两个记录而非「同一个去掉两个字段」**：
/// 结构上的差异要在契约层可见，否则「不可改」这件事只存在于注释里。
/// </para>
/// </remarks>
public sealed record TransactionUpdateRequest(
    int AccountId,
    decimal Amount,
    string? OccurredAt,
    string? Summary,
    string? Remark,
    int? CounterpartyAccountId,
    string? CounterpartyName,
    int? CategoryId,
    string? CategoryName,
    int[]? TagIds,
    string[]? TagNames);

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
/// <param name="Tags">
/// 这笔交易挂着的标签（主键 + 名称），**没有标签时为空数组**。
/// 顺序即关联行的落库顺序，与用户提交时的次序一致。
/// </param>
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
    IReadOnlyList<TagRefDto> Tags,
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
    /// <param name="tags">
    /// 这笔交易挂着的标签；**为 <c>null</c> 或空集合即「没有标签」**，此时出参为空数组。
    /// 同样是调用方刚解析/写入的那一份（其中可能有本次按名新建的标签），
    /// 避免为取几个名字再查一次库——与 <paramref name="category"/> 同一取舍。
    /// </param>
    /// <returns>交易 DTO。</returns>
    /// <remarks>
    /// <see cref="CategoryId"/> 与 <see cref="CategoryName"/> 由参数 <paramref name="category"/>
    /// 同时给出或同时为 <c>null</c>：分类名是给界面直接显示的，主键是给后续改分类用的，
    /// 前端拿到两者就能既显示、又不必为改名再查一次。
    /// <para>
    /// 标签同理，且**出参恒为数组而非 <c>null</c>**：标签是多值的，
    /// 「没有标签」在界面上的呈现是一片空白，让前端为它写一个判空分支没有任何信息量，
    /// 空数组与「没有标签」是一一对应的。
    /// </para>
    /// </remarks>
    public static TransactionDto From(
        Transaction transaction,
        Account account,
        Account? counterparty = null,
        Category? category = null,
        IReadOnlyCollection<Tag>? tags = null) => new(
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
        // 标签恒转成数组（null → 空数组），不把「没有标签」编码成 null
        [.. (tags ?? []).Select(TagRefDto.From)],
        DateTime.SpecifyKind(transaction.CreatedAt, DateTimeKind.Utc));
}

/// <summary>交易挂着的标签（标签主键 + 名称）。</summary>
/// <param name="Id">标签主键。</param>
/// <param name="Name">标签名称。</param>
/// <remarks>
/// 与 <see cref="TagDto"/> 分开是刻意的：那个描述的是**字典里的一条标签**
/// （带账套、启用状态、创建时间），此处描述的是**某笔交易上的一个标注**——
/// 界面需要的只有「显示什么名字」与「回传什么主键」，账套与创建时间在这里是噪音。
/// <para>
/// **没有 <c>isActive</c>**：交易挂着的标签可能是已停用的（停用是「不再供新记账选择」，
/// 不是「历史上从未用过」）。前端拿到的标签**一律按现有名字原样呈现**，
/// 不给已停用的标签加灰或划线——用户看的是「这笔账当时标了什么」，不是「这份词汇表现在长什么样」。
/// 停用状态只在标签管理页里呈现。
/// </para>
/// </remarks>
public sealed record TagRefDto(int Id, string Name)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="tag">标签实体。</param>
    /// <returns>标签引用 DTO。</returns>
    public static TagRefDto From(Tag tag) => new(tag.Id, tag.Name);
}
