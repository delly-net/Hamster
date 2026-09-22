using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 交易业务服务：期初余额入账、账户余额的汇总派生，以及升级既有数据库时的期初分录回填。
/// </summary>
/// <remarks>
/// **「明细方向 → 账户余额」的换算收敛在本服务内**（<see cref="SumSignedAmountsAsync"/>），
/// 全站只有这一处：<see cref="EntryDirection.Debit"/> 取正、<see cref="EntryDirection.Credit"/> 取负。
/// 调用方（账户端点、账户服务）一律只消费折算后的有符号金额，不自行判断方向。
/// </remarks>
public interface ITransactionService
{
    /// <summary>
    /// 为账户写入期初余额交易——按「目标账户 +期初金额，账本账户 −期初金额」记两条明细。
    /// </summary>
    /// <param name="account">
    /// 目标账户，需已取得主键（即已落库）。期初金额取其 <see cref="Account.InitialBalance"/>。
    /// </param>
    /// <param name="createdByUserId">
    /// 记账人主键；升级回填出来的期初交易没有记账人可考，此时传 <c>null</c>。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 实际写入了一笔期初交易返回 <c>true</c>；因**期初金额为 0** 或**该账户已有期初分录**
    /// 而未写入时返回 <c>false</c>。
    /// </returns>
    /// <remarks>
    /// 幂等：同一账户重复调用不会写出第二笔期初分录（账户创建与升级回填共用本方法）。
    /// 账本账户不存在时按需自动创建（每账套至多一个，见 <see cref="Account.IsSystem"/>）。
    /// </remarks>
    Task<bool> RecordOpeningBalanceAsync(
        Account account,
        int? createdByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量取账户余额的**有符号汇总**（借方为正、贷方为负之和），用于派生出账户余额。
    /// </summary>
    /// <param name="accountSetId">账套主键；只汇总该账套内的交易。</param>
    /// <param name="accountIds">目标账户主键集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 账户主键到有符号余额的映射。**无任何明细的账户不会出现在结果中**（调用方按 0 处理），
    /// 这与「期初金额为 0 的账户不写期初分录」相呼应：无分录即余额为 0。
    /// </returns>
    /// <remarks>
    /// 用一次分组查询取回全部账户的汇总，而非逐账户查询——后者是典型的 N+1。
    /// </remarks>
    Task<IReadOnlyDictionary<int, decimal>> SumSignedAmountsAsync(
        int accountSetId,
        IReadOnlyCollection<int> accountIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 为缺少期初分录的既有账户补写期初余额分录（升级既有数据库时用）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次实际补写的期初交易笔数。</returns>
    /// <remarks>
    /// 幂等：以「该账户是否已有期初明细」为判据，只补缺失的，重复执行不会产生第二笔分录。
    /// </remarks>
    Task<int> BackfillOpeningBalancesAsync(CancellationToken cancellationToken = default);
}
