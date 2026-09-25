using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的交易业务实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <remarks>
/// 本服务刻意**只依赖 <see cref="ISqlSugarClient"/>、不依赖 <see cref="IAccountService"/>**：
/// 账本账户的按需创建是记账流程的内部动作（它的期初金额恒为 0、不需要期初入账），
/// 走账户服务反而会绕成「创建账户 → 期初入账 → 创建账户」的递归。这里直接写库，
/// 也因此 <see cref="AccountService"/> 可以放心注入本服务而不会形成循环依赖。
/// <para>
/// **反向同理**：用户记账（<see cref="RecordUserTransactionAsync"/>）的目标账户由端点层经
/// <c>IAccountService.FindVisibleAsync</c> 取好后传入，本服务不自行判定可见性——
/// 一旦在此注入 <see cref="IAccountService"/> 就会与 <see cref="AccountService"/> 形成循环依赖。
/// </para>
/// </remarks>
public sealed class TransactionService(ISqlSugarClient db) : ITransactionService
{
    /// <summary>
    /// 账本账户的名称前缀。
    /// 仅用于人工辨认——账户的识别依据是 <see cref="Account.IsSystem"/> 与
    /// <see cref="Account.CurrencyCode"/> 而非名称，故用户改名后系统仍认得它，不会另建一个。
    /// </summary>
    /// <remarks>
    /// 名称保留「期初」二字是因为它诞生于期初入账；如今收入与支出也以同一账户配平，
    /// 但该账户**从不出现在任何界面上**，故这个偏窄的名字只在库里可辨，不影响使用。
    /// <para>
    /// 实际名称还带币种后缀（见 <see cref="BuildLedgerName"/>）：一个账套内**每个币种各有一个**
    /// 账本账户，不带后缀则库里若干行同名，排查数据时无从区分。
    /// </para>
    /// <para>
    /// **既有数据库中名为「期初账本」（无后缀）的那一行不做改名迁移**：它的身份由
    /// <see cref="Account.IsSystem"/> 与 <see cref="Account.CurrencyCode"/> 判定，名称差异对功能零影响；
    /// 为一个从不出现在界面上的名称增加一次数据迁移并不划算。
    /// </para>
    /// </remarks>
    private const string OPENING_LEDGER_NAME = "期初账本";

    /// <summary>期初交易的固定摘要。</summary>
    private const string OPENING_SUMMARY = "期初余额";

    /// <inheritdoc />
    public async Task<bool> RecordOpeningBalanceAsync(
        Account account,
        int? createdByUserId,
        DateTime? occurredAt,
        CancellationToken cancellationToken = default)
    {
        // 零额期初不写分录：零额分录不含信息，只会让「期初 0 的账户」平白多出一笔交易。
        // 余额派生不受影响——无分录的账户汇总恒为 0，与期初金额一致。
        if (account.InitialBalance == 0)
        {
            return false;
        }

        // 已有期初分录则不再写第二条：账户创建与升级回填共用本方法，回填必须幂等
        if (await HasOpeningEntryAsync(account.Id, cancellationToken))
        {
            return false;
        }

        // 期初的对手方恒为该账户**所属币种**的账本账户：一个账套内每个币种各有一个账本账户，
        // 与收入/支出同一口径。用其它币种的账本配平会让「借方合计 == 贷方合计」在跨币种下失去意义。
        var ledger = await EnsureLedgerAccountAsync(account.AccountSetId, account.CurrencyCode, cancellationToken);

        // 期初账本自身期初金额恒为 0（见 EnsureLedgerAccountAsync），不可能走到这里；
        // 万一真被传入（例如有人手工把它的期初金额改成了非 0），方向相反的两条明细会记在同一账户上，
        // 汇总为 0、交易自相抵，不会污染余额——故无需额外分支。
        var amount = Math.Abs(account.InitialBalance);

        // 目标账户按「期初金额的符号」定方向：正数入借方（余额增加）、负数入贷方（余额减少）；
        // 账本账户取相反方向。两条明细金额相同，复式记账的配平约束天然成立。
        var targetDirection = account.InitialBalance > 0 ? EntryDirection.Debit : EntryDirection.Credit;
        var ledgerDirection = targetDirection == EntryDirection.Debit ? EntryDirection.Credit : EntryDirection.Debit;

        var transaction = new Transaction
        {
            AccountSetId = account.AccountSetId,
            Type = TransactionType.OpeningBalance,
            // 期初的业务时刻由调用方指定（账户新建时由用户选），未指定才退回账户建档时刻：
            // 升级回填的历史账户无处可考「这笔期初是什么时候的余额」，只能取建档时刻。
            // 账户表刻意**不存**期初时间列——那会让同一事实两处存储、迟早漂移（见 Account 的类头注释）。
            // （账户表只存 UTC，读回时 Kind 为 Unspecified，值与写入时一致；
            // Kind 的统一标记在 DTO 层由 SpecifyKind 完成）
            OccurredAt = occurredAt ?? account.CreatedAt,
            Summary = OPENING_SUMMARY,
            CreatedByUserId = createdByUserId,
        };

        await WriteBalancedTransactionAsync(
            transaction,
            account,
            targetDirection,
            ledger,
            ledgerDirection,
            amount,
            cancellationToken);

        return true;
    }

    /// <inheritdoc />
    public async Task<Transaction> RecordUserTransactionAsync(
        Account account,
        Account? counterpartyAccount,
        Category? category,
        TransactionType type,
        decimal amount,
        DateTime occurredAt,
        string summary,
        string? remark,
        int createdByUserId,
        CancellationToken cancellationToken = default)
    {
        EnsureWriteInvariants(account, counterpartyAccount, category);

        // 未指定对手方 → 用该账户币种的系统账本账户，语义即「款项来自/去往账套之外」。
        // 指定了对手方 → 直接用：此时这是一笔两个真实账户之间的转账，账本账户完全不参与。
        // 转账**必然**走「指定了对手方」这一支：它的转入账户是必填的，端点层已拦下两者皆空的情形。
        var counterparty = counterpartyAccount
            ?? await EnsureLedgerAccountAsync(account.AccountSetId, account.CurrencyCode, cancellationToken);

        // 方向由交易类型决定而非金额符号：金额恒为正，两条明细等额反向，配平天然成立。
        // 「哪个方向算主账户方向」只有一处定义（含收入为何是借方、支出与转账为何同向），
        // 见 TransactionTypeExtensions.PrimaryDirection。
        var targetDirection = type.PrimaryDirection()
            // 期初余额没有主账户方向（它的方向由期初金额符号决定），故本方法只接受用户可记账的类型。
            // 端点层已按 IsUserRecordable 拦下其余取值，走到这里说明是编码错误，抛异常而非静默取一个默认方向
            ?? throw new ArgumentException($"交易类型 {type} 不支持用户记账，无法确定主账户明细的方向", nameof(type));
        var counterpartyDirection = targetDirection == EntryDirection.Debit
            ? EntryDirection.Credit
            : EntryDirection.Debit;

        var transaction = new Transaction
        {
            AccountSetId = account.AccountSetId,
            Type = type,
            OccurredAt = occurredAt,
            Summary = summary,
            Remark = remark,
            // 分类为空即「未分类」，是合法状态（记账时分类可选），不填哨兵值
            CategoryId = category?.Id,
            CreatedByUserId = createdByUserId,
        };

        await WriteBalancedTransactionAsync(
            transaction,
            account,
            targetDirection,
            counterparty,
            counterpartyDirection,
            Math.Abs(amount),
            cancellationToken);

        return transaction;
    }

    /// <inheritdoc />
    public async Task<Transaction?> FindAsync(
        int transactionId,
        int accountSetId,
        CancellationToken cancellationToken = default)
    {
        var matched = await db.Queryable<Transaction>()
            .Where(candidate => candidate.Id == transactionId && candidate.AccountSetId == accountSetId)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count == 0 ? null : matched[0];
    }

    /// <inheritdoc />
    public async Task<EditableTransaction?> FindEditableAsync(
        int transactionId,
        int accountSetId,
        CancellationToken cancellationToken = default)
    {
        // 取交易头这一段与 FindAsync 同一口径，直接委托它——两处各写一份查询，
        // 日后「怎么算属于本账套」若有变化，就会有一处悄悄不跟
        var transaction = await FindAsync(transactionId, accountSetId, cancellationToken);

        if (transaction is null)
        {
            return null;
        }

        // 主账户方向由交易类型决定；期初余额没有这个概念（null），故期初交易在此返回 null——
        // 它的两条明细（目标账户 + 账本账户）定不出主次，本方法无法回答「改哪一条」。
        // **这个 null 不等于「该笔账不存在」**：调用方按类型先行判定，期初交易在那一步就拿到 400，
        // 走不到这里（见 TransactionEndpoints 的改名端点注释）。
        if (transaction.Type.PrimaryDirection() is not { } primaryDirection)
        {
            return null;
        }

        var entries = await db.Queryable<TransactionEntry>()
            .Where(entry => entry.TransactionId == transaction.Id)
            .OrderBy(entry => entry.Id)
            .ToListAsync(cancellationToken);

        // 恰两条、且能定出主账户那条与对手方那条，才是本能力认识的形态。
        // 每笔交易恒有借贷两条明细（唯一写账入口 WriteBalancedTransactionAsync 保证），
        // 出现别的形态说明数据被外力改过——此处不做「尽量猜一条」的兜底：
        // 猜错方向会让编辑写到错误的账户上，比干脆拒绝危险得多。
        // 对手方的取法与查询侧同义（「同交易中方向不同的第一条」，见 EntryQueryService），
        // 两处口径一致才不会出现「界面上看到的对手方」与「编辑时改的对手方」不是同一个。
        var primary = entries.FirstOrDefault(entry => entry.Direction == primaryDirection);
        var counterparty = entries.FirstOrDefault(entry => entry.Direction != primaryDirection);

        if (entries.Count != 2 || primary is null || counterparty is null)
        {
            return null;
        }

        var accountIds = new[] { primary.AccountId, counterparty.AccountId };
        var accounts = await db.Queryable<Account>()
            .Where(account => accountIds.Contains(account.Id))
            .ToListAsync(cancellationToken);

        // 明细指向的账户在库里不存在（外键未被数据库强制）时同样归入「形态不认识」：
        // 两个端点缺一不可，凑不出完整的一笔账。
        var primaryAccount = accounts.FirstOrDefault(account => account.Id == primary.AccountId);
        var counterpartyAccount = accounts.FirstOrDefault(account => account.Id == counterparty.AccountId);

        if (primaryAccount is null || counterpartyAccount is null)
        {
            return null;
        }

        return new EditableTransaction(transaction, primaryAccount, counterpartyAccount);
    }

    /// <inheritdoc />
    public async Task<Transaction?> UpdateUserTransactionAsync(
        Transaction transaction,
        Account account,
        Account? counterpartyAccount,
        Category? category,
        decimal amount,
        DateTime occurredAt,
        string summary,
        string? remark,
        CancellationToken cancellationToken = default)
    {
        // 期初余额改不得：它的金额恒等于其目标账户的期初余额、且每账户至多一条
        // （后者是 BackfillOpeningBalancesAsync 的幂等判据）。允许改它，两条不变量会同时失效，
        // 且「账户的期初金额」会与「那条期初分录」脱钩。端点层已先判一次并给出 400，
        // 此处再判依然是「守住唯一写账入口」——本方法与 RecordUserTransactionAsync 是同一扇门。
        if (!transaction.Type.IsUserRecordable())
        {
            throw new ArgumentException($"交易类型 {transaction.Type} 不支持修改", nameof(transaction));
        }

        // 币种一致与分类同账套两条不变量与新建**共用同一份判据**：改一笔账与记一笔账
        // 在这两点上是同一件事，各写一份迟早只改一处
        EnsureWriteInvariants(account, counterpartyAccount, category);

        // 对手方为空 → 按**新主账户**的币种取账本账户：这样「把一笔支出从 CNY 账户改到 USD 账户」
        // 也不会把人民币的金额记到美元的账本上（跨币种配平是没有意义的数）。
        var counterparty = counterpartyAccount
            ?? await EnsureLedgerAccountAsync(transaction.AccountSetId, account.CurrencyCode, cancellationToken);

        // 主账户方向由**类型**决定，而类型不可改，故两条明细的方向一律不变。
        var primaryDirection = transaction.Type.PrimaryDirection()
            ?? throw new ArgumentException($"交易类型 {transaction.Type} 不支持修改，无法确定主账户明细的方向", nameof(transaction));

        // 交易头只改用户可改的字段：Type / AccountSetId / CreatedByUserId 一律不动
        // ——改账不换记账人、不换账套（同 AccountService.UpdateAsync「不可改字段不进契约」的取舍）。
        transaction.OccurredAt = occurredAt;
        transaction.Summary = summary;
        transaction.Remark = remark;
        transaction.CategoryId = category?.Id;

        await db.Ado.UseTranAsync(async () =>
        {
            // 先在事务内取回两条明细：取回与改写之间若被并发改动，事务内的读能看到一致快照。
            // 与 FindEditableAsync 同一取法（按明细主键升序），两处口径一致。
            var entries = await db.Queryable<TransactionEntry>()
                .Where(entry => entry.TransactionId == transaction.Id)
                .OrderBy(entry => entry.Id)
                .ToListAsync(cancellationToken);

            var primary = entries.FirstOrDefault(entry => entry.Direction == primaryDirection);
            var other = entries.FirstOrDefault(entry => entry.Direction != primaryDirection);

            // 形态不是「恰两条、主账户与对手方各一条」时抛异常而不猜：FindEditableAsync 已用同一判据
            // 把这类账挡在端点之外，走到这里说明库里出现了本系统产生不了的形态（数据被外力改过）。
            // 静默改写其中两条会留下第三条对不上的明细，那才是真正的账不平。
            if (entries.Count != 2 || primary is null || other is null)
            {
                throw new InvalidOperationException(
                    $"交易 {transaction.Id} 的明细不是「借贷各一条」，无法改写");
            }

            // 只改账户与金额，方向保持原值：主账户那条的方向由类型决定（类型不可改），
            // 对手方那条恒取相反方向。删旧插新会换掉明细主键，而主键是查询侧
            // 「同一时刻多条明细」的稳定排序键（见 EntryQueryService 的三级排序），换它会让翻页行序漂移。
            primary.AccountId = account.Id;
            primary.Amount = amount;
            other.AccountId = counterparty.Id;
            other.Amount = amount;

            await db.Updateable(transaction)
                .UpdateColumns(tx => new { tx.OccurredAt, tx.Summary, tx.Remark, tx.CategoryId })
                .ExecuteCommandAsync(cancellationToken);

            await db.Updateable(new List<TransactionEntry> { primary, other })
                .ExecuteCommandAsync(cancellationToken);
        });

        return transaction;
    }

    /// <summary>
    /// 校验「唯一写账入口」的两条不变量：币种一致、分类同账套。
    /// </summary>
    /// <param name="account">目标账户（收入账户 / 支出账户 / 转出账户）。</param>
    /// <param name="counterpartyAccount">对手方账户；<c>null</c> 即「未指定」，不看币种。</param>
    /// <param name="category">分类；<c>null</c> 即「未分类」。</param>
    /// <remarks>
    /// 新建与修改两条写入路径**共用本方法**：这两条不变量在两处是同一件事，
    /// 各写一份则只会在改其一的时候漏掉另一处。
    /// <para>
    /// **抛异常而非返回 null**：调用方传错参数是编码错误，不是用户可以修正的输入错误，
    /// 静默降级只会掩盖 bug。端点层已先判一次并给出 400，此处再判是因为本方法是唯一的写入口——
    /// 把它守在这里，「跨币种交易」与「跨账套分类」在库里就不可能存在，
    /// 与「配平由等额反向保证」同一性质（分类表没有可见性维度可依赖，这道卡只能设在写入路径上）。
    /// </para>
    /// </remarks>
    private static void EnsureWriteInvariants(Account account, Account? counterpartyAccount, Category? category)
    {
        if (counterpartyAccount is not null
            && !string.Equals(counterpartyAccount.CurrencyCode, account.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"对手方账户的币种（{counterpartyAccount.CurrencyCode}）与目标账户（{account.CurrencyCode}）不一致",
                nameof(counterpartyAccount));
        }

        if (category is not null && category.AccountSetId != account.AccountSetId)
        {
            throw new ArgumentException(
                $"分类（主键 {category.Id}）属于账套 {category.AccountSetId}，与交易所在账套 {account.AccountSetId} 不一致",
                nameof(category));
        }
    }

    /// <summary>
    /// 写入一笔「交易 + 借贷两条等额反向明细」，并回填交易主键。
    /// </summary>
    /// <param name="transaction">待写入的交易；除主键外的字段须已填好。</param>
    /// <param name="targetAccount">目标账户（用户选定的那个账户）。</param>
    /// <param name="targetDirection">目标账户的借贷方向。</param>
    /// <param name="counterpartyAccount">
    /// 对手方账户：期初余额恒为系统账本账户，用户记账时可能是账本账户（未指定对手方）
    /// 或一个真实账户（用户指定了来源/目标账户）。
    /// </param>
    /// <param name="counterpartyDirection">对手方账户的借贷方向，须与 <paramref name="targetDirection"/> 相反。</param>
    /// <param name="amount">两条明细的金额（恒为正、且相等）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// 期初余额与用户记账两条写入路径共用本方法：两者的差异只在「方向怎么定」与「对手方是谁」，
    /// 落库动作完全相同，故收敛在此处，避免两份「插交易 + 插两条明细」的代码各自漂移。
    /// <para>
    /// **交易与其明细同事务写入**：否则中途失败会留下一笔没有任何明细的「空交易」，
    /// 它既进不了余额汇总，又会让回填的查重判定误以为该账户已经入账。
    /// </para>
    /// <para>
    /// **本方法不校验两个账户的币种是否一致**：那是调用方（<see cref="RecordUserTransactionAsync"/>）
    /// 的职责，它在调进来之前已经判过。
    /// </para>
    /// </remarks>
    private async Task WriteBalancedTransactionAsync(
        Transaction transaction,
        Account targetAccount,
        EntryDirection targetDirection,
        Account counterpartyAccount,
        EntryDirection counterpartyDirection,
        decimal amount,
        CancellationToken cancellationToken)
    {
        await db.Ado.UseTranAsync(async () =>
        {
            transaction.Id = await db.Insertable(transaction).ExecuteReturnIdentityAsync(cancellationToken);

            await db.Insertable(new List<TransactionEntry>
            {
                new()
                {
                    TransactionId = transaction.Id,
                    AccountId = targetAccount.Id,
                    Direction = targetDirection,
                    Amount = amount,
                },
                new()
                {
                    TransactionId = transaction.Id,
                    AccountId = counterpartyAccount.Id,
                    Direction = counterpartyDirection,
                    Amount = amount,
                },
            }).ExecuteCommandAsync(cancellationToken);
        });
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, decimal>> SumSignedAmountsAsync(
        int accountSetId,
        IReadOnlyCollection<int> accountIds,
        CancellationToken cancellationToken = default)
    {
        // 空集合不查库：`IN ()` 是无意义的条件，直接给出空结果
        if (accountIds.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        var ids = accountIds as int[] ?? [.. accountIds];

        // 按「账户 + 方向」分组取回：同一账户的借方与贷方各占一行，折算符号后在内存里合并。
        // 不查原始明细逐条累加：那会把整本账读进内存，且失去了数据库聚合的意义。
        var rows = await db.Queryable<TransactionEntry>()
            .LeftJoin<Transaction>((entry, tx) => entry.TransactionId == tx.Id)
            .Where((entry, tx) => tx.AccountSetId == accountSetId && ids.Contains(entry.AccountId))
            .GroupBy((entry, tx) => new { entry.AccountId, entry.Direction })
            .Select((entry, tx) => new
            {
                entry.AccountId,
                entry.Direction,
                Total = SqlFunc.AggregateSum(entry.Amount),
            })
            .ToListAsync(cancellationToken);

        // 「明细方向 → 账户余额」的换算定义见 EntryDirectionExtensions.SignedAmount：
        // 借方为正、贷方为负。此处与「账目明细出参」共用同一个定义，不自行再写一遍三元表达式。
        var balances = new Dictionary<int, decimal>();
        foreach (var row in rows)
        {
            var signed = row.Direction.SignedAmount(row.Total);
            balances[row.AccountId] = balances.TryGetValue(row.AccountId, out var current)
                ? current + signed
                : signed;
        }

        return balances;
    }

    /// <inheritdoc />
    public async Task<int> BackfillOpeningBalancesAsync(CancellationToken cancellationToken = default)
    {
        // 先取回「已经期初入账」的账户集合。作为对手方出现在期初分录里的账本账户也会被取到，
        // 但它们期初金额为 0，随后会被初始金额条件过滤掉，无需在此特意排除。
        var recorded = await db.Queryable<TransactionEntry>()
            .LeftJoin<Transaction>((entry, tx) => entry.TransactionId == tx.Id)
            .Where((entry, tx) => tx.Type == TransactionType.OpeningBalance)
            .Select((entry, tx) => entry.AccountId)
            .ToListAsync(cancellationToken);

        var recordedIds = recorded.ToHashSet();

        // 零额账户天然不需要期初分录（见 RecordOpeningBalanceAsync），不必进入候选
        var candidates = await db.Queryable<Account>()
            .Where(account => account.InitialBalance != 0)
            .OrderBy(account => account.Id)
            .ToListAsync(cancellationToken);

        var written = 0;
        foreach (var account in candidates)
        {
            if (recordedIds.Contains(account.Id))
            {
                continue;
            }

            // 回填出来的期初交易没有记账人可考：账户表本身不记创建者。
            // 期初时间同样指定不了，传 null 退回账户建档时刻——这正是本次改动前的行为。
            if (await RecordOpeningBalanceAsync(account, null, null, cancellationToken))
            {
                written++;
            }
        }

        return written;
    }

    /// <summary>判断账户是否已有期初余额分录（作为目标账户，而非对手方）。</summary>
    /// <param name="accountId">账户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已存在返回 <c>true</c>。</returns>
    /// <remarks>
    /// 查重不依赖数据库唯一索引：Sqlite 与 PostgreSQL 都不支持「带部分条件的唯一索引」之外的
    /// 跨库一致写法，而「每账户至多一条期初分录」是本服务写入时的一个不变量，
    /// 放在写入路径上判一次，比引入一个两种库语义有出入的约束更可靠。
    /// </remarks>
    private async Task<bool> HasOpeningEntryAsync(int accountId, CancellationToken cancellationToken)
    {
        var matched = await db.Queryable<TransactionEntry>()
            .LeftJoin<Transaction>((entry, tx) => entry.TransactionId == tx.Id)
            .Where((entry, tx) =>
                entry.AccountId == accountId && tx.Type == TransactionType.OpeningBalance)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count > 0;
    }

    /// <summary>
    /// 取该账套内**指定币种**的期初账本账户，不存在则创建（每账套每币种至多一个）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="currencyCode">币种代码（对应 <see cref="Account.CurrencyCode"/>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>该币种的期初账本账户（已落库、有主键）。</returns>
    /// <remarks>
    /// **账本账户按币种分开**：一笔交易的借贷两条明细必须同币种，把人民币的收入与美元的支出
    /// 记在同一个账本账户上，它的余额会变成两种货币的裸加总——一个没有任何意义的数。
    /// 故账本账户的粒度是「账套 × 币种」。
    /// <para>
    /// **查找依据是 <see cref="Account.IsSystem"/> 与 <see cref="Account.CurrencyCode"/>，不是名称**：
    /// 名称对用户可改（<c>AccountService.UpdateAsync</c> 允许改名），拿它当身份依据，
    /// 一次改名就会让系统认不出既有账本账户、再建一个出来。
    /// </para>
    /// <para>
    /// **查找时忽略 <see cref="Account.IsActive"/>**：账本账户被停用后仍复用同一条。
    /// 否则停用一次就会再建一个，同一币种出现两个账本账户，期初余额被劈成两半。
    /// </para>
    /// <para>
    /// 币种代码按**不区分大小写**比对：账户侧的代码写入时已统一大写，此处容错是为了
    /// 让调用方不必关心大小写；库里若因历史原因存在小写行，也不会被重复建号。
    /// </para>
    /// </remarks>
    private async Task<Account> EnsureLedgerAccountAsync(
        int accountSetId,
        string currencyCode,
        CancellationToken cancellationToken)
    {
        var normalized = currencyCode.Trim().ToUpperInvariant();

        var existing = await db.Queryable<Account>()
            .Where(candidate => candidate.AccountSetId == accountSetId && candidate.IsSystem)
            .Where(candidate => candidate.CurrencyCode.ToUpper() == normalized)
            .OrderBy(candidate => candidate.Id)
            .Take(1)
            .ToListAsync(cancellationToken);

        if (existing.Count > 0)
        {
            return existing[0];
        }

        var ledger = new Account
        {
            AccountSetId = accountSetId,
            Name = BuildLedgerName(normalized),
            // 公共归属：账本账户汇总的是整个账套的期初，不属于任何单个成员
            Scope = AccountScope.Public,
            OwnerUserId = null,
            Type = AccountType.Ledger,
            // 期初金额为 0：账本账户自身的期初由各账户的期初分录累积出来。
            // 若再给它一个期初金额，它自己又需要一个对手方，递归无从终止。
            InitialBalance = 0,
            CurrencyCode = normalized,
            IsActive = true,
            IsSystem = true,
        };

        ledger.Id = await db.Insertable(ledger).ExecuteReturnIdentityAsync(cancellationToken);
        return ledger;
    }

    /// <summary>
    /// 拼账本账户的名称：<c>期初账本（币种代码）</c>。
    /// </summary>
    /// <param name="currencyCode">已归一化为大写的币种代码。</param>
    /// <returns>账本账户名称。</returns>
    /// <remarks>
    /// 带币种后缀是为**库内可辨**：一个账套内每个币种各有一个账本账户，
    /// 不带后缀则若干行同名，排查数据时无从区分。该名称从不出现在任何界面上，故无需与界面文案对齐。
    /// </remarks>
    private static string BuildLedgerName(string currencyCode) => $"{OPENING_LEDGER_NAME}（{currencyCode}）";
}
