using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 总资产结算的一次执行结果（供日志与订阅回执使用）。
/// </summary>
/// <param name="AccountSetId">本次处理的账套主键。</param>
/// <param name="DayCount">本次实际重算并落库的天数；0 表示没有待处理的日期（重复投递即如此）。</param>
/// <param name="RecordCount">本次写入的记录行数（天数 × 成员数 × 币种数）。</param>
/// <param name="LastExecutedDate">本次执行后水位所在的那一天；本次未处理任何日期时为 <c>null</c>。</param>
public sealed record TotalAssetSettlementRunResult(
    int AccountSetId,
    int DayCount,
    int RecordCount,
    DateTime? LastExecutedDate);

/// <summary>
/// 总资产结算：按「结算任务」里的日期逐日重算账套的总资产与总负债并落表。
/// </summary>
/// <remarks>
/// 本服务是「总资产结算订阅」的**业务实现**，订阅的接线在
/// <c>TotalAssetSettlementSubscription</c>（它只做一件事：收到结算事件就调 <see cref="RunAsync"/>）。
/// 分开的理由与事件总线本身一致：订阅者要做到「新增一个类文件即生效」，
/// 而业务逻辑要能脱离事件机制被直接调用与验证。
/// <para>
/// **本服务自己不认识任何事件**：它接受的是「把某本账套补齐到今天」，由调用方决定何时触发。
/// </para>
/// </remarks>
public interface ITotalAssetSettlementService
{
    /// <summary>
    /// 本订阅的执行水位代码（<c>hamster_settlement_subscription_execution.subscription_code</c>）。
    /// </summary>
    /// <remarks>
    /// 常量放在接口上而不是订阅者类里：读写的两边（本服务与
    /// <see cref="ISettlementSubscriptionExecutionService"/>）都按它认人，
    /// 而订阅者只是「谁在什么时候调」的那一层，把约定挂在它身上会让协议定义散落在调用方。
    /// <para>
    /// 改这个值等同于**换一个订阅**：既有水位全部作废、下次执行会从头重算（结果仍幂等，
    /// 只是白做功）。真要改，先想清楚这是不是本意。
    /// </para>
    /// </remarks>
    public const string SUBSCRIPTION_CODE = "TotalAssetSettlement";

    /// <summary>
    /// 把某本账套的总资产结算补齐：重算全部未处理过的日期并落表，然后推进水位。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次执行的天数、写入的记录行数与执行后的水位。</returns>
    /// <remarks>
    /// **幂等**：同一天重复执行只会把该日的记录**覆盖**一遍（先删该日全部行再插入），
    /// 且已处理过的日期根本不会被再次选中——水位是幂等的第一道闸，覆盖写是第二道。
    /// <para>
    /// **待处理区间**是「结算任务里比水位更晚的日期」，按日期**升序**逐日处理，
    /// 一日的顺序就是「从最早日期到最晚日期」（任务描述）。
    /// 于是同一本账套的历史无论积压多久，一次执行都会补齐到最新，且下次执行只做增量。
    /// </para>
    /// </remarks>
    Task<TotalAssetSettlementRunResult> RunAsync(
        int accountSetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 读某用户在某账套、某币种下某个自然月的按天记录，供首页走势图使用。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="userId">目标用户主键（记录按用户分行，见 <see cref="TotalAssetSettlementRecord.UserId"/>）。</param>
    /// <param name="currencyCode">目标币种代码（记录按币种分行，见 <see cref="TotalAssetSettlementRecord.CurrencyCode"/>）。</param>
    /// <param name="monthStart">目标月份的**本地日期**（通常为该月 1 日；时刻部分被忽略）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该月内的记录，按日期升序；**没有记录时是空列表**（尚未结算或当月还没有账）。</returns>
    /// <remarks>
    /// 区间是半开区间 <c>[本月 1 日, 次月 1 日)</c>，与结算窗口的上界口径一致。
    /// <para>
    /// **只返回存在的日子**，不在缺日处补零：记录就是「这一天结算过」的事实，
    /// 补零会把「这一天还没结算」画成「这一天资产为 0」——图上是一根掉到底的线，
    /// 那是错误信息而不是缺失信息。
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<TotalAssetSettlementRecord>> ListDailyAsync(
        int accountSetId,
        int userId,
        string currencyCode,
        DateTime monthStart,
        CancellationToken cancellationToken = default);
}
