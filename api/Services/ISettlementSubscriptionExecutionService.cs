using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 结算订阅执行水位的读写：某个订阅在某本账套上「处理到哪一天了」。
/// </summary>
/// <remarks>
/// 本服务只认 <c>(订阅代码, 账套)</c> 这个二元组，**不知道任何订阅的业务**——
/// 「处理一天」具体做什么由各订阅自己实现（如 <see cref="ITotalAssetSettlementService"/>）。
/// 水位读写之所以抽出来，是因为它是**每个订阅都要做且必须做对**的一件事：
/// 「读水位 → 算待处理区间 → 处理 → 推进水位」这条链上，最后一步写错的后果
/// （漏算一整天且再也不会补）远比业务本身写错更隐蔽。
/// <para>
/// **推进只增不减**：传入的日期不比已记日期更晚时，整行一个字节都不写、返回 <c>false</c>。
/// 「重复投递同一个事件」因此是**零写入**的——这正是幂等的判据，
/// 也是「同一份数据被算了两遍却看不出来」这类问题的反证（见
/// <see cref="SettlementSubscriptionExecution"/> 的注释）。
/// </para>
/// </remarks>
public interface ISettlementSubscriptionExecutionService
{
    /// <summary>
    /// 读某订阅在某账套上已处理完的最后一天。
    /// </summary>
    /// <param name="subscriptionCode">订阅代码（见 <see cref="SettlementSubscriptionExecution.SubscriptionCode"/>）。</param>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 最后执行日期；**从未执行过时返回 <c>null</c>**。
    /// <para>
    /// 返回 <c>null</c> 而不是某个哨兵日期：<c>null</c> 表示「无下界」（该订阅在这本账套上
    /// 还没干过活，全部历史日期都要处理），而任何哨兵日期都会把「无下界」表达成
    /// 「从某一天起」——账套的历史有多早，那个哨兵就得有多早，而它是猜的。
    /// </para>
    /// </returns>
    Task<DateTime?> FindLastExecutedDateAsync(
        string subscriptionCode,
        int accountSetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 把某订阅在某账套上的水位推进到指定日期。
    /// </summary>
    /// <param name="subscriptionCode">订阅代码。</param>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="lastExecutedDate">已处理完的最后一天（本地日期，时刻部分忽略）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>水位确实被推进（含首次建立）返回 <c>true</c>；已有不早于该日期水位、**本次未写入**返回 <c>false</c>。</returns>
    /// <remarks>
    /// **必须在业务结果落库之后调用**：水位是「已完成」而不是「已开始」（见
    /// <see cref="SettlementSubscriptionExecution.LastExecutedDate"/>）。
    /// <para>
    /// 调用方按日期**升序**逐个推进；反过来传（先推晚的再推早的）只会让早的那次变成空转，
    /// 因为本方法只增不减。
    /// </para>
    /// <para>
    /// **并发口径**：读-判-写之间不设事务。这是刻意的——本订阅只在结算执行任务里被调用，
    /// 而该任务由 <c>SettlementOptions.IsMasterNode</c> 保证只有一个节点在跑。
    /// 唯一索引 <c>(subscription_code, account_set_id)</c> 是最后一层保险：
    /// 真有两个写者同时建行，输的那个会因唯一索引冲突而抛异常，进而让这次事件派发计为失败、
    /// 结算任务不标记已执行并留待下次重试（见 <c>SettlementExecutionJob</c>），
    /// 而重试时它会读到已存在的水位、走本方法的空转分支。宁可重投也不丢事件。
    /// </para>
    /// </remarks>
    Task<bool> AdvanceAsync(
        string subscriptionCode,
        int accountSetId,
        DateTime lastExecutedDate,
        CancellationToken cancellationToken = default);
}
