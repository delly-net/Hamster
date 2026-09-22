using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的账目明细查询实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <param name="accounts">
/// 账户服务：本服务只借用它的**可见性判定**（列出当前用户可见的账户），不写任何账户数据。
/// 依赖方向不成环——<see cref="AccountService"/> 只依赖 <see cref="ITransactionService"/>，
/// 不会回头依赖本服务。
/// </param>
public sealed class EntryQueryService(ISqlSugarClient db, IAccountService accounts) : IEntryQueryService
{
    /// <inheritdoc />
    public async Task<EntryQueryPage> QueryAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        DateTime? from,
        DateTime? to,
        IReadOnlyCollection<int>? accountIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // 可见账户集：既是「明细行挂靠账户」的过滤依据，也是「账户名」的来源，一次取回两用。
        // includeInactive 恒为 true：账户是软删除，停用账户上的历史明细仍然查得到（见接口注释）。
        var visible = await accounts.ListByAccountSetAsync(
            accountSetId,
            userId,
            isAdmin,
            includeInactive: true,
            cancellationToken);

        var nameById = visible.ToDictionary(item => item.Account.Id, item => item.Account.Name);

        // 目标账户集：未指定账户时即全部可见账户；指定了则与可见集**求交**而非报错
        // （不可见的账户被静默剔除，交集为空就返回空页——不泄露「该账户是否存在」）。
        var targetIds = accountIds is null || accountIds.Count == 0
            ? nameById.Keys.ToArray()
            : accountIds.Where(nameById.ContainsKey).Distinct().ToArray();

        if (targetIds.Length == 0)
        {
            return EmptyPage(page, pageSize);
        }

        // 条件先落到非空局部变量再进表达式树：SqlSugar 对「DateTime 与 DateTime? 比较」的翻译不可靠，
        // 拆开后表达式里只剩两个 DateTime 的比较。
        var hasFrom = from is not null;
        var fromValue = from ?? default;
        var hasTo = to is not null;
        var toValue = to ?? default;

        // 计数与取数各自新建查询对象：ISugarQueryable 是会被链式方法改写的，
        // 复用同一个实例会让计数结果带上分页条件。
        var total = await BuildBaseQuery(accountSetId, targetIds, hasFrom, fromValue, hasTo, toValue)
            .CountAsync(cancellationToken);

        if (total == 0)
        {
            return EmptyPage(page, pageSize);
        }

        // 先投影再排序分页：投影后的查询只带一个泛型参数，`OrderBy` / `Skip` / `Take` 的签名明确，
        // 不会在「联表双泛型」的链式方法里撞上重载歧义。
        // **`MergeTable()` 不可省**：`Select` 之后 SqlSugar 仍把查询视为双表联查，
        // 直接接单参数 `OrderBy(row => ...)` 会撞上别名一致性检查（要求排序 lambda 的形参名与联表别名一致）
        // 并抛「多表查询存在别名不一致」。MergeTable 把投影结果集并成单表，此后排序分页不再受此限制
        // ——这是该异常提示给出的官方出路。投影里的每个属性都直接来自某个表的列，故排序键仍是确切的表列。
        // 三级排序键的最后一级是明细主键，它让「同一时刻的多条明细」也有确定次序——
        // 否则翻页时同一行可能在两页里各出现一次。
        var rows = await BuildBaseQuery(accountSetId, targetIds, hasFrom, fromValue, hasTo, toValue)
            .Select((entry, tx) => new EntryRow
            {
                EntryId = entry.Id,
                TransactionId = entry.TransactionId,
                AccountId = entry.AccountId,
                Direction = entry.Direction,
                Amount = entry.Amount,
                OccurredAt = tx.OccurredAt,
                Summary = tx.Summary,
                Remark = tx.Remark,
                Type = tx.Type,
            })
            .MergeTable()
            .OrderBy(row => row.OccurredAt, OrderByType.Asc)
            .OrderBy(row => row.TransactionId, OrderByType.Asc)
            .OrderBy(row => row.EntryId, OrderByType.Asc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var counterparties = await ResolveCounterpartiesAsync(rows, nameById, cancellationToken);

        var items = rows
            .Select(row =>
            {
                var counterparty = counterparties[row.EntryId];
                return new EntryQueryRow(
                    row.EntryId,
                    row.TransactionId,
                    row.OccurredAt,
                    row.Summary,
                    row.Remark,
                    row.Type,
                    row.AccountId,
                    nameById[row.AccountId],
                    row.Direction,
                    row.Amount,
                    counterparty.Kind,
                    counterparty.AccountId,
                    counterparty.Name);
            })
            .ToArray();

        return new EntryQueryPage(items, total, page, pageSize);
    }

    /// <summary>构造基础查询：联表 + 账套与账户过滤 + 时间区间。</summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="targetIds">目标账户主键（必然已与可见账户求交）。</param>
    /// <param name="hasFrom">是否限制下界。</param>
    /// <param name="fromValue">下界（含）。</param>
    /// <param name="hasTo">是否限制上界。</param>
    /// <param name="toValue">上界（含）。</param>
    /// <returns>每次调用**新建**的查询对象。</returns>
    /// <remarks>
    /// 每次新建而非复用：计数与分页取数用的是两份互不干扰的查询。
    /// <para>
    /// 时间条件写在**父交易的业务发生时间**上（<see cref="Transaction.OccurredAt"/>）：
    /// 不是落库时间，也不是明细上的字段——明细刻意没有自己的时间列。
    /// </para>
    /// </remarks>
    private ISugarQueryable<TransactionEntry, Transaction> BuildBaseQuery(
        int accountSetId,
        int[] targetIds,
        bool hasFrom,
        DateTime fromValue,
        bool hasTo,
        DateTime toValue) =>
        db.Queryable<TransactionEntry>()
            .LeftJoin<Transaction>((entry, tx) => entry.TransactionId == tx.Id)
            .Where((entry, tx) => tx.AccountSetId == accountSetId && targetIds.Contains(entry.AccountId))
            .WhereIF(hasFrom, (entry, tx) => tx.OccurredAt >= fromValue)
            .WhereIF(hasTo, (entry, tx) => tx.OccurredAt <= toValue);

    /// <summary>
    /// 解析本页每条明细的对手方。
    /// </summary>
    /// <param name="rows">本页明细。</param>
    /// <param name="nameById">可见账户主键到名称的映射。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>明细主键到对手方描述的映射。</returns>
    /// <remarks>
    /// 对手方定义为**同一交易中方向相反的第一条明细**（按明细主键升序）。
    /// 当前每笔交易恰有借贷两条明细，故对手方唯一；若将来出现多明细交易，本处以「取首条」给出确定答案，
    /// 而不是造出一个多对多的对手方列表——那需要的是另一种界面，不是本方法悄悄扩大返回值。
    /// <para>
    /// 只查本页交易涉及的明细（行数受页大小约束），且对不可见对手方**只取主键与类型、不取名称**：
    /// 判断「它是账本账户还是他人的个人账户」需要类型，而呈现它们则必须什么也不给。
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, Counterparty>> ResolveCounterpartiesAsync(
        IReadOnlyList<EntryRow> rows,
        IReadOnlyDictionary<int, string> nameById,
        CancellationToken cancellationToken)
    {
        var transactionIds = rows.Select(row => row.TransactionId).Distinct().ToArray();
        if (transactionIds.Length == 0)
        {
            return new Dictionary<int, Counterparty>();
        }

        var siblings = await db.Queryable<TransactionEntry>()
            .Where(entry => transactionIds.Contains(entry.TransactionId))
            .Select(entry => new SiblingEntry
            {
                EntryId = entry.Id,
                TransactionId = entry.TransactionId,
                AccountId = entry.AccountId,
                Direction = entry.Direction,
            })
            .ToListAsync(cancellationToken);

        var byTransaction = siblings
            .GroupBy(entry => entry.TransactionId)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.EntryId).ToArray());

        // 先在内存里定出每行明细的对手方账户主键，再统一去查「不可见对手方」的类型——
        // 避免在循环里逐行查库（N+1）。
        var counterpartyAccountByEntry = new Dictionary<int, int>();
        var unresolvedAccountIds = new HashSet<int>();

        foreach (var row in rows)
        {
            if (!byTransaction.TryGetValue(row.TransactionId, out var group))
            {
                continue;
            }

            var counterparty = group.FirstOrDefault(candidate => candidate.Direction != row.Direction);
            if (counterparty is null)
            {
                continue;
            }

            counterpartyAccountByEntry[row.EntryId] = counterparty.AccountId;

            if (!nameById.ContainsKey(counterparty.AccountId))
            {
                unresolvedAccountIds.Add(counterparty.AccountId);
            }
        }

        // 仅取主键与类型：名称对不可见对手方一律不外泄。
        // 无不可见对手方时不查库（本页可能全部对手方都可见，那是常态）。
        var hiddenTypes = new Dictionary<int, AccountType>();
        if (unresolvedAccountIds.Count > 0)
        {
            var hiddenAccounts = await db.Queryable<Account>()
                .Where(account => unresolvedAccountIds.Contains(account.Id))
                .Select(account => new HiddenAccount { Id = account.Id, Type = account.Type })
                .ToListAsync(cancellationToken);

            hiddenTypes = hiddenAccounts.ToDictionary(row => row.Id, row => row.Type);
        }

        var result = new Dictionary<int, Counterparty>();
        foreach (var row in rows)
        {
            if (!counterpartyAccountByEntry.TryGetValue(row.EntryId, out var counterpartyAccountId))
            {
                result[row.EntryId] = new Counterparty(CounterpartyKind.None, null, null);
                continue;
            }

            if (nameById.TryGetValue(counterpartyAccountId, out var name))
            {
                result[row.EntryId] = new Counterparty(CounterpartyKind.Account, counterpartyAccountId, name);
                continue;
            }

            // 不可见：只区分「系统账本账户」与「其余不可见账户」。账本账户是每账套至多一个的系统自有账户、
            // 其存在性已在 README 公开，故单独一档不构成信息披露；他人个人账户则连主键都不给。
            var kind = hiddenTypes.TryGetValue(counterpartyAccountId, out var type) && type == AccountType.Ledger
                ? CounterpartyKind.Ledger
                : CounterpartyKind.Hidden;

            result[row.EntryId] = new Counterparty(kind, null, null);
        }

        return result;
    }

    /// <summary>空页（无匹配明细，或目标账户集为空时直接给出，不必查库）。</summary>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <returns>条目为空、总数为 0 的一页。</returns>
    private static EntryQueryPage EmptyPage(int page, int pageSize) => new([], 0, page, pageSize);

    /// <summary>
    /// 分页查询的投影结果。
    /// </summary>
    /// <remarks>
    /// 用具名类型而非匿名类型：它要跨越两个方法（取数与对手方解析），匿名类型无法作为参数类型声明。
    /// **必须是可写属性的类**而非位置记录——SqlSugar 的 <c>Select</c> 靠属性赋值而非构造参数映射。
    /// </remarks>
    private sealed class EntryRow
    {
        /// <summary>明细主键。</summary>
        public int EntryId { get; set; }

        /// <summary>所属交易主键。</summary>
        public int TransactionId { get; set; }

        /// <summary>挂靠账户主键。</summary>
        public int AccountId { get; set; }

        /// <summary>借贷方向。</summary>
        public EntryDirection Direction { get; set; }

        /// <summary>金额（恒正）。</summary>
        public decimal Amount { get; set; }

        /// <summary>业务发生时间（UTC）。</summary>
        public DateTime OccurredAt { get; set; }

        /// <summary>交易摘要。</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>交易备注。</summary>
        public string? Remark { get; set; }

        /// <summary>交易类型。</summary>
        public TransactionType Type { get; set; }
    }

    /// <summary>同笔交易的兄弟明细（用于定位对手方）。</summary>
    private sealed class SiblingEntry
    {
        /// <summary>明细主键。</summary>
        public int EntryId { get; set; }

        /// <summary>所属交易主键。</summary>
        public int TransactionId { get; set; }

        /// <summary>挂靠账户主键。</summary>
        public int AccountId { get; set; }

        /// <summary>借贷方向。</summary>
        public EntryDirection Direction { get; set; }
    }

    /// <summary>不可见对手方账户的查询结果（只取主键与类型）。</summary>
    private sealed class HiddenAccount
    {
        /// <summary>账户主键。</summary>
        public int Id { get; set; }

        /// <summary>账户类型。</summary>
        public AccountType Type { get; set; }
    }

    /// <summary>对手方描述。</summary>
    /// <param name="Kind">可见性档位。</param>
    /// <param name="AccountId">账户主键；仅 <see cref="CounterpartyKind.Account"/> 时有值。</param>
    /// <param name="Name">账户名称；仅 <see cref="CounterpartyKind.Account"/> 时有值。</param>
    private sealed record Counterparty(CounterpartyKind Kind, int? AccountId, string? Name);
}
