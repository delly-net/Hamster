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
    /// 收集窗口在 SQL 侧预筛时向两侧放宽的秒数。
    /// </summary>
    /// <remarks>
    /// **为什么需要它**：Sqlite 把 <c>DateTime</c> 存成**文本**，而历史行的格式并不统一——
    /// 有 <c>2026-09-23 15:06:00.5630041</c>（SqlSugar 写入，带 7 位小数）也有
    /// <c>2026-09-23 15:06:00</c>（客户端显式传入的时刻，无小数部分）。两者按字符串比较时，
    /// 后者排在**同一个整秒的前面**，于是「恰好落在本地 0 点整」的一笔交易在与窗口边界比较时
    /// 可能被判到边界之外，凭空漏掉。
    /// <para>
    /// 故 SQL 只做**预筛**：把窗口向两侧各放宽一秒取回候选行，再由 C# 用真正的
    /// <see cref="DateTime"/> 比较做**精确**过滤（见 <see cref="CollectAsync"/>）。
    /// 多取回的行至多几笔、代价可忽略，换来的是边界判定不依赖数据库的文本格式。
    /// </para>
    /// </remarks>
    private static readonly TimeSpan WindowPrefilterMargin = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 结算的时区口径：**服务器本地时区**。
    /// </summary>
    /// <remarks>
    /// 在构造时解析一次并缓存：<see cref="TimeZoneInfo.Local"/> 每次访问都可能重新查一次系统时区库，
    /// 而本服务在一次收集里要反复用它换算（每个交易、每一天都要换）。
    /// <para>
    /// **注意它在本项目里的特殊性**：<c>Hamster.Api.csproj</c> 开着
    /// <c>InvariantGlobalization</c>，此处刻意不去改成固定偏移（如 +08:00）——
    /// 「交易日期的分组口径」应当跟随部署所在地，写死偏移会让一台部署在其它时区的实例
    /// 把用户的账分到错误的日期上。启动日志里会打印解析结果（见 <c>Program.LogStartupInfo</c>），
    /// 运维可据此确认「本地时区」在这台机器上到底是什么。
    /// </para>
    /// </remarks>
    private readonly TimeZoneInfo _timeZone = ResolveLocalTimeZone(logger);

    /// <inheritdoc />
    public async Task<SettlementCollectionResult> CollectAsync(CancellationToken cancellationToken = default)
    {
        // 窗口上界：本地「当天 0 点」，**不含**该时刻（任务描述：到当天 0 点之前(不含 0 点)）
        var endUtc = LocalDayStartToUtc(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timeZone).Date);
        var prefilterEnd = endUtc + WindowPrefilterMargin;

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
                ? LocalDayStartToUtc(watermark.Date.AddDays(1))
                : (DateTime?)null;

            // 已结算到今天（或更晚，如系统时钟被回调过）→ 窗口为空，不再查询
            if (startUtc is not null && startUtc >= endUtc)
            {
                continue;
            }

            var hasStart = startUtc is not null;
            var prefilterStart = startUtc?.Subtract(WindowPrefilterMargin) ?? DateTime.MinValue;

            var rows = await db.Queryable<Transaction>()
                .Where(transaction => transaction.AccountSetId == accountSetId)
                .Where(transaction => transaction.OccurredAt < prefilterEnd)
                .WhereIF(hasStart, transaction => transaction.OccurredAt >= prefilterStart)
                .OrderBy(transaction => transaction.Id)
                .ToListAsync(cancellationToken);

            var byDay = rows
                // 精确边界判定在这里：SQL 那一层向两侧各放宽了一秒（见 WindowPrefilterMargin）
                .Where(transaction =>
                    transaction.OccurredAt < endUtc &&
                    (startUtc is null || transaction.OccurredAt >= startUtc))
                .GroupBy(transaction => ToLocalDate(transaction.OccurredAt))
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
    /// <see cref="WindowPrefilterMargin"/> 那条注释）。真到了需要聚合的规模，再改成 GroupBy。
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

    /// <summary>
    /// 把一个 UTC 时刻换算成**本地日期**（时刻部分为 00:00:00）。
    /// </summary>
    /// <param name="utc">UTC 时刻。</param>
    /// <returns>本地日期。</returns>
    /// <remarks>
    /// 先 <c>SpecifyKind</c> 再换算：从库里读回的时间 <c>Kind</c> 是 <c>Unspecified</c>
    /// （两种数据库都不保存 Kind），而 <see cref="TimeZoneInfo.ConvertTimeFromUtc"/> 只在
    /// <c>Kind</c> 不是 <c>Local</c> 时才把它当作 UTC 处理。显式声明一次，
    /// 让「这一列存的就是 UTC」成为代码里的事实而不是隐含前提。
    /// </remarks>
    private DateTime ToLocalDate(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _timeZone).Date;

    /// <summary>
    /// 把**本地日期**的 0 点换算成 UTC 时刻。
    /// </summary>
    /// <param name="localDay">本地日期（时刻部分被忽略）。</param>
    /// <returns>该本地日 0 点对应的 UTC 时刻。</returns>
    /// <remarks>
    /// 用 <c>TimeZoneInfo.GetUtcOffset</c> 而不是 <c>ConvertTimeToUtc</c>：
    /// 后者在「夏令时向前跳」的那一天遇到不存在的本地时刻（如 2 点整跳到 3 点时的 2:30）
    /// 会抛 <c>ArgumentException</c>，一个每天都要跑的定时任务不该因为某年某一天而整体失败。
    /// <c>GetUtcOffset</c> 对不存在的时刻给的是跳变前的偏移，对重复的时刻给的是标准时间偏移，
    /// 两个取值都是确定的、不会抛异常。中国不使用夏令时，本条在本地是无差别的保险。
    /// </remarks>
    private DateTime LocalDayStartToUtc(DateTime localDay) =>
        DateTime.SpecifyKind(
            DateTime.SpecifyKind(localDay.Date, DateTimeKind.Unspecified) - _timeZone.GetUtcOffset(localDay.Date),
            DateTimeKind.Utc);

    /// <summary>
    /// 解析服务器本地时区，失败时回落 UTC 并告警。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <returns>本地时区；无法解析时为 UTC。</returns>
    /// <remarks>
    /// <see cref="TimeZoneInfo.Local"/> 在本进程里理论上不会抛异常，此处仍兜一层：
    /// 结算的日期分组一旦没有时区可用就无从谈起，宁可退化成「按 UTC 日分组」也不能让
    /// 两个定时任务的宿主构造失败——那会让**整个应用起不来**，代价远大于结算口径不精确。
    /// </remarks>
    private static TimeZoneInfo ResolveLocalTimeZone(ILogger logger)
    {
        try
        {
            return TimeZoneInfo.Local;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "无法解析服务器本地时区，结算的交易日期分组已回落为 UTC 日");
            return TimeZoneInfo.Utc;
        }
    }
}
