using System.Globalization;
using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 总资产端点（任意已登录用户）：读当前用户在当前账套里某个自然月的按天总资产。
/// </summary>
/// <remarks>
/// 供首页走势图使用，**只读**：数据由总资产结算订阅在结算事件后逐日重算并落表
/// （见 <see cref="ITotalAssetSettlementService"/>），本分组不提供任何写入或触发入口。
/// <para>
/// **结果因人而异**：快照按用户分行，同一天的「我的总资产」只含**我的个人账户**加公共账户
/// （见 <see cref="TotalAssetSettlementRecord.UserId"/>），故本分组既读账套（请求头）也取当前用户主键，
/// 与 <see cref="AccountSetPreferenceEndpoints"/> 同类、与账户/分类/标签那几个「账套内共用一份」的分组相反。
/// </para>
/// <para>
/// **不跨币种求和**：记录按币种分行，本端点取**系统默认币种**那一组
/// （没有汇率来源，把不同币种的金额相加是一个凭空捏造的数字，见该实体注释）。
/// </para>
/// </remarks>
public sealed class TotalAssetEndpoints : IEndpoint
{
    /// <summary><c>month</c> 查询参数的格式：<c>YYYY-MM</c>。</summary>
    private const string MONTH_FORMAT = "yyyy-MM";

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.TOTAL_ASSET_GROUP)
            .WithTags("总资产")
            .RequireAuthorization();

        group.MapGet("/daily", async (
                string? month,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICurrencyService currencies,
                ITotalAssetSettlementService totalAssets,
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
                    return Results.Ok(new TotalAssetDailyResponse(
                        null,
                        monthStart.ToString(MONTH_FORMAT, CultureInfo.InvariantCulture),
                        []));
                }

                var records = await totalAssets.ListDailyAsync(
                    accountSet!.Id,
                    actor!.Id,
                    currency.Code,
                    monthStart,
                    cancellationToken);

                return Results.Ok(new TotalAssetDailyResponse(
                    currency.Code,
                    monthStart.ToString(MONTH_FORMAT, CultureInfo.InvariantCulture),
                    [.. records.Select(TotalAssetDailyPoint.From)]));
            })
            .WithName("GetTotalAssetDaily")
            .WithSummary("读取当月按天总资产")
            .WithDescription(
                "返回当前用户在当前账套、系统默认币种下某个自然月的按天总资产与净资产。" +
                "**月份省略时取服务器本地的当月**——记录的日期是本地日期，用服务器本地月才不会与落库的日期错位。" +
                "**只返回有记录的日子**（记录由总资产结算订阅在结算事件后落库），" +
                "缺日不补零：补零会把「这一天还没结算」画成「这一天资产为 0」。上界为半开区间" +
                "（不含次月 1 日）。金额口径：资产合计 = 个人 + 公共的资金账户；负债合计 = 个人 + 公共的" +
                "负债账户（**带符号，欠款为负**）；净资产 = 资产合计 + 负债合计。" +
                "未携带账套请求头返回 400，未登录返回 401，无权访问账套返回 403。");

        // 刻意**不提供**任何写入或「立即重算」端点：见类头注释。
    }

    /// <summary>
    /// 解析 <c>month</c> 查询参数；省略时取服务器本地的当月。
    /// </summary>
    /// <param name="month">查询参数原值（<c>YYYY-MM</c>，可为 <c>null</c>）。</param>
    /// <param name="monthStart">解析结果：该月 1 日的本地日期（时刻部分为 00:00:00）。</param>
    /// <returns>解析（或回落）成功返回 <c>true</c>；格式不合法返回 <c>false</c>。</returns>
    /// <remarks>
    /// 回落用 <see cref="DateTime.Now"/> 而不是走 <c>LocalDay</c>：两者取的是同一个
    /// <see cref="TimeZoneInfo.Local"/>（见 <c>LocalDay.ResolveTimeZone</c>），
    /// 而这里只需要「当月」这一个粗粒度值，为它注入并缓存一个时区不值当。
    /// <para>
    /// **格式校验走 <c>TryParseExact</c> 而不是 <c>TryParse</c>**：后者会把
    /// <c>2026-9-1</c>、<c>2026/09/01</c> 之类一并收下，而本参数是「哪一年哪一月」，
    /// 收下一堆变体只会让调用方以为「传什么都能用」，回到格式约定上反而不清楚。
    /// </para>
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
    /// 与 <see cref="AccountSetPreferenceEndpoints"/> 的同名方法同构（用户身份以数据库为准，
    /// 不信任令牌中的声明；无账套请求头是 400 而不是 403）。
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
}

/// <summary>某账套某月按天总资产的响应体。</summary>
/// <param name="CurrencyCode">
/// 金额所属的币种代码；**一个启用币种都没有时为 <c>null</c>**（此时 <paramref name="Days"/> 必为空）。
/// </param>
/// <param name="Month">实际查询的月份（<c>YYYY-MM</c>）。请求省略 <c>month</c> 时即服务器本地的当月。</param>
/// <param name="Days">该月的按天记录，按日期升序；**没有记录时是空数组**。</param>
/// <remarks>
/// 回带 <paramref name="Month"/> 与 <paramref name="CurrencyCode"/> 是刻意的：两者都可能由**服务端**
/// 决定（当月、系统默认币种），调用方不把它们带回来就只能自己再猜一次，而猜错的表现是
/// 「图上画的是另一个币种的数、标题却写着这个币种」。前端据此呈现标题即可，不必自己算。
/// </remarks>
public sealed record TotalAssetDailyResponse(
    string? CurrencyCode,
    string Month,
    IReadOnlyList<TotalAssetDailyPoint> Days);

/// <summary>按天总资产的一个数据点。</summary>
/// <param name="Date">日期（<c>yyyy-MM-dd</c>，**本地日期**）。</param>
/// <param name="AssetTotal">资产合计 = 个人的资金账户 + 公共的资金账户。</param>
/// <param name="LiabilityTotal">负债合计 = 个人的负债账户 + 公共的负债账户，**带符号**（欠款为负）。</param>
/// <param name="NetTotal">净资产 = <paramref name="AssetTotal"/> + <paramref name="LiabilityTotal"/>。</param>
/// <remarks>
/// **负债是「加」上去的**：它的取值本身就是负数，故不必在这里再写一个减号——
/// 减号写两遍就会出现「负债为负时被加回去」这种符号错。
/// <para>
/// **不落下「个人 / 公共」的拆分**：走势图只画两条线，拆分是账户页的事；
/// 把它带出来只会让每个数据点多两个没人读的字段（同 <c>EntryFilterPreferenceDto</c> 不带时间戳的取舍）。
/// </para>
/// </remarks>
public sealed record TotalAssetDailyPoint(
    string Date,
    decimal AssetTotal,
    decimal LiabilityTotal,
    decimal NetTotal)
{
    /// <summary>由落库的记录构造数据点。</summary>
    /// <param name="record">按天记录。</param>
    /// <returns>数据点。</returns>
    public static TotalAssetDailyPoint From(TotalAssetSettlementRecord record)
    {
        var assets = record.PersonalAssetTotal + record.PublicAssetTotal;
        var liabilities = record.PersonalLiabilityTotal + record.PublicLiabilityTotal;

        return new TotalAssetDailyPoint(
            record.TransactionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            assets,
            liabilities,
            assets + liabilities);
    }
}
