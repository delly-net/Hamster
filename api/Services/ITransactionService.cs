using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 交易业务服务：期初余额入账、用户手工记账（收入/支出/转账）、账户余额的汇总派生，
/// 以及升级既有数据库时的期初分录回填。
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
    /// 对手方取该账户**所属币种**的账本账户，不存在时按需自动创建
    /// （每账套每币种至多一个，见 <see cref="Account.IsSystem"/> 与 <see cref="Account.CurrencyCode"/>）。
    /// </remarks>
    Task<bool> RecordOpeningBalanceAsync(
        Account account,
        int? createdByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 记一笔用户手工账（收入 / 支出 / 转账）——按「目标账户 ± 金额，对手方账户 ∓ 金额」记两条明细。
    /// </summary>
    /// <param name="account">
    /// 目标账户，需已取得主键（即已落库），且必须是当前用户**可见**的账户。
    /// 收入时它是收入账户（余额增加），支出与转账时它是支出账户 / 转出账户（余额减少）。
    /// </param>
    /// <param name="counterpartyAccount">
    /// 对手方账户，**可空**：
    /// <list type="bullet">
    ///   <item><c>null</c>：用该账户**所属币种**的系统账本账户配平，语义即「款项来自/去往账套之外」。
    ///   仅收入与支出可以如此（转账必须指定对手方，端点层已拦）。</item>
    ///   <item>非空：直接用传入的实体。此时这是一笔**两个真实账户之间的转账**，账本账户完全不参与。</item>
    /// </list>
    /// 非空时必须是当前用户可见的账户，且**币种须与 <paramref name="account"/> 相同**
    /// （见下方「不变量」）。
    /// </param>
    /// <param name="category">
    /// 分类，**可空**：<c>null</c> 即「未分类」，是正常状态（记账时分类是可选的）。
    /// 非空时必须是**同一账套**内的分类，否则抛 <see cref="ArgumentException"/>（见下方「不变量」）。
    /// <para>
    /// 分类由端点层解析后传入（可能是用户选中的、也可能是按名自动创建的），
    /// 本服务不查分类表——与 <paramref name="counterpartyAccount"/> 同一取舍。
    /// 分类挂在**交易**而非明细上，故一笔转账只带一个分类。
    /// </para>
    /// </param>
    /// <param name="type">
    /// 交易类型，取值须满足 <see cref="TransactionTypeExtensions.IsUserRecordable"/>
    /// （<see cref="TransactionType.Income"/> / <see cref="TransactionType.Expense"/> /
    /// <see cref="TransactionType.Transfer"/>）。
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
    /// **三种类型共用本方法**：收支与转账的落库动作完全相同（交易 + 两条等额反向明细），
    /// 差异只在「方向怎么定」与「对手方是谁」。转账与支出同向——转出账户记贷方，
    /// 与「钱离开的那个账户记贷方」这条规则一致，故无需为它单开分支。
    /// 转账的两个账户都必须是资金账户或负债账户，该判定在端点层
    /// （<c>AccountTypeExtensions.IsTransferAccount</c>），本方法不重复校验。
    /// </para>
    /// <para>
    /// **不变量：跨币种交易无法发生。** 对手方与目标账户的币种必须一致，否则抛 <see cref="ArgumentException"/>。
    /// 端点层已先判一次并给出 400，此处再判是因为本方法是**唯一写账入口**——
    /// 把它守在这里，「跨币种交易」在库里就不可能存在，与「配平由等额反向保证」同一性质。
    /// </para>
    /// <para>
    /// **不变量：分类不会跨账套。** <paramref name="category"/> 非空时其
    /// <see cref="Category.AccountSetId"/> 必须与 <paramref name="account"/> 的一致，
    /// 否则抛 <see cref="ArgumentException"/>。分类表没有可见性维度可依赖（与账户不同），
    /// 这道卡只能设在写入路径上。
    /// </para>
    /// <para>
    /// **其余前置条件**（本方法不重复校验，与 <see cref="IAccountService"/> 的取舍一致）：
    /// <paramref name="type"/> 须满足 <see cref="TransactionTypeExtensions.IsUserRecordable"/>、
    /// <paramref name="amount"/> 须大于 0，且 <paramref name="account"/> 与
    /// <paramref name="counterpartyAccount"/> 须是 <c>IAccountService.FindVisibleAsync</c> 取得的实体。
    /// 这些由端点层拦下并给出 400/404。
    /// </para>
    /// <para>
    /// **本服务刻意不注入 <see cref="IAccountService"/>**：后者已注入本服务
    /// （账户创建时要写期初分录），反向注入会构成循环依赖。可见性判定因此留在端点层，
    /// 服务层只负责写入——与 <c>EntryQueryService</c> 可以放心依赖
    /// <see cref="IAccountService"/> 的方向刚好相反，勿将判定挪进来。
    /// </para>
    /// <para>
    /// 对手方为空时取该账套内**该币种**的系统账本账户（见 <see cref="Account.IsSystem"/>），
    /// 不存在时按需自动创建，与期初余额同一口径。一个账套内每个币种各有一个账本账户。
    /// </para>
    /// </remarks>
    Task<Transaction> RecordUserTransactionAsync(
        Account account,
        Account? counterpartyAccount,
        Category? category,
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
