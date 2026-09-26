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
    /// <param name="occurredAt">
    /// 期初的业务时刻（UTC）；**未指定时退回账户建档时刻**（<see cref="Account.CreatedAt"/>）。
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
    /// <para>
    /// <paramref name="occurredAt"/> 就是「期初时间」的**唯一落点**：账户表不存该列，
    /// 用户选的期初时间只活在这笔分录的 <c>occurred_at</c> 上（连带 #36「不得引入可漂移重复列」）。
    /// </para>
    /// </remarks>
    Task<bool> RecordOpeningBalanceAsync(
        Account account,
        int? createdByUserId,
        DateTime? occurredAt,
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
    /// <param name="tags">
    /// 要挂到这笔交易上的标签集合，**可空**（<c>null</c> 与空集合同义，即「没有标签」，
    /// 是正常状态——记账时标签是可选的）。
    /// 非空时其中每个标签都必须是**同一账套**内的标签，否则抛 <see cref="ArgumentException"/>
    /// （见下方「不变量」）。
    /// <para>
    /// 标签由端点层解析后传入（可能是用户选中的、也可能是按名自动创建的），
    /// 本服务不查标签表——与 <paramref name="category"/> 同一取舍。
    /// 与分类**唯一的差别是基数**：分类至多一个（交易头的一列），标签可以有多个
    /// （落在 <c>hamster_transaction_tag</c> 子表里）。
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
    /// **不变量：分类与标签都不会跨账套。** <paramref name="category"/> 非空时其
    /// <see cref="Category.AccountSetId"/> 必须与 <paramref name="account"/> 的一致，
    /// <paramref name="tags"/> 中的每个标签同理，否则抛 <see cref="ArgumentException"/>。
    /// 分类表与标签表都没有可见性维度可依赖（与账户不同），这道卡只能设在写入路径上。
    /// </para>
    /// <para>
    /// **标签与交易头、两条明细同处一个事务**（<c>db.Ado.UseTranAsync</c>）：
    /// 否则中途失败会留下「交易记下了、标签没挂上」的半成品，而用户看到的是那一笔账已经记好了。
    /// 标签没有单独的写入端点，正是为了不让这条原子性有被绕过的可能。
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
        IReadOnlyCollection<Tag>? tags,
        TransactionType type,
        decimal amount,
        DateTime occurredAt,
        string summary,
        string? remark,
        int createdByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键取当前账套内的交易头。
    /// </summary>
    /// <param name="transactionId">交易主键。</param>
    /// <param name="accountSetId">当前账套主键；交易不属于该账套时视为不存在。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>交易实体；不存在或不属于该账套时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 与 <see cref="FindEditableAsync"/> 的分工是**「在不在」与「能不能改」**：本方法只看存在性与归属，
    /// 不看形态、也不取明细；后者还要定出主账户与对手方两条明细挂靠的账户。
    /// <para>
    /// 编辑端点需要**在解析形态之前**先看一眼类型（期初余额交易不可改，且它与「不存在」响应码不同），
    /// 而形态解析拿不到期初交易的类型——它没有主账户方向，只能返回 <c>null</c>。
    /// 两件事挤在一次查询里，就必然有一方的判据落空。
    /// </para>
    /// </remarks>
    Task<Transaction?> FindAsync(
        int transactionId,
        int accountSetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取一笔待编辑的交易，连同其借贷两条明细挂靠的**账户实体**。
    /// </summary>
    /// <param name="transactionId">交易主键。</param>
    /// <param name="accountSetId">当前账套主键；交易不属于该账套时视为不存在。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 交易头与两个端点账户；交易不存在、不属于该账套、或**形态不是本能力认识的那种**
    /// （明细不是恰两条、或缺少主账户方向的那条）时返回 <c>null</c>。
    /// </returns>
    /// <remarks>
    /// 返回**账户实体**而非仅主键：编辑端点要判「这两个账户对我是否可见」，
    /// 而账本账户必须与「他人不可见的账户」区分开——它对任何人的可见性判定都是 <c>null</c>，
    /// 却是每一笔用户收支的合法对手方。区分依据是 <see cref="Account.IsSystem"/>，
    /// 故端点需要拿到实体本身。
    /// <para>
    /// 可见性判定本身**不在这里**做（本服务不注入 <see cref="IAccountService"/>，
    /// 理由见 <see cref="RecordUserTransactionAsync"/>），本方法只负责把事实取回来。
    /// </para>
    /// <para>
    /// **形态不认识时返回 <c>null</c> 而不是抛异常**：这类数据在本系统无法产生
    /// （唯一写账入口 <c>TransactionService.WriteBalancedTransactionAsync</c> 恒写入两条等额反向的明细），
    /// 返回 null 的后果是该笔账在编辑端点上一律 404，不会把用户引向一次半途的改写。
    /// </para>
    /// </remarks>
    Task<EditableTransaction?> FindEditableAsync(
        int transactionId,
        int accountSetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 改写一笔已存在的用户交易：交易头 + 借贷**两条明细一并改写**，改完仍配平。
    /// </summary>
    /// <param name="transaction">
    /// 待改写的交易实体（须已落库、且由 <see cref="FindEditableAsync"/> 取回），
    /// 其 <see cref="Transaction.Type"/> 不可改——两条明细的**方向由类型决定**，
    /// 改类型等于要求整笔重算方向，而「收支互改」在语义上是两笔不同的账。
    /// </param>
    /// <param name="account">
    /// 新的主账户（用户选定的那个账户），须已取得主键且为当前用户**可见**的账户。
    /// 收入时它是收入账户，支出与转账时它是支出账户 / 转出账户。
    /// </param>
    /// <param name="counterpartyAccount">
    /// 新的对手方账户，**可空**；语义与 <see cref="RecordUserTransactionAsync"/> 完全一致
    /// （<c>null</c> 即落回 <paramref name="account"/> **所属币种**的系统账本账户——
    /// 按新主账户的币种取，故改到一个不同币种的账户上也不会跨币种）。
    /// </param>
    /// <param name="category">新的分类，<c>null</c> 即「未分类」；非空时须与交易同账套。</param>
    /// <param name="tags">
    /// 新的标签集合，<c>null</c> 与空集合同义，即「把这笔交易的标签清空」——
    /// **是覆盖而非保留**，与 <paramref name="remark"/> 同一语义（要保留原标签就把它们原样传回来）。
    /// </param>
    /// <param name="amount">新的金额，**恒为正**。</param>
    /// <param name="occurredAt">新的业务发生时间（UTC）。</param>
    /// <param name="summary">新的摘要（调用方需保证已 Trim 且非空）。</param>
    /// <param name="remark">新的备注；无备注时传 <c>null</c>（**是覆盖而非保留**：留空即清空备注）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>改写后的交易（即传入的实体）；交易已被并发删除时返回 <c>null</c>。</returns>
    /// <remarks>
    /// **就地 UPDATE 原有的两条明细，不删旧插新**：明细主键是查询侧「同一时刻多条明细」的
    /// 稳定排序键（见 <c>EntryQueryService</c> 的三级排序），换主键会让翻页时的行序漂移。
    /// <para>
    /// **交易与明细同事务**（<c>db.Ado.UseTranAsync</c>）：中途失败会留下一笔
    /// 「头已改、明细未改」的账，其金额与明细对不上。
    /// </para>
    /// <para>
    /// **只更新可改字段**：交易头写 <see cref="Transaction.OccurredAt"/> /
    /// <see cref="Transaction.Summary"/> / <see cref="Transaction.Remark"/> /
    /// <see cref="Transaction.CategoryId"/>，明细写 <see cref="TransactionEntry.AccountId"/> 与
    /// <see cref="TransactionEntry.Amount"/>。<see cref="Transaction.Type"/>、
    /// <see cref="Transaction.AccountSetId"/>、<see cref="Transaction.CreatedByUserId"/>
    /// 与明细的 <see cref="TransactionEntry.Direction"/> 一律不动——改账不换记账人、不换账套，
    /// 也不改方向（方向由类型决定，而类型不可改）。同 <c>AccountService.UpdateAsync</c>
    /// 「把不可改字段从契约中整个删掉」的取舍。
    /// </para>
    /// <para>
    /// **余额不需要任何写动作**：余额是派生值（<see cref="SumSignedAmountsAsync"/> 对明细的
    /// 有符号汇总），明细一改，新旧账户的余额下次查询即为新值。库里没有余额列可写，也不该有。
    /// </para>
    /// <para>
    /// **标签整体替换**（先删全部关联行再按新集合插入），与明细的就地 UPDATE **刻意不同**：
    /// 明细主键是查询侧的排序键、换不得，而标签关联行不参与任何排序或翻页，没有别的东西依赖它的主键。
    /// 两条口径的差异源自「有没有别的东西依赖这个主键」，不是随意的松紧不一
    /// （见 <c>TransactionTag</c> 的类头注释）。删除与插入都在同一个事务内，故不存在「删完没插上」的空窗。
    /// </para>
    /// <para>
    /// **不变量与 <see cref="RecordUserTransactionAsync"/> 同源**（币种一致、分类与标签同账套），
    /// 由同一份判定守卫；<paramref name="transaction"/>.Type 不满足
    /// <see cref="TransactionTypeExtensions.IsUserRecordable"/> 时抛 <see cref="ArgumentException"/>
    /// ——期初余额交易改不得（它的金额恒等于账户的期初余额、且每账户至多一条，
    /// 允许改这两条不变量会同时失效）。端点层已先判一次并给出 400，此处再判是因为
    /// 本方法与 <see cref="RecordUserTransactionAsync"/> 一样是**唯一写账入口**。
    /// </para>
    /// </remarks>
    Task<Transaction?> UpdateUserTransactionAsync(
        Transaction transaction,
        Account account,
        Account? counterpartyAccount,
        Category? category,
        IReadOnlyCollection<Tag>? tags,
        decimal amount,
        DateTime occurredAt,
        string summary,
        string? remark,
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

/// <summary>
/// 一笔待编辑交易及其两个端点账户。
/// </summary>
/// <param name="Transaction">交易头（已落库）。</param>
/// <param name="PrimaryAccount">
/// **主账户**明细挂靠的账户（即用户记账时选定的那个账户：收入账户 / 支出账户 / 转出账户）。
/// 哪条明细是主账户那条，由 <see cref="TransactionTypeExtensions.PrimaryDirection"/> 判定。
/// </param>
/// <param name="CounterpartyAccount">对手方明细挂靠的账户（可能是系统账本账户）。</param>
/// <remarks>
/// 两个账户都可能是**当前用户看不见**的：主账户是他人个人账户时（成员间共享的公共账户上
/// 由别人记的账），或对手方是账本账户时。可见性判定留给端点层——
/// <see cref="ITransactionService"/> 不注入 <c>IAccountService</c>（见其类头注释）。
/// </remarks>
public sealed record EditableTransaction(
    Transaction Transaction,
    Account PrimaryAccount,
    Account CounterpartyAccount);
