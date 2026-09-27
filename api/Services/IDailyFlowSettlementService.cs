namespace Hamster.Api.Services;

/// <summary>
/// 按天流量结算服务：把账套内**每一天**的收入或支出**发生额**重算出来并落表，供首页的当月收支图读取。
/// </summary>
/// <remarks>
/// 「流量」指「这一天发生了多少」，与 <see cref="ITotalAssetSettlementService"/> 的「存量」
/// （这一天收盘时有多少钱）相对：两者共用同一套「日期来源 + 水位推进 + 覆盖写」的骨架，
/// 差别只在被汇总的东西——那边按账户余额、这边按当日发生额。
/// <para>
/// **收入与支出各是一个服务实例、各有一份自己的水位**：水位按「订阅 + 账套」记
/// （见 <c>SettlementSubscriptionExecution</c>），两个订阅互不阻塞，一个口径出问题时可单独重跑。
/// 两者的算法逐字相同，故实现落在 <c>DailyFlowSettlementServiceBase</c> 里、由两个薄子类实例化。
/// </para>
/// </remarks>
public interface IDailyFlowSettlementService
{
    /// <summary>
    /// 按结算任务里的日期，从最早到最晚逐日重算并覆盖落库。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次执行的结果摘要。</returns>
    /// <remarks>
    /// **幂等**：同一天重算的结果与第一次相同（整天先删后插，见基类的落库方法）。
    /// 故「结算任务重投」「人工补跑」都不会写出重复行。
    /// </remarks>
    Task<DailyFlowSettlementRunResult> RunAsync(
        int accountSetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读某账套某月、**某个用户**的按天收支。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="userId">查看者主键（记录按用户分行，见 <c>IncomeSettlementRecord.UserId</c>）。</param>
    /// <param name="currencyCode">币种代码。</param>
    /// <param name="monthStart">该月 1 日（本地日期）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按日期升序的按天数据点；没有记录时是空列表。</returns>
    Task<IReadOnlyList<DailyFlowPoint>> ListDailyAsync(
        int accountSetId,
        int userId,
        string currencyCode,
        DateTime monthStart,
        CancellationToken cancellationToken = default);
}

/// <summary>一次按天流量结算的执行结果。</summary>
/// <param name="AccountSetId">账套主键。</param>
/// <param name="DayCount">本次重算的天数。</param>
/// <param name="RecordCount">本次写入的记录行数。</param>
/// <param name="LastExecutedDate">水位推进到的日期；未推进时为 <c>null</c>。</param>
public sealed record DailyFlowSettlementRunResult(
    int AccountSetId,
    int DayCount,
    int RecordCount,
    DateTime? LastExecutedDate);

/// <summary>按天流量的一个数据点（某个用户、某一天、某个币种）。</summary>
/// <param name="TransactionDate">日期（**本地日期**，时刻部分恒为 00:00:00）。</param>
/// <param name="PersonalTotal">个人账户在当日的合计（**恒为非负**）。</param>
/// <param name="PublicTotal">公共账户在当日的合计（**恒为非负**）。</param>
/// <remarks>
/// **不带币种**：调用方按单一币种查询（见 <see cref="IDailyFlowSettlementService.ListDailyAsync"/>），
/// 币种在请求的粒度上就固定了，逐个数据点再带一遍是冗余（同 <c>TotalAssetDailyPoint</c> 不带用户的取舍）。
/// </remarks>
public sealed record DailyFlowPoint(
    DateTime TransactionDate,
    decimal PersonalTotal,
    decimal PublicTotal);

/// <summary>一天里某一个用户的收支汇总（等待落库的中间形态）。</summary>
/// <param name="UserId">归属用户主键。</param>
/// <param name="CurrencyCode">币种代码。</param>
/// <param name="PersonalTotal">个人账户合计。</param>
/// <param name="PublicTotal">公共账户合计。</param>
/// <remarks>
/// 刻意**不做成实体**：实体类型是收入表与支出表各自的（两张表列名不同），
/// 基类不该知道它们；基类只算出这个中立形态，由子类映射成自己的实体再去落库。
/// </remarks>
public sealed record DailyFlowRow(
    int UserId,
    string CurrencyCode,
    decimal PersonalTotal,
    decimal PublicTotal);
