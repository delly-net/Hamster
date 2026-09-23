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
/// **反向同理**：用户记账（<see cref="RecordIncomeExpenseAsync"/>）的目标账户由端点层经
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
            // 期初的业务时刻即账户建立的时刻（账户表只存 UTC，读回时 Kind 为 Unspecified，
            // 值与写入时一致；Kind 的统一标记在 DTO 层由 SpecifyKind 完成）
            OccurredAt = account.CreatedAt,
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
    public async Task<Transaction> RecordIncomeExpenseAsync(
        Account account,
        Account? counterpartyAccount,
        TransactionType type,
        decimal amount,
        DateTime occurredAt,
        string summary,
        string? remark,
        int createdByUserId,
        CancellationToken cancellationToken = default)
    {
        // 币种不同就无法交易——这是记账的**核心不变量**，正常路径由端点层拦下并给 400，
        // 此处再判一次：本方法是唯一写账入口，把它守在这里，「跨币种交易」在库里就不可能存在，
        // 与「配平由等额反向保证」同一性质。抛异常而非返回 null：调用方传错参数是编码错误，
        // 不是用户可以修正的输入错误，静默降级只会掩盖 bug。
        if (counterpartyAccount is not null
            && !string.Equals(counterpartyAccount.CurrencyCode, account.CurrencyCode, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"对手方账户的币种（{counterpartyAccount.CurrencyCode}）与目标账户（{account.CurrencyCode}）不一致",
                nameof(counterpartyAccount));
        }

        // 未指定对手方 → 用该账户币种的系统账本账户，语义即「款项来自/去往账套之外」。
        // 指定了对手方 → 直接用：此时这是一笔两个真实账户之间的转账，账本账户完全不参与。
        var counterparty = counterpartyAccount
            ?? await EnsureLedgerAccountAsync(account.AccountSetId, account.CurrencyCode, cancellationToken);

        // 收入使目标账户余额增加（借方）、支出使其减少（贷方）；对手方一律取相反方向。
        // 方向由交易类型决定而非金额符号：金额恒为正，两条明细等额反向，配平天然成立。
        var targetDirection = type == TransactionType.Income
            ? EntryDirection.Debit
            : EntryDirection.Credit;
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
    /// **本方法不校验两个账户的币种是否一致**：那是调用方（<see cref="RecordIncomeExpenseAsync"/>）
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

            // 回填出来的期初交易没有记账人可考：账户表本身不记创建者
            if (await RecordOpeningBalanceAsync(account, null, cancellationToken))
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
