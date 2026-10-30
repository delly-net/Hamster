using System.Globalization;
using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 账目明细端点（任意已登录用户）：在当前账套内按时间区间与账户查询交易明细。
/// </summary>
/// <remarks>
/// **查询能力只此一份**：首页的「最近交易」面板要的正是本端点的逐条明细，它只多传一个 `order=desc`，
/// 不为那一块界面另开端点——否则可见性收敛、钱账户过滤、对手方分档、分类/标签的批量解析
/// 会在两处各写一遍，迟早漂移出「同一笔账在两页显示得不一样」。
/// </remarks>
/// <remarks>
/// **明细一律挂在账套下**：本端点先解析当前账套（请求头 <c>X-Account-Set-Id</c>），
/// 未指定账套即拒绝请求——否则会退化成「查询全库明细」的越权缺口。
/// <para>
/// 可见性判定收敛在 <see cref="IEntryQueryService"/>（其可见账户又来自 <see cref="IAccountService"/>），
/// 本层只负责参数校验与错误响应。请求里传入的账户主键会与可见账户**求交**，
/// 见查询端点说明。
/// </para>
/// </remarks>
public sealed class EntryEndpoints : IEndpoint
{
    /// <summary>默认每页条数，与前端 <c>ENTRY_PAGE_SIZE</c> 保持一致。</summary>
    private const int DEFAULT_PAGE_SIZE = 50;

    /// <summary>每页条数上限。没有上限时一个请求就能把整本账读进内存。</summary>
    private const int MAX_PAGE_SIZE = 200;

    /// <summary>
    /// 历史摘要候选的默认条数，与前端 <c>SUMMARY_OPTION_LIMIT</c> 保持一致。
    /// </summary>
    private const int DEFAULT_SUMMARY_LIMIT = 50;

    /// <summary>历史摘要候选的条数上限；同 <see cref="MAX_PAGE_SIZE"/>，防一个请求把整本账读进内存。</summary>
    private const int MAX_SUMMARY_LIMIT = 200;

    /// <summary>排序方向：升序（**默认**）。</summary>
    private const string ORDER_ASC = "asc";

    /// <summary>排序方向：倒序。首页「最近交易」面板用它取最新的若干条。</summary>
    private const string ORDER_DESC = "desc";

    /// <summary><c>month</c> 查询参数的格式：<c>YYYY-MM</c>。</summary>
    private const string MONTH_FORMAT = "yyyy-MM";

    /// <summary><c>from</c> / <c>to</c> 日期参数的格式：<c>yyyy-MM-dd</c>（服务器本地日期）。</summary>
    private const string DATE_FORMAT = "yyyy-MM-dd";

    /// <summary>日报表允许的最长日期区间（天，闭区间计数）。</summary>
    /// <remarks>
    /// 区间按日展开：表格一天一行、折线一天一点。没有上限时一个请求就能让前端渲染十年的日期，
    /// 而「日报表」的实际用途远用不到一年以上。366 天给闰年留出余量。
    /// </remarks>
    private const int MAX_REPORT_DAYS = 366;

    /// <summary>月报表默认的月份跨度（月，闭区间计数）：从当前月往前推，含当月。</summary>
    /// <remarks>
    /// 只在**省略参数**时使用；与前端「默认过去 12 个月」保持一致。
    /// </remarks>
    private const int DEFAULT_REPORT_MONTHS = 12;

    /// <summary>月报表允许的最长月份区间（月，闭区间计数）。</summary>
    /// <remarks>
    /// 区间按月份展开：表格一月一行、折线一月一点。36 个月（3 年）给纵向对比留出空间，
    /// 又不至于让一次聚合扫过十年以上的历史。前端同样按 36 拦截，两端拦的是不同的事。
    /// </remarks>
    private const int MAX_REPORT_MONTHS = 36;

    /// <summary>日期参数格式错误时的字段错误。</summary>
    private static readonly string[] DATE_ERROR =
        [$"日期格式不正确，应为 {DATE_FORMAT}（如 2026-10-01）"];

    /// <summary>日期区间非法时的字段错误。</summary>
    private static readonly string[] DATE_RANGE_ERROR = ["结束日期不能早于开始日期"];

    /// <summary>日期区间过长时的字段错误。</summary>
    private static readonly string[] DATE_SPAN_ERROR = [$"日期区间不能超过 {MAX_REPORT_DAYS} 天"];

    /// <summary>月份参数格式错误时的字段错误。</summary>
    private static readonly string[] MONTH_ERROR =
        [$"月份格式不正确，应为 {MONTH_FORMAT}（如 2026-10）"];

    /// <summary>月份区间非法时的字段错误。</summary>
    private static readonly string[] MONTH_RANGE_ERROR = ["结束月份不能早于开始月份"];

    /// <summary>月份区间过长时的字段错误。</summary>
    private static readonly string[] MONTH_SPAN_ERROR = [$"月份区间不能超过 {MAX_REPORT_MONTHS} 个月"];

    /// <summary>时间参数格式错误时的字段错误。</summary>
    private static readonly string[] TIME_ERROR =
        ["时间格式不正确，应为 ISO 8601 时间（如 2026-09-01T00:00:00Z）"];

    /// <summary>排序方向非法时的字段错误。</summary>
    private static readonly string[] ORDER_ERROR =
        [$"排序方向只能是 {ORDER_ASC}（升序）或 {ORDER_DESC}（倒序）"];

    /// <summary>交易类型非法时的字段错误；取值由枚举名现拼，避免两处各写一遍枚举取值。</summary>
    private static readonly string[] TYPE_ERROR =
        [$"交易类型只能是 {string.Join("、", Enum.GetNames<TransactionType>())}"];

    /// <summary>历史摘要候选条数非法时的字段错误。</summary>
    private static readonly string[] SUMMARY_LIMIT_ERROR =
        [$"条数须在 1 到 {MAX_SUMMARY_LIMIT} 之间"];

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.ENTRY_GROUP)
            .WithTags("账目明细")
            .RequireAuthorization();

        group.MapGet("", async (
                string? from,
                string? to,
                int[]? accountIds,
                int[]? tagIds,
                string[]? types,
                int? page,
                int? pageSize,
                string? order,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IEntryQueryService entries,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var errors = new Dictionary<string, string[]>();

                // 时间参数绑成 string 再自行解析：直接绑 DateTime? 时非法输入只会得到框架的空白 400，
                // 与全站「字段级中文错误」的约定不符。
                var hasFrom = TryParseTime(from, out var fromValue, errors, "from");
                var hasTo = TryParseTime(to, out var toValue, errors, "to");

                if (hasFrom && hasTo && fromValue > toValue)
                {
                    errors["to"] = ["结束时间不能早于开始时间"];
                }

                // 页码与页大小给默认值而不要求必传：查询页只带筛选条件，分页由前端按需追加。
                var currentPage = page ?? 1;
                if (currentPage < 1)
                {
                    errors["page"] = ["页码不能小于 1"];
                }

                var currentPageSize = pageSize ?? DEFAULT_PAGE_SIZE;
                if (currentPageSize < 1 || currentPageSize > MAX_PAGE_SIZE)
                {
                    errors["pageSize"] = [$"每页条数须在 1 到 {MAX_PAGE_SIZE} 之间"];
                }

                var descending = IsDescending(order, errors);
                var typeFilter = ParseTypes(types, errors);

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var result = await entries.QueryAsync(
                    accountSet!.Id,
                    actor!.Id,
                    actor.IsAdmin,
                    // 未传即不限该端
                    hasFrom ? fromValue : null,
                    hasTo ? toValue : null,
                    accountIds,
                    tagIds,
                    typeFilter,
                    currentPage,
                    currentPageSize,
                    descending,
                    cancellationToken);

                return Results.Ok(EntryQueryPageDto.From(result));
            })
            .WithName("QueryEntries")
            .WithSummary("账目明细查询")
            .WithDescription(
                "在当前账套内按时间区间与账户查询交易明细，按**业务发生时间**排序分页返回" +
                "（同一时刻按交易主键、再按明细主键，保证翻页结果稳定）。" +
                "排序方向由 `order` 给定：`asc`（默认，升序）或 `desc`（倒序，**三级排序键逐级反向**，" +
                "即升序结果的严格逆序）；大小写不敏感，**未知取值返回 400 而不是静默按升序处理**" +
                "（否则「传了 desc、结果却是升序」无从判断）。" +
                "`desc` 只改方向：行过滤、total、对手方/分类/标签的解析与 `asc` 完全相同。" +
                "**首页「最近交易」面板正是用 `order=desc&pageSize=10` 取最新的 10 条明细**" +
                "——它要的就是本端点的逐条明细，故不另开一个近似端点。" +
                "**行粒度是一条交易明细**：一笔交易由借贷两条明细构成，若两条明细挂在两个不同的所选账户上，" +
                "则它们在结果里各占一行——这正是复式记账的呈现方式。" +
                "amount 恒为正数、direction 为借贷方向，两者是账本的底层事实（库内不存带符号金额）；" +
                "**呈现请用 signedAmount**：它是 amount 按 direction 取符号后的值（借方为正、贷方为负），" +
                "含义是该条明细对它挂靠账户的余额增减——收入/支出分列与正负着色都由它派生，" +
                "换算定义与账户余额汇总共用一处（借方为正、贷方为负）。" +
                "时间区间比较的是 occurred_at（业务发生时间，可补记往日支出），不是落库时间；" +
                "from/to 均为**闭区间**端点，省略即该侧不限。" +
                "accountIds 可重复传参、省略即不限账户；**其中的不可见账户会被静默剔除**" +
                "（不报错、也不返回其明细），与可见账户的交集为空时返回空页——" +
                "若对不可见账户报错，这个参数就成了探测他人账户的探针。" +
                "**明细行只落在钱账户上（资金账户 / 负债账户）**：账本账户对任何人不呈现，" +
                "往来账户记的是「谁欠谁」而不是「钱放在哪」，两者都**不作为明细行出现**" +
                "（往来账户的余额与来往由账户管理页承担）。" +
                "但往来账户照常作为**对手方**出现——「支出 现金 → 老王」的行为「现金」那一行，" +
                "counterpartyKind = Account、counterpartyName = 老王；" +
                "被排除的是「往来账户作为记账主体」，不是「往来账户这个信息」。" +
                "账本账户则只会作为期初分录的对手方，" +
                "以 counterpartyKind = Ledger 的形式被标记（不给主键与名称）。" +
                "**注意**：被排除的行只是不呈现，库内数据一行未动——" +
                "那笔交易的借贷两条明细照旧配平，账户余额也照旧把它们计入。" +
                "**categoryId / categoryName 为交易级属性**（分类挂在交易而非明细上）：" +
                "一笔交易的两条明细会拿到同一个分类，**未分类**时两者均为 null——" +
                "分类是可选的，null 是正常的，不是数据缺失。" +
                "分类名由后端随行下发（分类改名后历史明细自动显示新名字），" +
                "**已停用分类的名称照常给出**：停用是「不再供新记账选择」，不是「历史上从未用过」。" +
                "**tags 为交易级属性**（标签挂在交易而非明细上，落在子表里）：" +
                "一笔交易的两条明细会拿到同一份标签，**没有标签时是空数组**——标签是可选的，空数组是正常的。" +
                "标签名同样由后端随行下发（标签改名后历史明细自动显示新名字），" +
                "**已停用的标签名称照常给出**（与分类同一口径）。" +
                "标签的**次序**即用户当初提交的次序，界面照此呈现即可，不必自己排序。" +
                "tagIds 可重复传参、省略即不限标签；**匹配语义是「任一命中」**而非「全部命中」——" +
                "多选标签的常规意图是「这几类我都想看看」，而不是「同时具备这几个标签的账」。" +
                "与 accountIds 一样，**不属于本账套的标签主键会被静默忽略**（不报错、只是匹配不到），" +
                "否则这个参数就成了探测「某标签是否属于他人账套」的探针；" +
                "tagIds 与 accountIds **同时给出时是「且」的关系**，两个维度各自收窄。" +
                "**types 按交易类型过滤**：取值即交易类型的**枚举名**" +
                "（`Income` 收入 / `Expense` 支出 / `Transfer` 转账 / `OpeningBalance` 期初余额），" +
                "可重复传参（`types=Income&types=Expense` 为「或」）、整体省略即不限类型；" +
                "大小写不敏感、两端空白会被裁掉，**未知取值返回 400 并在 `errors.types` 给出字段级错误**" +
                "（与 `order` 同一取舍：静默忽略会让「传了 types 却没过滤」无从判断）；" +
                "**按数值传值不被接受**——枚举对外一律以字符串收发，前后端不共同维护数值对照表。" +
                "条件写的是**交易头**的类型（与 `from` / `to` 同一张表），不是明细上的字段。" +
                "**记账页「最近 N 次同类型交易」正是用 `types=<本页类型>&order=desc&page=1&pageSize=2N` 取回**：" +
                "过滤必须落在服务端——客户端本地按类型筛同一页数据会被「昨天记的一百笔支出」挤空。" +
                "**账户筛选项请直接用 `GET /api/accounts?includeInactive=true`**：" +
                "明细页的筛选需要含已停用账户（软删除的账户上仍有历史明细），该参数已经提供该能力，" +
                "不另设一套平行接口。" +
                "但该端点**不按类型过滤**——账户管理页需要完整列表，不为此加 `type` 参数；" +
                "因此**为「账目明细」构造账户筛选项的调用方须自行排除 `Contact`**：" +
                "明细行本就不含往来账户，候选若含就成了「选了也查不到」的空档。" +
                "这是**呈现层分组**（同「本接口不呈现往来账户明细」这条数据面规则的配套），" +
                "与「可见性由后端判定」不是一回事。");

        group.MapGet("/summaries", async (
                string[]? types,
                int? limit,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IEntryQueryService entries,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var errors = new Dictionary<string, string[]>();

                var currentLimit = limit ?? DEFAULT_SUMMARY_LIMIT;
                if (currentLimit < 1 || currentLimit > MAX_SUMMARY_LIMIT)
                {
                    errors["limit"] = SUMMARY_LIMIT_ERROR;
                }

                // 与 /api/entries 的 types **共用同一份解析**（含「未知取值 400 而不是静默忽略」的取舍）：
                // 两处各写一遍会漂移成「明细查询报错、摘要候选却悄悄不过滤」
                var typeFilter = ParseTypes(types, errors);

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var result = await entries.ListSummariesAsync(
                    accountSet!.Id,
                    actor!.Id,
                    actor.IsAdmin,
                    typeFilter,
                    currentLimit,
                    cancellationToken);

                return Results.Ok(result.Select(EntrySummaryOptionDto.From).ToArray());
            })
            .WithName("ListEntrySummaries")
            .WithSummary("历史摘要候选")
            .WithDescription(
                "在当前账套内聚合出**用过的交易摘要**（去重），供记账表单与改账弹窗做候选：" +
                "用户记下一笔时可以从上次的写法里选，而不必每次重新敲一遍。" +
                "**这是独立端点而不是 `GET /api/entries` 的一个参数**：后者的行粒度是**明细**且必须分页" +
                "（`pageSize` 上限 200），拿它去重既把整行明细的载荷搬进内存，又只能覆盖最近那一页" +
                "——「上个月记过的摘要」会凭空不在候选里。聚合是查询形状上的差别（`GROUP BY`），" +
                "不是同一件事的两个参数集。" +
                "**口径与 `GET /api/entries` 一致**：只聚合**钱账户**（资金账户 / 负债账户）上有明细的交易，" +
                "可见性同样复用账户服务；否则候选里会出现用户在「账目明细」页根本查不到的账（往来账户上的明细）。" +
                "**两类行都不参与**：往来账户与账本账户作为记账主体的明细本就不在明细页呈现，" +
                "它们的摘要自然也不该成为候选。" +
                "`types` 按**交易类型**过滤，取值与语义与 `GET /api/entries` 的 `types` **完全相同**" +
                "（枚举名、可重复传参、大小写不敏感、未知取值 400 并在 `errors.types` 给出字段级错误）：" +
                "记账页与改账弹窗传的是**本页那一种类型**（收入页只列收入的历史摘要）——" +
                "摘要的可复用性本就随类型走（「工资」是收入摘要、「早餐」是支出摘要），跨类型混排只会让候选变长而不变准。" +
                "省略 `types` 即不限类型（期初余额的摘要也在其中）。" +
                "排序为**最近使用时间倒序**（同一时刻按使用次数降序、再按摘要升序兜底，保证同一份数据两次请求的行序一致）；" +
                "`lastUsedAt` 取的是**业务发生时间**的最大值而不是落库时间——补记往日支出时两者会差很远，" +
                "而用户心里「上次用这条摘要」说的正是那笔账发生的时间。" +
                "`usageCount` 是**笔数**不是明细行数：一笔交易由借贷两条明细构成，数行数会让每一次记账都被计成 2 次。" +
                "`limit` 默认 50、上限 200，越界返回 400 并在 `errors.limit` 给出字段级错误。" +
                "返回项的 `summary` **原样给出用户当初敲下的写法**：不做大小写折叠、也不把去空白后相同的两条合并——" +
                "摘要是自由文本而不是字典项，合并两条写法等于替用户改了账。" +
                "失败约定与 `GET /api/entries` 一致：未带账套 400、未登录 401。");

        group.MapGet("/category-totals", async (
                string? month,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICurrencyService currencies,
                IEntryQueryService entries,
                ILogger<EntryEndpoints> logger,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                if (!TryResolveMonth(month, out var monthStart))
                {
                    return Results.BadRequest(new { message = $"月份格式不正确，应为 {MONTH_FORMAT}" });
                }

                var monthLabel = monthStart.ToString(MONTH_FORMAT, CultureInfo.InvariantCulture);

                // 与首页两张走势图同一条取舍：没有可用币种时返回空结果而不是 404，
                // 前端把「没有币种可看」与「这个月没有发生额」都呈现成空态。
                var currency = await currencies.GetDefaultAsync(cancellationToken);
                if (currency is null)
                {
                    return Results.Ok(new EntryCategoryTotalsResponse(null, monthLabel, [], []));
                }

                // 月份是**服务器本地**的自然月：交易日期的分组口径跟着部署所在地走。
                // 换算成 UTC 后交给查询服务；上界取次月 1 日 0 点（不含）。
                var timeZone = LocalDay.ResolveTimeZone(logger);
                var fromUtc = LocalDay.StartToUtc(monthStart, timeZone);
                var toUtc = LocalDay.StartToUtc(monthStart.AddMonths(1), timeZone);

                var result = await entries.ListCategoryTotalsAsync(
                    accountSet!.Id,
                    actor!.Id,
                    actor.IsAdmin,
                    currency.Code,
                    fromUtc,
                    toUtc,
                    cancellationToken);

                return Results.Ok(EntryCategoryTotalsResponse.From(currency.Code, monthLabel, result));
            })
            .WithName("ListEntryCategoryTotals")
            .WithSummary("收支分类合计")
            .WithDescription(
                "返回当前账套、**系统默认币种**下某个自然月内收入与支出**按分类的合计**，" +
                "供首页的「收入构成 / 支出构成」百分比饼图使用。" +
                "**只统计收入与支出两类交易**（各取主账户方向那一条明细：收入取借方、支出取贷方）：" +
                "转账是账户之间的搬运、没有单侧发生额，期初余额也不是本月发生额，" +
                "两者进入分类占比都会让「这个月的钱花在哪」失真。" +
                "**账户范围是公共账户 + 当前用户本人的个人账户**的资金 / 负债账户；" +
                "即使调用者是管理员也**不包含其他成员的个人账户**——首页的按天记录按用户分行，" +
                "分类占比必须与它同一口径。" +
                "金额恒为正、按分类分组；`categoryId` / `categoryName` **均为 null 表示未分类**，" +
                "那是合法归集结果而不是数据缺失，照常出现在列表里。" +
                "分类名由后端下发且**含已停用分类**（停用是「不再供新记账选择」，不是「历史上从未用过」）。" +
                "排序为**金额降序**，金额相同时按分类主键升序（未分类在同额时排最后）。" +
                "`month` 省略时取**服务器本地的当月**，格式为 `YYYY-MM`，非法取值返回 400；" +
                "月份边界按服务器本地时区换算为 UTC，区间为 **[当月 1 日 0 点, 次月 1 日 0 点)**。" +
                "没有可用币种时返回 `currencyCode = null` 与两个空数组，而不是 404。" +
                "失败约定与 `GET /api/entries` 一致：未带账套 400、未登录 401、无权访问账套 403。");

        group.MapGet("/daily-flows", async (
                string? month,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICurrencyService currencies,
                IEntryQueryService entries,
                ILogger<EntryEndpoints> logger,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                if (!TryResolveMonth(month, out var monthStart))
                {
                    return Results.BadRequest(new { message = $"月份格式不正确，应为 {MONTH_FORMAT}" });
                }

                var monthLabel = monthStart.ToString(MONTH_FORMAT, CultureInfo.InvariantCulture);

                // 与分类合计、首页两张走势图同一条取舍：没有可用币种时返回空结果而不是 404，
                // 前端把「没有币种可看」与「这个月没有发生额」都呈现成空态。
                var currency = await currencies.GetDefaultAsync(cancellationToken);
                if (currency is null)
                {
                    return Results.Ok(new EntryDailyFlowsResponse(null, monthLabel, []));
                }

                // 月份是**服务器本地**的自然月：日期的分组口径跟着部署所在地走。
                // 换算成 UTC 后交给查询服务；上界取次月 1 日 0 点（不含）。
                var timeZone = LocalDay.ResolveTimeZone(logger);
                var fromUtc = LocalDay.StartToUtc(monthStart, timeZone);
                var toUtc = LocalDay.StartToUtc(monthStart.AddMonths(1), timeZone);

                var result = await entries.ListDailyFlowsAsync(
                    accountSet!.Id,
                    actor!.Id,
                    actor.IsAdmin,
                    currency.Code,
                    fromUtc,
                    toUtc,
                    timeZone,
                    cancellationToken);

                return Results.Ok(EntryDailyFlowsResponse.From(currency.Code, monthLabel, result));
            })
            .WithName("ListEntryDailyFlows")
            .WithSummary("按日收支合计")
            .WithDescription(
                "返回当前账套、**系统默认币种**下某个自然月内**按天的收入、支出与净额**，" +
                "供「月报表」页的按日折线图使用。" +
                "**这是实时聚合**：直接统计交易明细，与首页的 `/api/income-expenses/daily` 不同，" +
                "不依赖结算记录，也包含尚未结算的日期。" +
                "**只统计收入与支出两类交易**（各取主账户方向那一条明细：收入取借方、支出取贷方）：" +
                "转账是账户之间的搬运、没有单侧发生额，期初余额也不是本月发生额，" +
                "两者进入走势都会让「这个月每天赚多少、花多少」失真。" +
                "**账户范围是公共账户 + 当前用户本人的个人账户**的资金 / 负债账户；" +
                "即使调用者是管理员也**不包含其他成员的个人账户**——与 `category-totals` 同一口径。" +
                "金额：收入与支出**都是正数**（方向由字段名表达），`netTotal = incomeTotal − expenseTotal`。" +
                "`days` **只含至少有一笔收支的日期**（按本地日期升序）：这是后端「缺日不补零」的口径，" +
                "补零是呈现层的决定（月报表的数据源是实时的，空白日就是真实的 0）。" +
                "`month` 省略时取**服务器本地的当月**，格式为 `YYYY-MM`，非法取值返回 400；" +
                "月份边界按服务器本地时区换算为 UTC，区间为 **[当月 1 日 0 点, 次月 1 日 0 点)**。" +
                "没有可用币种时返回 `currencyCode = null` 与空数组，而不是 404。" +
                "失败约定与 `GET /api/entries` 一致：未带账套 400、未登录 401、无权访问账套 403。");

        group.MapGet("/daily-report", async (
                string? from,
                string? to,
                int[]? accountIds,
                int[]? tagIds,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICurrencyService currencies,
                IEntryQueryService entries,
                ILogger<EntryEndpoints> logger,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var errors = new Dictionary<string, string[]>();

                // 日期按 **yyyy-MM-dd** 解析：日报表的区间由用户直接选日期，而不是选月份再切。
                // 绑成 string 再自行解析的理由同 `GET /api/entries`：非法输入要得到字段级中文错误，
                // 而不是框架的空白 400。
                var hasFrom = TryParseLocalDate(from, out var fromDay, errors, "from");
                var hasTo = TryParseLocalDate(to, out var toDay, errors, "to");

                // 省略即默认「服务器本地当月 1 日 ~ 今天」：与账目明细页的初始区间同一读法，
                // 也避免把当月尚未到来的日子摊进按日表格。
                var now = DateTime.Now;
                var effectiveFrom = hasFrom ? fromDay : new DateTime(now.Year, now.Month, 1);
                var effectiveTo = hasTo ? toDay : now.Date;

                if (errors.Count == 0)
                {
                    if (effectiveFrom > effectiveTo)
                    {
                        errors["to"] = DATE_RANGE_ERROR;
                    }
                    else if ((effectiveTo - effectiveFrom).TotalDays + 1 > MAX_REPORT_DAYS)
                    {
                        errors["to"] = DATE_SPAN_ERROR;
                    }
                }

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var fromLabel = effectiveFrom.ToString(DATE_FORMAT, CultureInfo.InvariantCulture);
                var toLabel = effectiveTo.ToString(DATE_FORMAT, CultureInfo.InvariantCulture);

                // 与分类合计、按日收支同一条取舍：没有可用币种时返回空结果而不是 404。
                var currency = await currencies.GetDefaultAsync(cancellationToken);
                if (currency is null)
                {
                    return Results.Ok(new EntryDailyReportResponse(
                        null,
                        fromLabel,
                        toLabel,
                        [],
                        new EntryCategoryTotalsDto([], []),
                        new EntryTagTotalsDto([], [])));
                }

                // 日期区间是**服务器本地**的闭区间：分组口径跟着部署所在地走。
                // 上界取结束日期的**次日 0 点**（不含），于是「结束日期当天的全部账」都落在区间内。
                var timeZone = LocalDay.ResolveTimeZone(logger);
                var fromUtc = LocalDay.StartToUtc(effectiveFrom, timeZone);
                var toUtc = LocalDay.StartToUtc(effectiveTo.AddDays(1), timeZone);

                var result = await entries.ListDailyReportAsync(
                    accountSet!.Id,
                    actor!.Id,
                    actor.IsAdmin,
                    currency.Code,
                    fromUtc,
                    toUtc,
                    timeZone,
                    accountIds,
                    tagIds,
                    cancellationToken);

                return Results.Ok(EntryDailyReportResponse.Create(
                    currency.Code, fromLabel, toLabel, result));
            })
            .WithName("GetEntryDailyReport")
            .WithSummary("日报表聚合（按日 / 按分类 / 按标签）")
            .WithDescription(
                "返回当前账套、**系统默认币种**下任意日期区间内的三份聚合，供「日报表」页使用：" +
                "按日收支（折线与表格）、按分类合计（两张饼图）、按标签合计（两张饼图）。" +
                "**三份结果由同一次查询派生**：同一批主方向明细行在服务端内存里同时折出三个视角，" +
                "故折线、饼图与表格的口径天然一致，不会出现「一处有、一处没有」。" +
                "**这是实时聚合**：直接统计交易明细，与 `/api/entries/daily-flows` 同源，" +
                "但区间不再限定为自然月、并额外支持账户与标签筛选。" +
                "**只统计收入与支出两类交易**（各取主账户方向那一条明细：收入取借方、支出取贷方）：" +
                "转账是账户之间的搬运、没有单侧发生额，期初余额也不是区间发生额，两者均不计入。" +
                "**账户范围是公共账户 + 当前用户本人的个人账户**的资金 / 负债账户；" +
                "即使调用者是管理员也**不包含其他成员的个人账户**——与 `category-totals` / `daily-flows` 同一口径。" +
                "`accountIds` 可重复传参、省略即不限账户；**不可见账户、非默认币种账户与往来账户会被静默剔除**" +
                "（不报错、也不返回其数据），交集为空时返回全空结果——若对不可见账户报错，这个参数就成了探测他人账户的探针。" +
                "`tagIds` 可重复传参、省略即不限标签，**匹配语义是「任一命中」**；" +
                "与 `accountIds` 同时给出时是「且」的关系，两个维度各自收窄。" +
                "**标签是多值维度**：一笔交易挂多个标签时，其金额在**每个标签下各计一次**（金额不摊分），" +
                "故 `tags` 各标签金额之和可能大于区间收支合计；没有标签的交易归入 `tagId` / `tagName` 均为 null 的" +
                "「未打标签」一项。分类维度仍按交易头那一个分类归集，不存在重复计入；" +
                "`categoryId` / `categoryName` 均为 null 表示「未分类」——两者都是合法的归集结果，不是数据缺失。" +
                "分类名与标签名由后端下发且**含已停用项**（停用是「不再供新记账选择」，不是「历史上从未用过」）。" +
                "`days` **只含至少有一笔收支的日期**（按服务器本地日期升序）：补零是呈现层的决定，" +
                "日报表的数据源是实时的，空白日就是真实的 0。" +
                "`from` / `to` 为 **yyyy-MM-dd** 的服务器本地日期，**闭区间**（含结束当天）；" +
                "省略 `from` 取服务器本地当月 1 日、省略 `to` 取服务器本地今天。" +
                "日期格式非法返回 400 并在 `errors.from` / `errors.to` 给出字段级错误；" +
                "`from` 晚于 `to`、或区间超过 366 天返回 400 并在 `errors.to` 说明原因。" +
                "没有可用币种时返回 `currencyCode = null` 与三份空集合，而不是 404。" +
                "失败约定与 `GET /api/entries` 一致：未带账套 400、未登录 401、无权访问账套 403。");

        // 月报表：与上方日报表同一套口径、同一份聚合内核，只是把时间粒度从「日」换成「月」。
        group.MapGet("/monthly-report", async (
                string? from,
                string? to,
                int[]? accountIds,
                int[]? tagIds,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICurrencyService currencies,
                IEntryQueryService entries,
                ILogger<EntryEndpoints> logger,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var errors = new Dictionary<string, string[]>();

                // 月份按 **yyyy-MM** 解析：月报表的区间由用户直接选月份，而不是选日期再折算。
                // 绑成 string 再自行解析的理由同 `GET /api/entries`：
                // 非法输入要得到字段级中文错误，而不是框架的空白 400。
                var hasFrom = TryParseLocalMonth(from, out var fromMonth, errors, "from");
                var hasTo = TryParseLocalMonth(to, out var toMonth, errors, "to");

                // 省略即默认「当前月往前推 11 个月 ~ 当前月」，共 12 个月（**含当月**）：
                // 与前端默认区间同构，也避免调用方自己再算一次「过去 12 个月」。
                var now = DateTime.Now;
                var currentMonth = new DateTime(now.Year, now.Month, 1);
                var effectiveFrom = hasFrom
                    ? fromMonth
                    : currentMonth.AddMonths(-(DEFAULT_REPORT_MONTHS - 1));
                var effectiveTo = hasTo ? toMonth : currentMonth;

                if (errors.Count == 0)
                {
                    var monthCount = ((effectiveTo.Year - effectiveFrom.Year) * 12)
                        + effectiveTo.Month
                        - effectiveFrom.Month
                        + 1;

                    if (effectiveFrom > effectiveTo)
                    {
                        errors["to"] = MONTH_RANGE_ERROR;
                    }
                    else if (monthCount > MAX_REPORT_MONTHS)
                    {
                        errors["to"] = MONTH_SPAN_ERROR;
                    }
                }

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var fromLabel = effectiveFrom.ToString(MONTH_FORMAT, CultureInfo.InvariantCulture);
                var toLabel = effectiveTo.ToString(MONTH_FORMAT, CultureInfo.InvariantCulture);

                // 与分类合计、按日收支同一条取舍：没有可用币种时返回空结果而不是 404。
                var currency = await currencies.GetDefaultAsync(cancellationToken);
                if (currency is null)
                {
                    return Results.Ok(new EntryMonthlyReportResponse(
                        null,
                        fromLabel,
                        toLabel,
                        [],
                        new EntryCategoryTotalsDto([], []),
                        new EntryTagTotalsDto([], [])));
                }

                // 月份区间是**服务器本地**的闭区间：上界取结束月份的**次月 1 日 0 点**（不含），
                // 于是「结束月份整月的账」都落在区间内；当月尚未结束时自然只有截至今天的数据。
                var timeZone = LocalDay.ResolveTimeZone(logger);
                var fromUtc = LocalDay.StartToUtc(effectiveFrom, timeZone);
                var toUtc = LocalDay.StartToUtc(effectiveTo.AddMonths(1), timeZone);

                var result = await entries.ListMonthlyReportAsync(
                    accountSet!.Id,
                    actor!.Id,
                    actor.IsAdmin,
                    currency.Code,
                    fromUtc,
                    toUtc,
                    timeZone,
                    accountIds,
                    tagIds,
                    cancellationToken);

                return Results.Ok(EntryMonthlyReportResponse.Create(
                    currency.Code, fromLabel, toLabel, result));
            })
            .WithName("GetEntryMonthlyReport")
            .WithSummary("月报表聚合（按月 / 按分类 / 按标签）")
            .WithDescription(
                "返回当前账套、**系统默认币种**下任意月份区间内的三份聚合，供「月报表」页使用：" +
                "按月收支（折线与表格）、按分类合计（两张饼图）、按标签合计（两张饼图）。" +
                "**三份结果由同一次查询派生**：同一批主方向明细行在服务端内存里同时折出三个视角，" +
                "故折线、饼图与表格的口径天然一致，不会出现「一处有、一处没有」。" +
                "**这是实时聚合**：直接统计交易明细，按月分组按**服务器本地时区**在 C# 侧折算" +
                "（不在 SQL 侧按月 GROUP BY），与 `daily-report` 共用同一份取数与可见性实现。" +
                "**只统计收入与支出两类交易**（各取主账户方向那一条明细：收入取借方、支出取贷方）：" +
                "转账是账户之间的搬运、没有单侧发生额，期初余额也不是区间发生额，两者均不计入。" +
                "**账户范围是公共账户 + 当前用户本人的个人账户**的资金 / 负债账户；" +
                "即使调用者是管理员也**不包含其他成员的个人账户**——与 `category-totals` / `daily-report` 同一口径。" +
                "`accountIds` 可重复传参、省略即不限账户；**不可见账户、非默认币种账户与往来账户会被静默剔除**" +
                "（不报错、也不返回其数据），交集为空时返回全空结果——若对不可见账户报错，这个参数就成了探测他人账户的探针。" +
                "`tagIds` 可重复传参、省略即不限标签，**匹配语义是「任一命中」**；" +
                "与 `accountIds` 同时给出时是「且」的关系，两个维度各自收窄。" +
                "**标签是多值维度**：一笔交易挂多个标签时，其金额在**每个标签下各计一次**（金额不摊分），" +
                "故 `tags` 各标签金额之和可能大于区间收支合计；没有标签的交易归入 `tagId` / `tagName` 均为 null 的" +
                "「未打标签」一项。分类维度仍按交易头那一个分类归集，不存在重复计入；" +
                "`categoryId` / `categoryName` 均为 null 表示「未分类」——两者都是合法的归集结果，不是数据缺失。" +
                "分类名与标签名由后端下发且**含已停用项**（停用是「不再供新记账选择」，不是「历史上从未用过」）。" +
                "`months` **只含至少有一笔收支的月份**（按服务器本地月份升序）：补零是呈现层的决定，" +
                "月报表的数据源是实时的，空白月份就是真实的 0。" +
                "`from` / `to` 为 **yyyy-MM** 的服务器本地月份，**闭区间**（含结束月份整月）；" +
                "省略 `from` 取当前月往前推 11 个月、省略 `to` 取当前月。" +
                "月份格式非法返回 400 并在 `errors.from` / `errors.to` 给出字段级错误；" +
                "`from` 晚于 `to`、或区间超过 36 个月返回 400 并在 `errors.to` 说明原因。" +
                "没有可用币种时返回 `currencyCode = null` 与三份空集合，而不是 404。" +
                "失败约定与 `GET /api/entries` 一致：未带账套 400、未登录 401、无权访问账套 403。");
    }

    /// <summary>
    /// 解析 <c>yyyy-MM-dd</c> 日期参数。
    /// </summary>
    /// <param name="value">查询参数原值；<c>null</c> 或空白表示省略。</param>
    /// <param name="day">解析结果（时刻部分为 00:00:00）。</param>
    /// <param name="errors">字段级错误收集器；非法取值时写入 <paramref name="key"/>。</param>
    /// <param name="key">字段名（<c>from</c> / <c>to</c>）。</param>
    /// <returns>成功解析返回 <c>true</c>；省略或格式非法返回 <c>false</c>。</returns>
    /// <remarks>
    /// 用 <see cref="DateTime.TryParseExact(string, string, IFormatProvider, DateTimeStyles, out DateTime)"/>
    /// 而不是 <c>TryParse</c>：后者会把 <c>2026-1-1</c> 之类的写法也当作合法，
    /// 而接口声明的格式只有一个，宽进会让前端两种写法各写一遍。
    /// <para>
    /// **省略与非法都返回 false，区别在是否写错误**：调用方据 <c>errors.ContainsKey</c> 判断
    /// 是否继续（非法时已写错误），省略则各自回落默认值。
    /// </para>
    /// </remarks>
    private static bool TryParseLocalDate(
        string? value,
        out DateTime day,
        Dictionary<string, string[]> errors,
        string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            day = default;
            return false;
        }

        if (!DateTime.TryParseExact(
                value,
                DATE_FORMAT,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            day = default;
            errors[key] = DATE_ERROR;
            return false;
        }

        day = parsed.Date;
        return true;
    }

    /// <summary>
    /// 解析 <c>yyyy-MM</c> 月份参数。
    /// </summary>
    /// <param name="value">查询参数原值；<c>null</c> 或空白表示省略。</param>
    /// <param name="monthStart">解析结果：该月 1 日（时刻部分为 00:00:00）。</param>
    /// <param name="errors">字段级错误收集器；非法取值时写入 <paramref name="key"/>。</param>
    /// <param name="key">字段名（<c>from</c> / <c>to</c>）。</param>
    /// <returns>成功解析返回 <c>true</c>；省略或格式非法返回 <c>false</c>。</returns>
    /// <remarks>
    /// 与 <see cref="TryParseLocalDate"/> 同构，只是粒度换成月：**省略与非法都返回 <c>false</c>，
    /// 区别在是否写错误**——调用方据 <c>errors.ContainsKey</c> 判断是否继续，省略则各自回落默认值。
    /// 用 <c>TryParseExact</c> 而不是 <c>TryParse</c>：接口声明的格式只有一个，
    /// 宽进会让前端两种写法各写一遍。
    /// </remarks>
    private static bool TryParseLocalMonth(
        string? value,
        out DateTime monthStart,
        Dictionary<string, string[]> errors,
        string key)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            monthStart = default;
            return false;
        }

        if (!DateTime.TryParseExact(
                value,
                MONTH_FORMAT,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            monthStart = default;
            errors[key] = MONTH_ERROR;
            return false;
        }

        monthStart = new DateTime(parsed.Year, parsed.Month, 1);
        return true;
    }

    /// <summary>
    /// 解析 <c>month</c> 查询参数；省略时取服务器本地的当月。
    /// </summary>
    /// <param name="month">查询参数原值（<c>YYYY-MM</c>，可为 <c>null</c>）。</param>
    /// <param name="monthStart">解析结果：该月 1 日的本地日期（时刻部分为 00:00:00）。</param>
    /// <returns>解析（或回落）成功返回 <c>true</c>；格式不合法返回 <c>false</c>。</returns>
    /// <remarks>
    /// 与 <c>TotalAssetEndpoints</c> / <c>IncomeExpenseEndpoints</c> 的同名方法逐字相同
    /// （含「用 <c>TryParseExact</c> 而不是 <c>TryParse</c>」与「回落用 <see cref="DateTime.Now"/>」两条取舍）。
    /// 刻意不抽公共方法：三处各自只有十几行，而抽出来就要为「首页口径」造一个共享类型，
    /// 代价大于收益。
    /// </remarks>
    private static bool TryResolveMonth(string? month, out DateTime monthStart)
    {
        if (string.IsNullOrWhiteSpace(month))
        {
            var today = DateTime.Now.Date;
            monthStart = new DateTime(today.Year, today.Month, 1);
            return true;
        }

        if (!DateTime.TryParseExact(
                month,
                MONTH_FORMAT,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            monthStart = default;
            return false;
        }

        monthStart = new DateTime(parsed.Year, parsed.Month, 1);
        return true;
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
    /// 与 <see cref="AccountEndpoints"/> 的同名方法同构。用户身份以数据库为准（不信任令牌中的声明）。
    /// 账套未携带请求头时 <c>ResolveCurrentAccountSetAsync</c> 返回的是 <c>(null, null)</c>——
    /// **这不是失败而是「无账套」**，此处显式转成 400：明细必然落在某个账套内，无账套即无可查对象。
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

    /// <summary>解析排序方向参数。</summary>
    /// <param name="raw">原始文本；为空表示按默认方向（升序）。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    /// <returns>是否倒序；未指定或非法取值时均为 <c>false</c>（非法取值另有 400，见注释）。</returns>
    /// <remarks>
    /// 大小写不敏感（<c>DESC</c> 与 <c>desc</c> 同义）：查询串里的大小写是笔误级的差异，
    /// 为此回 400 只会让人去翻文档，而不是发现自己多按了一次大写键。
    /// <para>
    /// 但**未知取值一律报错，不静默回落到升序**：静默回落会让「我明明传了 desc、结果却是升序」
    /// 无从判断——而这正是本参数唯一可能出错的地方，也是它唯一需要给出的反馈。
    /// </para>
    /// </remarks>
    private static bool IsDescending(string? raw, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // 未指定即默认方向，不算错误
            return false;
        }

        var normalized = raw.Trim();
        if (string.Equals(normalized, ORDER_DESC, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(normalized, ORDER_ASC, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        errors["order"] = ORDER_ERROR;
        return false;
    }

    /// <summary>解析交易类型筛选参数。</summary>
    /// <param name="raw">原始取值（可重复传参）；<c>null</c> 或空数组表示不限类型。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    /// <returns>去重后的交易类型集合；出现非法取值时返回空数组（调用方随后即返回 400）。</returns>
    /// <remarks>
    /// **按枚举名逐个比对白名单**，而不是直接 <c>Enum.TryParse</c>：后者会把 <c>"2"</c> 这类
    /// 数字串也解析成合法取值，等于悄悄允许调用方按数值传值——而本项目的枚举对外一律以**字符串**
    /// 收发，前后端不共同维护数值对照表（数值是直接落库的数据契约，不是接口契约）。
    /// <para>
    /// 大小写不敏感、两端裁空白（与 <see cref="IsDescending"/> 同一宽容度），
    /// 重复传同一个类型静默去重；**空串视为「这一项没给」而跳过**，不算错误。
    /// </para>
    /// <para>
    /// 但**未知取值一律 400 并写 <c>errors.types</c>，不静默忽略**：忽略会让「传了 types、结果却
    /// 没过滤」无从判断——而记账页正是靠这个参数把面板收窄到本页那一种类型，静默忽略会直接表现为
    /// 「收入页里混着支出」。这与 <c>order</c> 的取舍同源。
    /// </para>
    /// </remarks>
    private static TransactionType[] ParseTypes(string[]? raw, Dictionary<string, string[]> errors)
    {
        if (raw is null || raw.Length == 0)
        {
            return [];
        }

        var parsed = new List<TransactionType>();
        foreach (var item in raw)
        {
            var normalized = item?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                continue;
            }

            var matched = Enum.GetNames<TransactionType>()
                .FirstOrDefault(name => string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase));

            if (matched is null)
            {
                errors["types"] = TYPE_ERROR;
                return [];
            }

            var value = Enum.Parse<TransactionType>(matched);
            if (!parsed.Contains(value))
            {
                parsed.Add(value);
            }
        }

        return [.. parsed];
    }

    /// <summary>解析时间参数（UTC，含端点）。</summary>
    /// <param name="raw">原始文本；为空表示不限该端。</param>
    /// <param name="value">解析结果（UTC）。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    /// <param name="field">字段名，用于错误定位。</param>
    /// <returns>需要限制该端时返回 <c>true</c>。</returns>
    /// <remarks>
    /// 无时区后缀的输入按 UTC 解释（<c>AssumeUniversal</c>）：本系统的业务时间一律 UTC 存储，
    /// 把「裸时间」当成服务器本地时区会让同一份输入在不同部署上落到不同区间。
    /// </remarks>
    private static bool TryParseTime(
        string? raw,
        out DateTime value,
        Dictionary<string, string[]> errors,
        string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // 未指定即不限该侧，不算错误
            value = default;
            return false;
        }

        if (!DateTime.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out value))
        {
            errors[field] = TIME_ERROR;
            return false;
        }

        return true;
    }
}

/// <summary>账目明细（对外暴露）。</summary>
/// <param name="Id">明细主键。</param>
/// <param name="TransactionId">所属交易主键。</param>
/// <param name="OccurredAt">业务发生时间（UTC，ISO 8601）。</param>
/// <param name="Summary">交易摘要。</param>
/// <param name="Remark">交易备注；无备注时为 <c>null</c>。</param>
/// <param name="TransactionType">交易类型，取值 <c>OpeningBalance</c>。</param>
/// <param name="AccountId">挂靠账户主键。</param>
/// <param name="AccountName">挂靠账户名称。</param>
/// <param name="Direction">借贷方向，取值 <c>Debit</c> / <c>Credit</c>。</param>
/// <param name="Amount">金额，**恒为正**；方向由 <paramref name="Direction"/> 表达，库内不折算符号。</param>
/// <param name="SignedAmount">
/// 带符号金额：<paramref name="Amount"/> 按其 <paramref name="Direction"/> 取符号后的值
/// （借方为正、贷方为负），含义是「该条明细对它挂靠账户的余额增减了多少」。
/// **界面的主依据**——收入/支出分列、正负着色都取自它；<paramref name="Amount"/> 与
/// <paramref name="Direction"/> 则保留为账本的底层事实。
/// </param>
/// <param name="CounterpartyKind">
/// 对手方档位，取值 <c>None</c>（无对手方明细）/ <c>Account</c>（可见）/ <c>Ledger</c>（系统账本账户）/
/// <c>Hidden</c>（存在但不可见）。
/// </param>
/// <param name="CounterpartyAccountId">对手方账户主键；**仅 <c>Account</c> 档有值**。</param>
/// <param name="CounterpartyName">对手方账户名称；**仅 <c>Account</c> 档有值**。</param>
/// <param name="CategoryId">交易分类主键；**未分类**时为 <c>null</c>。</param>
/// <param name="CategoryName">
/// 交易分类名称；**未分类**时为 <c>null</c>。与 <paramref name="CategoryId"/> 同生同灭。
/// 分类名由后端下发（不是前端自己拼的），改名后历史明细自动显示新名字。
/// </param>
/// <param name="IsPrimary">
/// 这条明细是否挂在**主账户**上（主账户 = 用户记账时选定的那个账户：收入账户 / 支出账户 / 转出账户）。
/// 一笔交易的两条明细里恰有一行为 <c>true</c>（期初余额的行恒为 <c>false</c>）。
/// </param>
/// <param name="Tags">
/// 这笔交易挂着的标签（主键 + 名称），**没有标签时为空数组**；同一笔交易的两条明细拿到同一份。
/// 次序即用户当初提交的次序，照此呈现即可。
/// <para>
/// 与 <see cref="TransactionDto"/> 共用 <see cref="TagRefDto"/> 这一个形状：
/// 「某笔交易上的一个标签」在两个端点上就是同一件事，
/// 各定义一个记录只会让前端为同一份 JSON 写两套类型。
/// </para>
/// </param>
/// <remarks>
/// 枚举一律**以字符串**对外，前端据此映射中文标签，前后端不共同维护数值对照表。
/// 对手方分档而非「给名称或给 null」：<c>Ledger</c> 与 <c>Hidden</c> 都不给主键与名称，
/// 「不可见」这件事本身不携带任何可辨识信息。
/// <para>
/// **分类与标签都不分档**：两者都没有可见性维度（账套内所有成员共用同一份字典），
/// 故直接给出主键与名称，不像对手方那样需要 <c>Ledger</c> / <c>Hidden</c> 这类遮罩档位。
/// </para>
/// <para>
/// <see cref="Tags"/> **不带 <c>isActive</c>**：交易挂着的标签可能是已停用的
/// （停用是「不再供新记账选择」，不是「历史上从未用过」）。本页一律按现有名字原样呈现，
/// 不给已停用的标签加灰或划线——用户看的是「这笔账当时标了什么」，
/// 不是「这份词汇表现在长什么样」。停用状态只在标签管理页里呈现。
/// </para>
/// <para>
/// <see cref="IsPrimary"/> 是为**改账**下发的：一笔交易可能占两行（转账的两端都会呈现），
/// 界面从任一行点开编辑时，都要把「这一行」还原成「主账户 + 对手方」两个端点——
/// 该行是主账户行则 <c>accountId</c> 即主账户，否则主账户是它的对手方。
/// 这个判据（方向是否等于该类型的主账户方向）只定义在后端一处、随本字段下发，
/// **前端不镜像**：它与 <see cref="SignedAmount"/> 同一性质——算错的后果是
/// 「编辑写到了错误的账户上」（数据损坏），不是显示问题，故由后端算好给出。
/// </para>
/// </remarks>
public sealed record EntryDto(
    int Id,
    int TransactionId,
    DateTime OccurredAt,
    string Summary,
    string? Remark,
    string TransactionType,
    int AccountId,
    string AccountName,
    string Direction,
    decimal Amount,
    decimal SignedAmount,
    string CounterpartyKind,
    int? CounterpartyAccountId,
    string? CounterpartyName,
    int? CategoryId,
    string? CategoryName,
    bool IsPrimary,
    TagRefDto[] Tags)
{
    /// <summary>由查询结果构造 DTO。</summary>
    /// <param name="row">明细行。</param>
    /// <returns>明细 DTO。</returns>
    public static EntryDto From(EntryQueryRow row) => new(
        row.Id,
        row.TransactionId,
        // 从 Sqlite 读回的时间为 DateTimeKind.Unspecified，显式标记为 UTC，
        // 保证序列化输出带 Z 后缀、语义不产生歧义（与 AccountDto.From 一致）
        DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc),
        row.Summary,
        row.Remark,
        row.Type.ToString(),
        row.AccountId,
        row.AccountName,
        row.Direction.ToString(),
        row.Amount,
        // 带符号金额在 DTO 层派生而非在 IEntryQueryService 里：后者在注释中承诺
        // 「纯读取、不参与符号换算」，那条承诺继续成立；且这是**出参呈现**的派生值，
        // 与 Direction.ToString() 同级，属于 DTO 的职责。换算定义仍是 EntryDirectionExtensions
        // .SignedAmount 一处（与余额汇总共用）。
        row.Direction.SignedAmount(row.Amount),
        row.CounterpartyKind.ToString(),
        row.CounterpartyAccountId,
        row.CounterpartyName,
        // 分类是交易级的属性，同一笔交易的两条明细会拿到同一个分类——
        // 这不是重复，而是「一笔转账只应有一个分类」在明细视图下的如实呈现
        row.CategoryId,
        row.CategoryName,
        // 原样透传：判据在查询侧算得（见 EntryQueryRow.IsPrimary 的说明），
        // DTO 不重算——两处各算一遍正是它要避免的漂移
        row.IsPrimary,
        // 服务层的 EntryTag 与端点的 TagRefDto 形状相同但类型不同：依赖方向是端点 → 服务，
        // 让服务层引用端点的 DTO 会把这个方向反过来。转换点只有这一处，成本是一个 Select
        [.. row.Tags.Select(tag => new TagRefDto(tag.Id, tag.Name))]);
}

/// <summary>历史摘要的一条候选（对外暴露）。</summary>
/// <param name="Summary">摘要原文（用户当初敲下的写法，原样给出，不做任何归一）。</param>
/// <param name="LastUsedAt">最近一次使用时间（UTC，ISO 8601）；即该摘要下**业务发生时间**的最大值。</param>
/// <param name="UsageCount">用了多少次（**笔数**，不是明细行数）。</param>
/// <remarks>
/// 与 <see cref="EntryDto"/> 同为「枚举/时间以对外约定呈现」的那一层：库里的时间读回来
/// <c>DateTimeKind</c> 是 <c>Unspecified</c>，这里显式标记为 UTC，序列化才会带 <c>Z</c> 后缀
/// ——与 <see cref="EntryDto.From"/> 同一处理，前端不必猜这是哪个时区。
/// <para>
/// <see cref="Summary"/> 与 <see cref="UsageCount"/> 都是**如实给出**：前者不做形状归一，
/// 后者数的是笔数。候选的取舍（为什么不去重写法、为什么不用明细行数）见
/// <c>IEntryQueryService.ListSummariesAsync</c> 的说明。
/// </para>
/// </remarks>
public sealed record EntrySummaryOptionDto(string Summary, DateTime LastUsedAt, int UsageCount)
{
    /// <summary>由服务层的摘要候选构造 DTO。</summary>
    /// <param name="option">服务层给出的候选。</param>
    /// <returns>精简后的候选 DTO。</returns>
    public static EntrySummaryOptionDto From(EntrySummaryOption option) => new(
        option.Summary,
        DateTime.SpecifyKind(option.LastUsedAt, DateTimeKind.Utc),
        option.UsageCount);
}

/// <summary>收入与支出的分类合计（对外暴露）。</summary>
/// <param name="CurrencyCode">
/// 金额所属的币种代码；**一个启用币种都没有时为 <c>null</c>**（此时两个列表必为空）。
/// </param>
/// <param name="Month">实际查询的月份（<c>YYYY-MM</c>）；请求省略 <c>month</c> 时即服务器本地的当月。</param>
/// <param name="Income">收入侧的分类合计，按金额降序；没有收入发生时为空数组。</param>
/// <param name="Expense">支出侧的分类合计，按金额降序；没有支出发生时为空数组。</param>
/// <remarks>
/// 回带 <paramref name="CurrencyCode"/> 与 <paramref name="Month"/> 的理由同
/// <see cref="IncomeExpenseDailyResponse"/>：两者都可能由服务端决定，前端不自己再猜一次。
/// </remarks>
public sealed record EntryCategoryTotalsResponse(
    string? CurrencyCode,
    string Month,
    EntryCategoryTotalDto[] Income,
    EntryCategoryTotalDto[] Expense)
{
    /// <summary>由服务层结果构造响应体。</summary>
    /// <param name="currencyCode">实际统计的币种代码；无可用币种时为 <c>null</c>。</param>
    /// <param name="month">实际查询的月份（<c>YYYY-MM</c>）。</param>
    /// <param name="totals">服务层的分类合计。</param>
    /// <returns>对外响应体。</returns>
    public static EntryCategoryTotalsResponse From(
        string? currencyCode,
        string month,
        EntryCategoryTotals totals) => new(
        currencyCode,
        month,
        [.. totals.Income.Select(EntryCategoryTotalDto.From)],
        [.. totals.Expense.Select(EntryCategoryTotalDto.From)]);
}

/// <summary>某个分类在本月的收入或支出合计（对外暴露）。</summary>
/// <param name="CategoryId">分类主键；<c>null</c> 表示**未分类**（合法状态）。</param>
/// <param name="CategoryName">
/// 分类名称；未分类时为 <c>null</c>，与 <paramref name="CategoryId"/> 同生同灭。
/// **已停用分类的名称照常给出**。
/// </param>
/// <param name="Amount">本月发生额，**恒为正**；方向由所在列表（收入 / 支出）表达。</param>
public sealed record EntryCategoryTotalDto(int? CategoryId, string? CategoryName, decimal Amount)
{
    /// <summary>由服务层的分类合计构造 DTO。</summary>
    /// <param name="item">服务层的分类合计。</param>
    /// <returns>对外 DTO。</returns>
    public static EntryCategoryTotalDto From(EntryCategoryTotal item) =>
        new(item.CategoryId, item.CategoryName, item.Amount);
}

/// <summary>某账套某月按天的收支合计（对外暴露）。</summary>
/// <param name="CurrencyCode">
/// 金额所属的币种代码；**一个启用币种都没有时为 <c>null</c>**（此时 <paramref name="Days"/> 必为空）。
/// </param>
/// <param name="Month">实际查询的月份（<c>YYYY-MM</c>）；请求省略 <c>month</c> 时即服务器本地的当月。</param>
/// <param name="Days">该月的按天收支，按日期升序；**只含至少有一笔收支的日期**。</param>
/// <remarks>
/// 回带 <paramref name="CurrencyCode"/> 与 <paramref name="Month"/> 的理由同
/// <see cref="EntryCategoryTotalsResponse"/>：两者都可能由**服务端**决定，调用方不把它们带回来
/// 就只能自己再猜一次。
/// </remarks>
public sealed record EntryDailyFlowsResponse(
    string? CurrencyCode,
    string Month,
    EntryDailyFlowDto[] Days)
{
    /// <summary>由服务层结果构造响应体。</summary>
    /// <param name="currencyCode">实际统计的币种代码；无可用币种时为 <c>null</c>。</param>
    /// <param name="month">实际查询的月份（<c>YYYY-MM</c>）。</param>
    /// <param name="flows">服务层的按日收支。</param>
    /// <returns>对外响应体。</returns>
    public static EntryDailyFlowsResponse From(
        string? currencyCode,
        string month,
        EntryDailyFlows flows) => new(
        currencyCode,
        month,
        [.. flows.Days.Select(EntryDailyFlowDto.From)]);
}

/// <summary>按天收支的一个数据点（对外暴露）。</summary>
/// <param name="Date">日期（<c>yyyy-MM-dd</c>，**本地日期**）。</param>
/// <param name="IncomeTotal">当日收入合计，**恒为非负**。</param>
/// <param name="ExpenseTotal">当日支出合计，**恒为非负**。</param>
/// <param name="NetTotal">净额 = <paramref name="IncomeTotal"/> − <paramref name="ExpenseTotal"/>（**带符号**）。</param>
/// <remarks>
/// **两个发生额都是正数，符号只体现在 <paramref name="NetTotal"/> 上**：图上把支出线画在下方是
/// **呈现**的决定（前端取负即可），而接口上「支出 500」就是 500（同 <c>IncomeExpenseDailyPoint</c>
/// 的取舍）。
/// </remarks>
public sealed record EntryDailyFlowDto(
    string Date,
    decimal IncomeTotal,
    decimal ExpenseTotal,
    decimal NetTotal)
{
    /// <summary>由服务层的按日收支构造 DTO。</summary>
    /// <param name="flow">服务层的按日收支。</param>
    /// <returns>对外 DTO。</returns>
    public static EntryDailyFlowDto From(EntryDailyFlow flow) => new(
        flow.Date,
        flow.IncomeTotal,
        flow.ExpenseTotal,
        flow.NetTotal);
}

/// <summary>日报表的三份聚合（对外暴露）。</summary>
/// <param name="CurrencyCode">
/// 金额所属的币种代码；**一个启用币种都没有时为 <c>null</c>**（此时三份集合必为空）。
/// </param>
/// <param name="From">实际查询的起始日期（<c>yyyy-MM-dd</c>，服务器本地日期，**含**）。</param>
/// <param name="To">实际查询的结束日期（<c>yyyy-MM-dd</c>，服务器本地日期，**含**）。</param>
/// <param name="Days">按日期升序的按日收支；**只含至少有一笔收支的日期**。</param>
/// <param name="Categories">按分类的收入与支出合计。</param>
/// <param name="Tags">按标签的收入与支出合计。</param>
/// <remarks>
/// 回带 <paramref name="CurrencyCode"/>、<paramref name="From"/> 与 <paramref name="To"/> 的理由同
/// <see cref="EntryDailyFlowsResponse"/>：它们都可能由**服务端**决定（省略参数时取当月 1 日 ~ 今天），
/// 调用方不把它们带回来就只能自己再猜一次。
/// </remarks>
public sealed record EntryDailyReportResponse(
    string? CurrencyCode,
    string From,
    string To,
    EntryDailyFlowDto[] Days,
    EntryCategoryTotalsDto Categories,
    EntryTagTotalsDto Tags)
{
    /// <summary>由服务层结果构造响应体。</summary>
    /// <param name="currencyCode">实际统计的币种代码；无可用币种时为 <c>null</c>。</param>
    /// <param name="from">实际查询的起始日期（<c>yyyy-MM-dd</c>）。</param>
    /// <param name="to">实际查询的结束日期（<c>yyyy-MM-dd</c>）。</param>
    /// <param name="report">服务层的三份聚合。</param>
    /// <returns>对外响应体。</returns>
    /// <remarks>
    /// **方法名是 <c>Create</c> 而不是其余 DTO 的 <c>From</c>**：本记录已有名为
    /// <paramref name="from"/> 的属性，同名静态方法会与它冲突（CS0102）。
    /// 这是命名让位，不是口径差异。
    /// </remarks>
    public static EntryDailyReportResponse Create(
        string? currencyCode,
        string from,
        string to,
        EntryDailyReport report) => new(
        currencyCode,
        from,
        to,
        [.. report.Days.Select(EntryDailyFlowDto.From)],
        new EntryCategoryTotalsDto(
            [.. report.Categories.Income.Select(EntryCategoryTotalDto.From)],
            [.. report.Categories.Expense.Select(EntryCategoryTotalDto.From)]),
        new EntryTagTotalsDto(
            [.. report.Tags.Income.Select(EntryTagTotalDto.From)],
            [.. report.Tags.Expense.Select(EntryTagTotalDto.From)]));
}

/// <summary>月报表聚合的响应体。</summary>
/// <param name="CurrencyCode">实际统计的币种代码；无可用币种时为 <c>null</c>。</param>
/// <param name="From">实际查询的起始月份（<c>yyyy-MM</c>，服务器本地月份，**含**）。</param>
/// <param name="To">实际查询的结束月份（<c>yyyy-MM</c>，服务器本地月份，**含**）。</param>
/// <param name="Months">按月份升序的按月收支；**只含至少有一笔收支的月份**。</param>
/// <param name="Categories">按分类的收入与支出合计。</param>
/// <param name="Tags">按标签的收入与支出合计。</param>
/// <remarks>
/// 与 <see cref="EntryDailyReportResponse"/> 同构，只是 <c>days</c> 换成 <c>months</c>：
/// 调用方的呈现层可以按同一套规则处理两种粒度。
/// </remarks>
public sealed record EntryMonthlyReportResponse(
    string? CurrencyCode,
    string From,
    string To,
    EntryMonthlyFlowDto[] Months,
    EntryCategoryTotalsDto Categories,
    EntryTagTotalsDto Tags)
{
    /// <summary>由服务层结果构造响应体。</summary>
    /// <param name="currencyCode">实际统计的币种代码；无可用币种时为 <c>null</c>。</param>
    /// <param name="from">实际查询的起始月份（<c>yyyy-MM</c>）。</param>
    /// <param name="to">实际查询的结束月份（<c>yyyy-MM</c>）。</param>
    /// <param name="report">服务层的三份聚合。</param>
    /// <returns>对外响应体。</returns>
    /// <remarks>
    /// **方法名同样是 <c>Create</c>**：本记录已有名为 <paramref name="from"/> 的属性，
    /// 同名静态方法会与它冲突（CS0102）——与 <see cref="EntryDailyReportResponse"/> 同一命名让位。
    /// </remarks>
    public static EntryMonthlyReportResponse Create(
        string? currencyCode,
        string from,
        string to,
        EntryMonthlyReport report) => new(
        currencyCode,
        from,
        to,
        [.. report.Months.Select(EntryMonthlyFlowDto.From)],
        new EntryCategoryTotalsDto(
            [.. report.Categories.Income.Select(EntryCategoryTotalDto.From)],
            [.. report.Categories.Expense.Select(EntryCategoryTotalDto.From)]),
        new EntryTagTotalsDto(
            [.. report.Tags.Income.Select(EntryTagTotalDto.From)],
            [.. report.Tags.Expense.Select(EntryTagTotalDto.From)]));
}

/// <summary>某个月份的收入、支出与净额（对外暴露）。</summary>
/// <param name="Month">月份（<c>yyyy-MM</c>，**服务器本地月份**）。</param>
/// <param name="IncomeTotal">当月收入合计，**恒为非负**。</param>
/// <param name="ExpenseTotal">当月支出合计，**恒为非负**。</param>
/// <param name="NetTotal">净额 = <paramref name="IncomeTotal"/> − <paramref name="ExpenseTotal"/>（**带符号**）。</param>
public sealed record EntryMonthlyFlowDto(
    string Month,
    decimal IncomeTotal,
    decimal ExpenseTotal,
    decimal NetTotal)
{
    /// <summary>由服务层的按月收支构造 DTO。</summary>
    /// <param name="item">服务层的按月收支。</param>
    /// <returns>对外 DTO。</returns>
    public static EntryMonthlyFlowDto From(EntryMonthlyFlow item) =>
        new(item.Month, item.IncomeTotal, item.ExpenseTotal, item.NetTotal);
}

/// <summary>收入与支出的分类合计（日报表响应内嵌）。</summary>
/// <param name="Income">收入侧的分类合计，按金额降序。</param>
/// <param name="Expense">支出侧的分类合计，按金额降序。</param>
public sealed record EntryCategoryTotalsDto(
    EntryCategoryTotalDto[] Income,
    EntryCategoryTotalDto[] Expense);

/// <summary>收入与支出的标签合计（日报表响应内嵌）。</summary>
/// <param name="Income">收入侧的标签合计，按金额降序。</param>
/// <param name="Expense">支出侧的标签合计，按金额降序。</param>
/// <remarks>
/// **各标签金额之和可能大于区间收支合计**：一笔交易挂多个标签时，其金额在每个标签下各计一次。
/// 调用方在呈现层须写明这一点，不能把它当成「一分不多、一分不少」的拆分。
/// </remarks>
public sealed record EntryTagTotalsDto(
    EntryTagTotalDto[] Income,
    EntryTagTotalDto[] Expense);

/// <summary>某个标签在区间内的收入或支出合计（对外暴露）。</summary>
/// <param name="TagId">标签主键；<c>null</c> 表示**未打标签**（合法状态）。</param>
/// <param name="TagName">
/// 标签名称；未打标签时为 <c>null</c>，与 <paramref name="TagId"/> 同生同灭。
/// **已停用标签的名称照常给出**。
/// </param>
/// <param name="Amount">
/// 区间发生额，**恒为正**；方向由所在列表（收入 / 支出）表达。
/// 一笔多标签交易会在每个标签下各计一次（见 <see cref="EntryTagTotalsDto"/>）。
/// </param>
public sealed record EntryTagTotalDto(int? TagId, string? TagName, decimal Amount)
{
    /// <summary>由服务层的标签合计构造 DTO。</summary>
    /// <param name="item">服务层的标签合计。</param>
    /// <returns>对外 DTO。</returns>
    public static EntryTagTotalDto From(EntryTagTotal item) =>
        new(item.TagId, item.TagName, item.Amount);
}

/// <summary>一页账目明细。</summary>
/// <param name="Items">本页明细。</param>
/// <param name="Total">满足筛选条件的明细总数（跨页）。</param>
/// <param name="Page">当前页码，从 1 开始。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record EntryQueryPageDto(EntryDto[] Items, int Total, int Page, int PageSize)
{
    /// <summary>由查询结果构造分页 DTO。</summary>
    /// <param name="page">一页明细。</param>
    /// <returns>分页 DTO。</returns>
    public static EntryQueryPageDto From(EntryQueryPage page) => new(
        [.. page.Items.Select(EntryDto.From)],
        page.Total,
        page.Page,
        page.PageSize);
}
