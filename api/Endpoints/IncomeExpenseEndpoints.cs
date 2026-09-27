using System.Globalization;
using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 收支端点（任意已登录用户）：读当前用户在当前账套里某个自然月的按天收入与支出。
/// </summary>
/// <remarks>
/// 供首页的当月收支图使用，**只读**：数据由收入结算订阅与支出结算订阅在结算事件后逐日重算并落表
/// （见 <see cref="IIncomeSettlementService"/> / <see cref="IExpenseSettlementService"/>），
/// 本分组不提供任何写入或触发入口。
/// <para>
/// **把两张表合成一个响应**：两个订阅各写各的表，而图上那两条线必须落在**同一批日期**上
/// （理由见 <see cref="ApiPathConst.INCOME_EXPENSE_GROUP"/>）。合并规则：**日期的并集**，
/// 某一天只有一侧有记录时另一侧按 0 计（见 <see cref="Merge"/>）。
/// </para>
/// <para>
/// **结果因人而异**：两张快照都按用户分行，同一天的「我的收入」只含**我的个人账户**加公共账户，
/// 故本分组既读账套（请求头）也取当前用户主键（同 <see cref="TotalAssetEndpoints"/>）。
/// </para>
/// <para>
/// **不跨币种求和**：两张表都按币种分行，本端点取**系统默认币种**那一组
/// （没有汇率来源，把不同币种的金额相加是一个凭空捏造的数字）。
/// </para>
/// </remarks>
public sealed class IncomeExpenseEndpoints : IEndpoint
{
    /// <summary><c>month</c> 查询参数的格式：<c>YYYY-MM</c>。</summary>
    private const string MONTH_FORMAT = "yyyy-MM";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.INCOME_EXPENSE_GROUP)
            .WithTags("收支")
            .RequireAuthorization();

        group.MapGet("/daily", async (
                string? month,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICurrencyService currencies,
                IIncomeSettlementService incomes,
                IExpenseSettlementService expenses,
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

                // 币种取不到（一个启用币种都没有）时返回空序列而不是 404：
                // 「没有币种可看」与「这个月还没有数据」在界面上是同一种呈现（暂无数据），
                // 而 404 会让前端把它当成一次错误、弹出报错提示。
                var currency = await currencies.GetDefaultAsync(cancellationToken);
                if (currency is null)
                {
                    return Results.Ok(new IncomeExpenseDailyResponse(
                        null,
                        monthStart.ToString(MONTH_FORMAT, CultureInfo.InvariantCulture),
                        []));
                }

                var incomePoints = await incomes.ListDailyAsync(
                    accountSet!.Id,
                    actor!.Id,
                    currency.Code,
                    monthStart,
                    cancellationToken);

                var expensePoints = await expenses.ListDailyAsync(
                    accountSet.Id,
                    actor.Id,
                    currency.Code,
                    monthStart,
                    cancellationToken);

                return Results.Ok(new IncomeExpenseDailyResponse(
                    currency.Code,
                    monthStart.ToString(MONTH_FORMAT, CultureInfo.InvariantCulture),
                    Merge(incomePoints, expensePoints)));
            })
            .WithName("GetIncomeExpenseDaily")
            .WithSummary("读取当月按天收入与支出")
            .WithDescription(
                "返回当前用户在当前账套、系统默认币种下某个自然月的按天收入、支出与净额。" +
                "**月份省略时取服务器本地的当月**——记录的日期是本地日期，用服务器本地月才不会与落库的日期错位。" +
                "**日期取两张记录表的并集**：某一天只有一侧有记录时，另一侧按 0 计（两个订阅各推各的水位，" +
                "短暂的不同步是可能的）；两侧都没有记录的日子**不出现**——补零会把「这天还没结算」" +
                "画成「这天收支为 0」。金额口径：收入与支出**都是正数**（方向由字段名表达），" +
                "净额 = 收入 − 支出。上界为半开区间（不含次月 1 日）。" +
                "未携带账套请求头返回 400，未登录返回 401，无权访问账套返回 403。");

        // 刻意**不提供**任何写入或「立即重算」端点：见类头注释。
    }

    /// <summary>
    /// 把两条按天序列按**日期的并集**合并成一组数据点。
    /// </summary>
    /// <param name="incomes">收入序列（按日期升序，来自收入记录表）。</param>
    /// <param name="expenses">支出序列（按日期升序，来自支出记录表）。</param>
    /// <returns>按日期升序的合并结果。</returns>
    /// <remarks>
    /// **合并而不是「只取两边都有的日期」**：两个订阅各推各的水位、各写各的表，
    /// 同一时刻「收入算到 5 号、支出算到 3 号」是正常状态（比如支出那一侧刚失败过一次，
    /// 而收入那侧成功了）。若取交集，4 号、5 号会从图上整个消失——用户看到的是「这两天没有收支」，
    /// 而事实是「这两天还没有算」；取并集则这两天以「收入有值、支出为 0」出现，
    /// 随着下一次结算执行自愈。这也与「缺日不补零」不冲突：**并集只包含至少一侧落过库的日期**，
    /// 两侧都没有记录的日子仍然不出现。
    /// <para>
    /// **同一日期只可能有一行**：两张表的唯一键都把 <c>transaction_date</c> 包含在内，
    /// 故按日期建字典不会有覆盖（真有重复键说明表结构被改了，那时这里静默取最后一条，
    /// 而上游的插入早就会因唯一键冲突而失败——不会走到这一步）。
    /// </para>
    /// </remarks>
    private static IReadOnlyList<IncomeExpenseDailyPoint> Merge(
        IReadOnlyList<DailyFlowPoint> incomes,
        IReadOnlyList<DailyFlowPoint> expenses)
    {
        var incomeByDate = new Dictionary<DateTime, decimal>(incomes.Count);
        foreach (var point in incomes)
        {
            incomeByDate[point.TransactionDate] = point.PersonalTotal + point.PublicTotal;
        }

        var expenseByDate = new Dictionary<DateTime, decimal>(expenses.Count);
        foreach (var point in expenses)
        {
            expenseByDate[point.TransactionDate] = point.PersonalTotal + point.PublicTotal;
        }

        // 日期的并集，按日期升序（两个来源各自已升序，但并集仍要显式排序：
        // 数据库的排序在 Sqlite 上是「按日期文本」，此处按 DateTime 真值排一遍更稳）
        var dates = new SortedSet<DateTime>();
        dates.UnionWith(incomeByDate.Keys);
        dates.UnionWith(expenseByDate.Keys);

        return
        [
            .. dates.Select(date =>
            {
                var income = incomeByDate.GetValueOrDefault(date);
                var expense = expenseByDate.GetValueOrDefault(date);

                return new IncomeExpenseDailyPoint(
                    date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    income,
                    expense,
                    income - expense);
            }),
        ];
    }

    /// <summary>
    /// 解析 <c>month</c> 查询参数；省略时取服务器本地的当月。
    /// </summary>
    /// <param name="month">查询参数原值（<c>YYYY-MM</c>，可为 <c>null</c>）。</param>
    /// <param name="monthStart">解析结果：该月 1 日的本地日期（时刻部分为 00:00:00）。</param>
    /// <returns>解析（或回落）成功返回 <c>true</c>；格式不合法返回 <c>false</c>。</returns>
    /// <remarks>
    /// 与 <see cref="TotalAssetEndpoints"/> 的同名方法逐字相同（含「用 <c>TryParseExact</c> 而不是
    /// <c>TryParse</c>」与「回落用 <see cref="DateTime.Now"/>」两条取舍）。
    /// 刻意不抽公共方法：两处各自只有十几行，而抽出来就要为「首页口径」造一个共享类型，
    /// 代价大于收益（同 <c>ui/README.md</c> 里三个选择框不抽公共组件的取舍）。
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
    /// <remarks>与 <see cref="TotalAssetEndpoints"/> 的同名方法同构（用户身份以数据库为准；无账套请求头是 400）。</remarks>
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
}

/// <summary>某账套某月按天收支的响应体。</summary>
/// <param name="CurrencyCode">
/// 金额所属的币种代码；**一个启用币种都没有时为 <c>null</c>**（此时 <paramref name="Days"/> 必为空）。
/// </param>
/// <param name="Month">实际查询的月份（<c>YYYY-MM</c>）。请求省略 <c>month</c> 时即服务器本地的当月。</param>
/// <param name="Days">该月的按天记录，按日期升序；**没有任何记录时是空数组**。</param>
/// <remarks>
/// 回带 <paramref name="Month"/> 与 <paramref name="CurrencyCode"/> 的理由同 <c>TotalAssetDailyResponse</c>：
/// 两者都可能由**服务端**决定，调用方不把它们带回来就只能自己再猜一次。
/// </remarks>
public sealed record IncomeExpenseDailyResponse(
    string? CurrencyCode,
    string Month,
    IReadOnlyList<IncomeExpenseDailyPoint> Days);

/// <summary>按天收支的一个数据点。</summary>
/// <param name="Date">日期（<c>yyyy-MM-dd</c>，**本地日期**）。</param>
/// <param name="IncomeTotal">收入合计 = 个人的收入 + 公共的收入（**恒为非负**）。</param>
/// <param name="ExpenseTotal">支出合计 = 个人的支出 + 公共的支出（**恒为非负**）。</param>
/// <param name="NetTotal">净额 = <paramref name="IncomeTotal"/> − <paramref name="ExpenseTotal"/>（**带符号**）。</param>
/// <remarks>
/// **两个字段都是正数，符号只体现在 <paramref name="NetTotal"/> 上**：图上把支出线画在下方是**呈现**
/// 的决定（前端取负即可），而库里与接口上「支出 500」就是 500。
/// 若在接口层就把它变成 −500，读接口的人要回答「这个月一共花了多少」时就得先做一次取反，
/// 而取反这一步很容易被漏掉，漏掉的表现是「花得越多、图上的支出线越高」。
/// <para>
/// **不落下「个人 / 公共」的拆分**：走势图只画两条线，拆分是账户页的事（同 <c>TotalAssetDailyPoint</c>）。
/// </para>
/// </remarks>
public sealed record IncomeExpenseDailyPoint(
    string Date,
    decimal IncomeTotal,
    decimal ExpenseTotal,
    decimal NetTotal);
