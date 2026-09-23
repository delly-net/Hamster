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
    /// 记一笔收入或支出——按「目标账户 ± 金额，系统账本账户 ∓ 金额」记两条明细。
    /// </summary>
    /// <param name="account">
    /// 目标账户，需已取得主键（即已落库），且必须是当前用户**可见**的账户。
    /// 收入使其余额增加、支出使其减少。
    /// </param>
    /// <param name="type">
    /// 交易类型，仅 <see cref="TransactionType.Income"/> 与 <see cref="TransactionType.Expense"/> 有效
    /// （见 <see cref="TransactionTypeExtensions.IsUserRecordable"/>）。
    /// </param>
    /// <param name="amount">金额，单位「元」，**恒为正**（方向由 <paramref name="type"/> 表达，不靠金额符号）。</param>
    /// <param name="occurredAt">业务发生时间（UTC）。与落库时间刻意分开：可补记往日的收支。</param>
    /// <param name="summary">交易摘要（调用方需保证已 Trim 且非空）。</param>
    /// <param name="remark">备注；无备注时传 <c>null</c>。</param>
    /// <param name="createdByUserId">记账人主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入后的交易（已取得主键）。</returns>
    /// <remarks>
    /// 复式配平（借方合计 == 贷方合计）由两条**等额反向**的明细天然满足，
    /// 无需额外的配平校验；「方向 → 账户余额」的换算仍只有
    /// <see cref="SumSignedAmountsAsync"/> 一处，故本方法落地后账户余额自动生效。
    /// <para>
    /// **前置条件**（本方法不重复校验，与 <see cref="IAccountService"/> 的取舍一致）：
    /// <paramref name="type"/> 须满足 <see cref="TransactionTypeExtensions.IsUserRecordable"/>、
    /// <paramref name="amount"/> 须大于 0，且 <paramref name="account"/> 须是
    /// <c>IAccountService.FindVisibleAsync</c> 取得的实体。这些由端点层拦下并给出 400/404。
    /// </para>
    /// <para>
    /// **本服务刻意不注入 <see cref="IAccountService"/>**：后者已注入本服务
    /// （账户创建时要写期初分录），反向注入会构成循环依赖。可见性判定因此留在端点层，
    /// 服务层只负责写入——与 <c>EntryQueryService</c> 可以放心依赖
    /// <see cref="IAccountService"/> 的方向刚好相反，勿将判定挪进来。
    /// </para>
    /// <para>
    /// 对手方为该账套的系统账本账户（见 <see cref="Account.IsSystem"/>），不存在时按需自动创建，
    /// 与期初余额同一口径。
    /// </para>
    /// </remarks>
    Task<Transaction> RecordIncomeExpenseAsync(
        Account account,
        TransactionType type,
        decimal amount,
        DateTime occurredAt,
        string summary,
        string? remark,
        int createdByUserId,
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
