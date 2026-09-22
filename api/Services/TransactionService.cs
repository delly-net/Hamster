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
/// </remarks>
public sealed class TransactionService(ISqlSugarClient db) : ITransactionService
{
    /// <summary>
    /// 期初账本账户的名称。
    /// 仅用于人工辨认——账户的识别依据是 <see cref="Account.IsSystem"/> 而非名称，
    /// 故用户改名后系统仍认得它，不会另建一个。
    /// </summary>
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

        var ledger = await EnsureLedgerAccountAsync(account.AccountSetId, cancellationToken);

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

        // 交易与其明细同事务写入：否则中途失败会留下一笔没有任何明细的「空交易」，
        // 它既进不了余额汇总，又会让回填的查重判定误以为该账户已经入账。
        await db.Ado.UseTranAsync(async () =>
        {
            transaction.Id = await db.Insertable(transaction).ExecuteReturnIdentityAsync(cancellationToken);

            await db.Insertable(new List<TransactionEntry>
            {
                new()
                {
                    TransactionId = transaction.Id,
                    AccountId = account.Id,
                    Direction = targetDirection,
                    Amount = amount,
                },
                new()
                {
                    TransactionId = transaction.Id,
                    AccountId = ledger.Id,
                    Direction = ledgerDirection,
                    Amount = amount,
                },
            }).ExecuteCommandAsync(cancellationToken);
        });

        return true;
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

        // 「明细方向 → 账户余额」的唯一换算处：借方为正、贷方为负
        var balances = new Dictionary<int, decimal>();
        foreach (var row in rows)
        {
            var signed = row.Direction == EntryDirection.Debit ? row.Total : -row.Total;
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
    /// 取该账套的期初账本账户，不存在则创建（每账套至多一个）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>期初账本账户（已落库、有主键）。</returns>
    /// <remarks>
    /// **查找时忽略 <see cref="Account.IsActive"/>**：账本账户被停用后仍复用同一条。
    /// 否则停用一次就会再建一个，账套内出现两个账本账户，期初余额被劈成两半。
    /// </remarks>
    private async Task<Account> EnsureLedgerAccountAsync(int accountSetId, CancellationToken cancellationToken)
    {
        var existing = await db.Queryable<Account>()
            .Where(candidate => candidate.AccountSetId == accountSetId && candidate.IsSystem)
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
            Name = OPENING_LEDGER_NAME,
            // 公共归属：账本账户汇总的是整个账套的期初，不属于任何单个成员
            Scope = AccountScope.Public,
            OwnerUserId = null,
            Type = AccountType.Ledger,
            // 期初金额为 0：账本账户自身的期初由各账户的期初分录累积出来。
            // 若再给它一个期初金额，它自己又需要一个对手方，递归无从终止。
            InitialBalance = 0,
            IsActive = true,
            IsSystem = true,
        };

        ledger.Id = await db.Insertable(ledger).ExecuteReturnIdentityAsync(cancellationToken);
        return ledger;
    }
}
