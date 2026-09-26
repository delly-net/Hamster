using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的结算业务实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <param name="logger">日志记录器。</param>
/// <remarks>
/// 本服务只依赖 <see cref="ISqlSugarClient"/>，不依赖任何其它业务服务——
/// 结算做的事情是「把交易与明细抄一份留档」，抄写不需要账户、分类、标签那几个服务的判断，
/// 也就不会与它们形成循环依赖。
/// </remarks>
public sealed class SettlementService(ISqlSugarClient db, ILogger<SettlementService> logger) : ISettlementService
{
    /// <summary>
    /// 结算的时区口径：**服务器本地时区**（解析与缓存的理由见 <see cref="LocalDay"/>）。
    /// </summary>
    private readonly TimeZoneInfo _timeZone = LocalDay.ResolveTimeZone(logger);

    /// <inheritdoc />
    public async Task<SettlementCollectionResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        // 窗口上界：本地「当天 0 点」，**不含**该时刻（任务描述：到当天 0 点之前(不含 0 点)）
        var endUtc = LocalDay.StartToUtc(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone).Date, _timeZone);
        var prefilterEnd = endUtc + LocalDay.PrefilterMargin;

        var watermarks = await LoadWatermarksAsync(cancellationToken);

        // 逐个账套收集，而不是「一次查出全部交易再在内存里按账套分组」：
        // 每个账套的水位各不相同，一次查询无法表达「各按各的下界」，只能在内存里补过滤、
        // 把已经结算过的账套的历史行也一并读进内存。账套数量是个位数，多几次查询的代价可以忽略。
        var accountSetIds = await db.Queryable<AccountSet>()
            .OrderBy(accountSet => accountSet.Id)
            .Select(accountSet => accountSet.Id)
            .ToListAsync(cancellationToken);

        var createdTasks = new List<SettlementTask>();
        var transactionCount = 0;
        var entryCount = 0;

        foreach (var accountSetId in accountSetIds)
        {
            // 水位为空 = 这个账套从未结算过 = **第一次执行，不限初始时间**
            var startUtc = watermarks.TryGetValue(accountSetId, out var watermark)
                ? LocalDay.StartToUtc(watermark.Date.AddDays(1), _timeZone)
                : (DateTime?)null;

            // 已结算到今天（或更晚，如系统时钟被回调过）→ 窗口为空，不再查询
            if (startUtc is not null && startUtc >= endUtc)
            {
                continue;
            }

            var hasStart = startUtc is not null;
            var prefilterStart = startUtc?.Subtract(LocalDay.PrefilterMargin) ?? DateTime.MinValue;

            var rows = await db.Queryable<Transaction>()
                .Where(transaction => transaction.AccountSetId == accountSetId)
                .Where(transaction => transaction.OccurredAt < prefilterEnd)
                .WhereIF(hasStart, transaction => transaction.OccurredAt >= prefilterStart)
                .OrderBy(transaction => transaction.Id)
                .ToListAsync(cancellationToken);

            var byDay = rows
                // 精确边界判定在这里：SQL 那一层向两侧各放宽了一秒（见 LocalDay.PrefilterMargin）
                .Where(transaction =>
                    transaction.OccurredAt < endUtc &&
                    (startUtc is null || transaction.OccurredAt >= startUtc))
                .GroupBy(transaction => LocalDay.DateOf(transaction.OccurredAt, _timeZone))
                .OrderBy(group => group.Key)
                .ToList();

            foreach (var day in byDay)
            {
                var localDay = day.Key;

                // 已结算的日子冻结、不重算（同接口注释）。当天内被触发两次时，第一次之后这里全部命中。
                if (await HasTaskAsync(accountSetId, localDay, cancellationToken))
                {
                    continue;
                }

                var dayTransactions = day.OrderBy(transaction => transaction.Id).ToList();
                var (task, storedEntries) = await CreateTaskAsync(
                    accountSetId,
                    localDay,
                    dayTransactions,
                    cancellationToken);

                createdTasks.Add(task);
                transactionCount += dayTransactions.Count;
                entryCount += storedEntries;
            }
        }

        if (createdTasks.Count > 0)
        {
            logger.LogInformation(
                "交易统计完成：新建 {TaskCount} 个结算任务，冗余存储 {TransactionCount} 笔交易、{EntryCount} 条明细",
                createdTasks.Count,
                transactionCount,
                entryCount);
        }

        return new SettlementCollectionResult(createdTasks, transactionCount, entryCount);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SettlementTask>> FindPendingExecutionsAsync(
        CancellationToken cancellationToken = default) =>
        await db.Queryable<SettlementTask>()
            .Where(task => task.ExecutedAt == null)
            .OrderBy(task => task.TransactionDate)
            .OrderBy(task => task.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<SettlementSnapshotCounts?> CountSnapshotsAsync(
        int settlementTaskId,
        CancellationToken cancellationToken = default)
    {
        var exists = await db.Queryable<SettlementTask>()
            .Where(task => task.Id == settlementTaskId)
            .AnyAsync(cancellationToken);
        if (!exists)
        {
            return null;
        }

        var transactionCount = await db.Queryable<SettlementTransaction>()
            .Where(snapshot => snapshot.SettlementTaskId == settlementTaskId)
            .CountAsync(cancellationToken);
        var entryCount = await db.Queryable<SettlementEntry>()
            .LeftJoin<SettlementTransaction>((entry, snapshot) => entry.SettlementTransactionId == snapshot.Id)
            .Where((entry, snapshot) => snapshot.SettlementTaskId == settlementTaskId)
            .CountAsync(cancellationToken);

        return new SettlementSnapshotCounts(transactionCount, entryCount);
    }

    /// <inheritdoc />
    public async Task<bool> MarkExecutedAsync(
        SettlementTask task,
        DateTime executedAt,
        CancellationToken cancellationToken = default)
    {
        // `executed_at IS NULL` 是**条件更新**而不是多余判断：两个实例同时结算同一个任务时，
        // 数据库保证只有一个能改到这行，返回值因此如实区分「我标记的」与「别人已经标记过了」。
        var affected = await db.Updateable<SettlementTask>()
            .SetColumns(existing => existing.ExecutedAt == executedAt)
            .Where(existing => existing.Id == task.Id && existing.ExecutedAt == null)
            .ExecuteCommandAsync(cancellationToken);

        if (affected > 0)
        {
            task.ExecutedAt = executedAt;
        }

        return affected > 0;
    }

    /// <summary>
    /// 取回各账套的水位：已有结算任务里最大的交易日期。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>账套主键 → 该账套已收集的最后一天。</returns>
    /// <remarks>
    /// 整表取回后在内存里取最大值，而不是写一条 <c>GROUP BY + MAX</c>：
    /// 结算任务一天至多一条、一个账套一年至多 365 条，十年也不过几千行，
    /// 换取的是「水位怎么算」这件事只有一处、且用的是 C# 的日期比较（不受文本格式影响，同
    /// <see cref="LocalDay.PrefilterMargin"/> 那条注释）。真到了需要聚合的规模，再改成 GroupBy。
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, DateTime>> LoadWatermarksAsync(CancellationToken cancellationToken)
    {
        var tasks = await db.Queryable<SettlementTask>()
            .Select(task => new { task.AccountSetId, task.TransactionDate })
            .ToListAsync(cancellationToken);

        var watermarks = new Dictionary<int, DateTime>();
        foreach (var task in tasks)
        {
            if (!watermarks.TryGetValue(task.AccountSetId, out var current) || task.TransactionDate > current)
            {
                watermarks[task.AccountSetId] = task.TransactionDate;
            }
        }

        return watermarks;
    }

    /// <summary>
    /// 判断某账套的某一天是否已经建立过结算任务。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="localDay">交易日期（本地日期）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已建立返回 <c>true</c>。</returns>
    private Task<bool> HasTaskAsync(int accountSetId, DateTime localDay, CancellationToken cancellationToken) =>
        db.Queryable<SettlementTask>()
            .Where(task => task.AccountSetId == accountSetId && task.TransactionDate == localDay)
            .AnyAsync(cancellationToken);

    /// <summary>
    /// 为「某账套的某一天」建立结算任务，并冗余存储该日的全部交易与明细。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="localDay">交易日期（本地日期）。</param>
    /// <param name="transactions">该日的全部交易。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已落库的结算任务（带主键），以及冗余存储的明细条数。</returns>
    /// <remarks>
    /// **整天的三张表同处一个事务**：中途失败会留下「有结算任务、没有快照交易」的空壳，
    /// 而水位是按结算任务推出来的，那个空壳会让这一天**被永久跳过**——比失败本身更难发现。
    /// 同事务写入则「要么整天都在、要么整天都不在」，重新触发时会重来一遍。
    /// <para>
    /// 明细条数**由本方法一并返回**而不是让调用方再查一次：明细本来就在下面按交易主键
    /// 一次性取回，把它的数量顺手交出去即可，省掉一次纯为日志服务的查询。
    /// </para>
    /// </remarks>
    private async Task<(SettlementTask Task, int EntryCount)> CreateTaskAsync(
        int accountSetId,
        DateTime localDay,
        IReadOnlyList<Transaction> transactions,
        CancellationToken cancellationToken)
    {
        var task = new SettlementTask
        {
            AccountSetId = accountSetId,
            // 本地日期，时刻部分恒为 00:00:00（见 SettlementTask.TransactionDate）
            TransactionDate = localDay,
            CreatedAt = DateTime.UtcNow,
        };

        var transactionIds = transactions.Select(transaction => transaction.Id).ToList();
        var entries = await db.Queryable<TransactionEntry>()
            .Where(entry => transactionIds.Contains(entry.TransactionId))
            .OrderBy(entry => entry.Id)
            .ToListAsync(cancellationToken);

        var accountNames = await LoadAccountNamesAsync(entries, cancellationToken);
        var categoryNames = await LoadCategoryNamesAsync(transactions, cancellationToken);

        await db.Ado.UseTranAsync(async () =>
        {
            task.Id = await db.Insertable(task).ExecuteReturnIdentityAsync(cancellationToken);

            // 快照交易**逐笔插入并取回自增主键**，而不是批量插一次：
            // 下面每条快照明细都要挂到它所属的那笔快照交易上，而批量插入拿不回一整串主键
            // （SqlSugar 只回一个标识值）。一天至多数百笔，逐笔插入的代价可以接受。
            var snapshotIds = new Dictionary<int, int>(transactions.Count);
            foreach (var transaction in transactions)
            {
                var snapshot = new SettlementTransaction
                {
                    SettlementTaskId = task.Id,
                    SourceTransactionId = transaction.Id,
                    AccountSetId = transaction.AccountSetId,
                    Type = transaction.Type,
                    OccurredAt = transaction.OccurredAt,
                    Summary = transaction.Summary,
                    Remark = transaction.Remark,
                    CategoryId = transaction.CategoryId,
                    CategoryName = transaction.CategoryId is { } categoryId
                        ? categoryNames.GetValueOrDefault(categoryId)
                        : null,
                    CreatedByUserId = transaction.CreatedByUserId,
                    CreatedAt = transaction.CreatedAt,
                    UpdatedAt = transaction.UpdatedAt,
                };

                snapshot.Id = await db.Insertable(snapshot).ExecuteReturnIdentityAsync(cancellationToken);
                snapshotIds[transaction.Id] = snapshot.Id;
            }

            var snapshots = entries
                .Where(entry => snapshotIds.ContainsKey(entry.TransactionId))
                .Select(entry => new SettlementEntry
                {
                    SettlementTransactionId = snapshotIds[entry.TransactionId],
                    SourceEntryId = entry.Id,
                    AccountId = entry.AccountId,
                    AccountName = accountNames.GetValueOrDefault(entry.AccountId, string.Empty),
                    Direction = entry.Direction,
                    Amount = entry.Amount,
                })
                .ToList();

            if (snapshots.Count > 0)
            {
                await db.Insertable(snapshots).ExecuteCommandAsync(cancellationToken);
            }
        });

        return (task, entries.Count);
    }

    /// <summary>
    /// 取回明细涉及账户的名称，供快照冗余。
    /// </summary>
    /// <param name="entries">该日的全部明细。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>账户主键 → 名称。</returns>
    /// <remarks>
    /// 取不到名字时用空串占位并记一条告警，**不跳过这条明细**：
    /// 明细是配平的两条，丢一条会让快照的「借方合计 == 贷方合计」当场失效——
    /// 那比缺一个账户名严重得多。理论上取不到是不可能的（账户只做软删除、
    /// 外键因此不会悬空），记告警是为了万一真的发生时有迹可循。
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, string>> LoadAccountNamesAsync(
        IReadOnlyCollection<TransactionEntry> entries,
        CancellationToken cancellationToken)
    {
        var ids = entries.Select(entry => entry.AccountId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var accounts = await db.Queryable<Account>()
            .Where(account => ids.Contains(account.Id))
            .Select(account => new { account.Id, account.Name })
            .ToListAsync(cancellationToken);

        var names = accounts.ToDictionary(account => account.Id, account => account.Name);

        var missing = ids.Count - names.Count;
        if (missing > 0)
        {
            logger.LogWarning(
                "结算快照有 {Missing} 个账户取不到名称（账户作软删除、外键不应悬空），其快照明细的账户名记为空串",
                missing);
        }

        return names;
    }

    /// <summary>
    /// 取回交易涉及分类的名称，供快照冗余。
    /// </summary>
    /// <param name="transactions">该日的全部交易。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分类主键 → 名称；未分类的交易不参与。</returns>
    /// <remarks>
    /// 冗余的是**快照时点**的名字：分类日后改名或停用都不改写历史结算
    /// （见 <see cref="SettlementTransaction.CategoryName"/>）。
    /// 命中的分类**不筛启用状态**——停用的分类仍是历史上那些账的分类。
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, string>> LoadCategoryNamesAsync(
        IReadOnlyCollection<Transaction> transactions,
        CancellationToken cancellationToken)
    {
        var ids = transactions
            .Where(transaction => transaction.CategoryId is not null)
            .Select(transaction => transaction.CategoryId!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        var categories = await db.Queryable<Category>()
            .Where(category => ids.Contains(category.Id))
            .Select(category => new { category.Id, category.Name })
            .ToListAsync(cancellationToken);

        return categories.ToDictionary(category => category.Id, category => category.Name);
    }
}
