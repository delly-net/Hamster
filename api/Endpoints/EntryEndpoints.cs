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

    /// <summary>时间参数格式错误时的字段错误。</summary>
    private static readonly string[] TIME_ERROR =
        ["时间格式不正确，应为 ISO 8601 时间（如 2026-09-01T00:00:00Z）"];

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
                int? page,
                int? pageSize,
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
                    currentPage,
                    currentPageSize,
                    cancellationToken);

                return Results.Ok(EntryQueryPageDto.From(result));
            })
            .WithName("QueryEntries")
            .WithSummary("账目明细查询")
            .WithDescription(
                "在当前账套内按时间区间与账户查询交易明细，按**业务发生时间**升序分页返回" +
                "（同一时刻按交易主键、再按明细主键升序，保证翻页结果稳定）。" +
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
                "**账户筛选项请直接用 `GET /api/accounts?includeInactive=true`**：" +
                "明细页的筛选需要含已停用账户（软删除的账户上仍有历史明细），该参数已经提供该能力，" +
                "不另设一套平行接口。" +
                "但该端点**不按类型过滤**——账户管理页需要完整列表，不为此加 `type` 参数；" +
                "因此**为「账目明细」构造账户筛选项的调用方须自行排除 `Contact`**：" +
                "明细行本就不含往来账户，候选若含就成了「选了也查不到」的空档。" +
                "这是**呈现层分组**（同「本接口不呈现往来账户明细」这条数据面规则的配套），" +
                "与「可见性由后端判定」不是一回事。");
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
/// <remarks>
/// 枚举一律**以字符串**对外，前端据此映射中文标签，前后端不共同维护数值对照表。
/// 对手方分档而非「给名称或给 null」：<c>Ledger</c> 与 <c>Hidden</c> 都不给主键与名称，
/// 「不可见」这件事本身不携带任何可辨识信息。
/// <para>
/// **分类不分档**：它没有可见性维度（账套内所有成员共用同一份字典），
/// 故直接给出主键与名称，不像对手方那样需要 <c>Ledger</c> / <c>Hidden</c> 这类遮罩档位。
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
    bool IsPrimary)
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
        row.IsPrimary);
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
