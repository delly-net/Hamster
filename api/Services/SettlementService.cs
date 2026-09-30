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
        // 本地「今天」：既是窗口的上界（今天 0 点，**不含**该时刻——任务描述：到当天 0 点之前(不含 0 点)），
        // 也是复查补收的日期上界（当天交给下一轮窗口收集）
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone).Date;
        var endUtc = LocalDay.StartToUtc(localToday, _timeZone);

        var watermarks = await LoadWatermarksAsync(cancellationToken);

        // 逐个账套收集，而不是「一次查出全部交易再在内存里按账套分组」：
        // 每个账套的水位各不相同，一次查询无法表达「各按各的下界」，只能在内存里补过滤、
        // 把已经结算过的账套的历史行也一并读进内存。账套数量是个位数，多几次查询的代价可以忽略。
        var accountSetIds = await db.Queryable<AccountSet>()
            .OrderBy(accountSet => accountSet.Id)
            .Select(accountSet => accountSet.Id)
            .ToListAsync(cancellationToken);

        var createdTasks = new List<SettlementTask>();
        var recheckTasks = new List<SettlementTask>();
        var transactionCount = 0;
        var entryCount = 0;

        foreach (var accountSetId in accountSetIds)
        {
            // 水位为空 = 这个账套从未结算过 = **第一次执行，不限初始时间**
            var startUtc = watermarks.TryGetValue(accountSetId, out var watermark)
                ? LocalDay.StartToUtc(watermark.Date.AddDays(1), _timeZone)
                : (DateTime?)null;

            // —— 第一趟：窗口收集 <c>[水位 + 1 天, 今天 0 点)</c> ——
            var window = await CollectWindowAsync(accountSetId, startUtc, endUtc, cancellationToken);
            createdTasks.AddRange(window.Tasks);
            transactionCount += window.TransactionCount;
            entryCount += window.EntryCount;

            // —— 第二趟：复查补收 ——
            // 紧接本账套的窗口趟执行（而不是等全部账套的窗口趟都跑完再统一复查）：
            // 窗口趟刚落库的快照在这一趟里会被如实认作「已留档」，两趟因此天然不会重复收集同一笔交易。
            var recheck = await RecheckAsync(accountSetId, localToday, cancellationToken);
            recheckTasks.AddRange(recheck.Tasks);
            transactionCount += recheck.TransactionCount;
            entryCount += recheck.EntryCount;
        }

        if (createdTasks.Count > 0 || recheckTasks.Count > 0)
        {
            logger.LogInformation(
                "交易统计完成：新建 {TaskCount} 个结算任务（其中复查补收 {RecheckCount} 个），" +
                "冗余存储 {TransactionCount} 笔交易、{EntryCount} 条明细",
                createdTasks.Count + recheckTasks.Count,
                recheckTasks.Count,
                transactionCount,
                entryCount);
        }

        return new SettlementCollectionResult(createdTasks, recheckTasks, transactionCount, entryCount);
    }

    /// <summary>
    /// 第一趟（窗口收集）：把窗口内每个本地日的**全部**交易建成结算任务。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="startUtc">窗口下界（UTC）；<c>null</c> 表示不限下界（该账套第一次执行）。</param>
    /// <param name="endUtc">窗口上界（UTC，本地今天 0 点），**不含**该时刻。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次新建的任务，及其冗余存储的交易与明细条数。</returns>
    /// <remarks>
    /// 这是「往前推进」的一趟：它只认窗口与水位，**不回答「已过去那些天的账是否都留了档」**——
    /// 那正是第二趟（<see cref="RecheckAsync"/>）的职责，两趟合起来才构成完整的收集口径
    /// （见 <see cref="ISettlementService.CollectAsync"/>）。
    /// </remarks>
    private async Task<(List<SettlementTask> Tasks, int TransactionCount, int EntryCount)> CollectWindowAsync(
        int accountSetId,
        DateTime? startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken)
    {
        var tasks = new List<SettlementTask>();
        var transactionCount = 0;
        var entryCount = 0;

        // 已结算到今天（或更晚，如系统时钟被回调过）→ 窗口为空，不再查询
        if (startUtc is not null && startUtc >= endUtc)
        {
            return (tasks, transactionCount, entryCount);
        }

        var hasStart = startUtc is not null;
        var prefilterStart = startUtc?.Subtract(LocalDay.PrefilterMargin) ?? DateTime.MinValue;
        var prefilterEnd = endUtc + LocalDay.PrefilterMargin;

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

            // 窗口内已有结算任务的日子整日跳过：窗口会因为「水位停在原地」而被反复扫到
            // （如水位之后的那些天本就没有账、或收集任务在同一天内被触发两次），跳过是这一趟的幂等所在。
            // 这一天若还留着**未如实留档**的交易（事后补记、被改账），由第二趟补收——本趟不做这个判断。
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

            tasks.Add(task);
            transactionCount += dayTransactions.Count;
            entryCount += storedEntries;
        }

        return (tasks, transactionCount, entryCount);
    }

    /// <summary>
    /// 第二趟（复查补收）：把「尚未如实留档」的交易按本地日分组，各补建一条同日的**增量**结算任务。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="localToday">本地今天；只补收早于它的日子（当天由下一轮窗口收集负责）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次补建的任务，及其冗余存储的交易与明细条数。</returns>
    /// <remarks>
    /// **判据是「快照与交易是否一致」，而不是「这一天有没有结算任务」**：后者只回答「这一天曾被收集过」，
    /// 回答不了「这一天现在的账是不是都留了档」。故本趟把该账套的**全部**交易与**全部**快照都读回来，
    /// 逐笔比对 <c>(source_transaction_id, updated_at)</c>，落在下面两类的即「尚未如实留档」：
    /// <list type="bullet">
    /// <item>一笔快照都没有——事后补记到已结算日期的那笔账（本次修复的主场景），
    /// 以及「某天当时无账、因此没建任务，事后才补记」这条同性质的路径；</item>
    /// <item>有快照但快照的 <c>updated_at</c> 与交易当前的 <c>updated_at</c> 都对不上——留档之后被改过账
    /// （改账会把这一列更新为改账时刻，见 <c>TransactionService</c>）。</item>
    /// </list>
    /// 同一天的多笔交易合成**一条**增量任务；原任务与既有快照行一律不动（见 <c>SettlementTask</c> 类头）。
    /// <para>
    /// **幂等**：补收任务落库后这些交易就有了 <c>updated_at</c> 相符的快照，再跑一次本趟一笔都找不出来。
    /// 同一笔交易被连改两次也不会漏——每次都对应一个不同的 <c>updated_at</c>，各自补收一条。
    /// </para>
    /// <para>
    /// **两边都从库里读回来再比较**，不拿内存里刚写好的实体值去比：<c>updated_at</c> 经数据库往返后精度
    /// 可能被截断（PostgreSQL 的时间只存到微秒），拿内存值去比会把刚留过档的交易又判成「未留档」，
    /// 于是每次收集都重复补收一遍。
    /// </para>
    /// <para>
    /// **代价**：每个账套每次收集各读一遍交易表与快照表。家庭账本是数年、万级行的规模，读的又是同账套的
    /// 窄查询，可以忽略；真到了需要收窄的规模，可按「该账套最早结算任务的创建时间」或按日比对来筛。
    /// </para>
    /// </remarks>
    private async Task<(List<SettlementTask> Tasks, int TransactionCount, int EntryCount)> RecheckAsync(
        int accountSetId,
        DateTime localToday,
        CancellationToken cancellationToken)
    {
        var snapshots = await db.Queryable<SettlementTransaction>()
            .Where(snapshot => snapshot.AccountSetId == accountSetId)
            .Select(snapshot => new { snapshot.SourceTransactionId, snapshot.UpdatedAt })
            .ToListAsync(cancellationToken);

        // 键是「源交易主键 + 留档时的修改时间」：一笔交易每被留档一次就留下一个键，
        // 交易当前的修改时间与其中任意一个相等即视为已如实留档（改账会带来新的时间 → 对不上 → 补收）。
        var covered = new HashSet<(int SourceTransactionId, DateTime UpdatedAt)>();
        foreach (var snapshot in snapshots)
        {
            covered.Add((snapshot.SourceTransactionId, snapshot.UpdatedAt));
        }

        var transactions = await db.Queryable<Transaction>()
            .Where(transaction => transaction.AccountSetId == accountSetId)
            .OrderBy(transaction => transaction.Id)
            .ToListAsync(cancellationToken);

        var byDay = transactions
            .Where(transaction => !covered.Contains((transaction.Id, transaction.UpdatedAt)))
            .GroupBy(transaction => LocalDay.DateOf(transaction.OccurredAt, _timeZone))
            // 当天不补收：这一天才过了一半，它属于下一轮窗口收集的范围（现在建任务，之后改的账还要再补）
            .Where(group => group.Key < localToday)
            .OrderBy(group => group.Key)
            .ToList();

        var tasks = new List<SettlementTask>();
        var transactionCount = 0;
        var entryCount = 0;

        foreach (var day in byDay)
        {
            var dayTransactions = day.OrderBy(transaction => transaction.Id).ToList();
            var (task, storedEntries) = await CreateTaskAsync(
                accountSetId,
                day.Key,
                dayTransactions,
                cancellationToken);

            tasks.Add(task);
            transactionCount += dayTransactions.Count;
            entryCount += storedEntries;

            logger.LogInformation(
                "复查补收：账套 {AccountSetId} 的 {Date} 有 {Count} 笔交易未如实留档，已补建结算任务 {TaskId}" +
                "（原任务与既有快照不动）",
                accountSetId,
                day.Key.ToString("yyyy-MM-dd"),
                dayTransactions.Count,
                task.Id);
        }

        return (tasks, transactionCount, entryCount);
    }

    /// <inheritdoc />
    public async Task<DateTime?> FindEarliestUnexecutedDateAsync(
        int accountSetId,
        CancellationToken cancellationToken = default)
    {
        var pendingDays = await db.Queryable<SettlementTask>()
            .Where(task => task.AccountSetId == accountSetId && task.ExecutedAt == null)
            .Select(task => task.TransactionDate)
            .ToListAsync(cancellationToken);

        // 取最小值放在 C# 侧（同 LoadWatermarksAsync 的取舍）：一天的比较不差这一次查询的代价，
        // 换来的是不依赖两种库对日期文本/时间的排序行为。
        return pendingDays.Count == 0 ? null : pendingDays.Min();
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
    /// 一个账套一年至多 365 天、每天再算上复查补收的增量任务也不过个位数，十年仍是万行以内，
    /// 换取的是「水位怎么算」这件事只有一处、且用的是 C# 的日期比较（不受文本格式影响，同
    /// <see cref="LocalDay.PrefilterMargin"/> 那条注释）。真到了需要聚合的规模，再改成 GroupBy。
    /// <para>
    /// **补收的增量任务不会让水位倒退**：同一天存在多条任务时取最大值，结果仍是那一天——
    /// 水位表达的是「往前收集到哪天」，而「哪天的账还没留全」由复查趟逐笔比对，
    /// 两者是两件事（这也是补收不需要动水位的原因）。
    /// </para>
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
    /// 判断某账套的某一天是否已经建立过结算任务（**任意一条**即算）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="localDay">交易日期（本地日期）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已建立返回 <c>true</c>。</returns>
    /// <remarks>
    /// **「这一天有任务」不等于「这一天的账已全部留档」**：事后补记/改动的交易由复查趟补收成**同日增量任务**，
    /// 而本方法只回答「窗口趟要不要再为这天建任务」。两者不可互相替代——若把本方法当作留档判据，
    /// 就会重新回到「已结算日期冻结、补记的账永远收不到」那个缺陷。
    /// </remarks>
    private Task<bool> HasTaskAsync(int accountSetId, DateTime localDay, CancellationToken cancellationToken) =>
        db.Queryable<SettlementTask>()
            .Where(task => task.AccountSetId == accountSetId && task.TransactionDate == localDay)
            .AnyAsync(cancellationToken);

    /// <summary>
    /// 为「某账套的某一天」建立结算任务，并冗余存储<paramref name="transactions"/>及其明细。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="localDay">交易日期（本地日期）。</param>
    /// <param name="transactions">本次要留档的那批交易：窗口趟传该日的全部交易，复查趟传该日**尚未留档**的子集。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已落库的结算任务（带主键），以及冗余存储的明细条数。</returns>
    /// <remarks>
    /// **一个任务里的三张表同处一个事务**：中途失败会留下「有结算任务、没有快照交易」的空壳，
    /// 而水位是按结算任务推出来的，那个空壳会让这一天**被永久跳过**——比失败本身更难发现。
    /// 同事务写入则「要么整天都在、要么整天都不在」，重新触发时会重来一遍。
    /// <para>
    /// **任务内容可以是该日的子集**：复查补收建出的增量任务只装「还没留档的那几笔」，
    /// 与同日已有任务并存（<c>(account_set_id, transaction_date)</c> 上刻意没有唯一索引）。
    /// 故读侧若要某日的完整视图，必须把该日**全部任务**的快照合起来看，
    /// 不要再假设「一天一条任务 = 一天的全部账」。
    /// </para>
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
    /// <param name="entries">本次要留档的那批明细。</param>
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
    /// <param name="transactions">本次要留档的那批交易。</param>
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
