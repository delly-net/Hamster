using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 按天流量结算的共享骨架：收入与支出两套订阅的算法逐字相同，差异收敛成本类的几个抽象成员。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <param name="transactions">交易业务服务：当日发生额的**唯一**来源（<c>SumPrimaryAmountsAsync</c>）。</param>
/// <param name="accountSets">账套服务：取账套成员列表（记录按用户分行，成员口径即此）。</param>
/// <param name="executions">订阅执行水位。</param>
/// <param name="settlements">结算服务：取「最早的未执行结算任务日期」，用于给下界让出被补收的那一天。</param>
/// <param name="logger">日志记录器（由子类传入自己的类型化实例，故此处是非泛型的 <see cref="ILogger"/>）。</param>
/// <remarks>
/// **为什么抽基类、而此前三个选择框却刻意没抽**（见 <c>ui/README.md</c> 的设计约定）：
/// 那条结论针对的是「契约不同、硬抽会把分支塞进公共实现」的情形（单值 vs 多值的三个选择框）；
/// 这里的两个服务**契约逐字相同**——同一个方法签名、同一套算法、同一套水位语义，
/// 唯一的差别是「落在哪张表、哪个订阅码、哪个交易类型」，正好是抽象成员能干净表达的形状。
/// 若不抽，两份 90% 相同的逐日重算代码会各自漂移，而它们共有的那部分（覆盖写的半开区间、
/// 事务失败必须自throw、先落库后推水位）恰恰是最容易改错的地方。
/// <para>
/// **表相关查询一律留给子类**，基类不碰实体类型：SqlSugar 对「泛型实体 + 接口成员表达式」的
/// 列名翻译并不可靠（见 <c>AccountTypeExtensions.IsMoneyAccount</c> 注释里的同类问题），
/// 而收益只是省下子类里三个方法体；相比之下，让每张表的查询写在能看见那张表的类型旁边更稳。
/// </para>
/// </remarks>
public abstract class DailyFlowSettlementServiceBase(
    ISqlSugarClient db,
    ITransactionService transactions,
    IAccountSetService accountSets,
    ISettlementSubscriptionExecutionService executions,
    ISettlementService settlements,
    ILogger logger) : IDailyFlowSettlementService
{
    /// <summary>
    /// 日界换算用的本地时区，构造时解析一次并缓存（口径见 <see cref="LocalDay"/>）。
    /// </summary>
    private readonly TimeZoneInfo _timeZone = LocalDay.ResolveTimeZone(logger);

    /// <summary>
    /// SqlSugar 客户端，供**子类**写自己那几张表的查询（基类不碰实体类型，见类头注释）。
    /// </summary>
    /// <remarks>
    /// 转发基类捕获的那个实例而不是让子类再捕获一次自己的构造参数：
    /// 同一个对象被基类与子类各存一份会触发警告 CS9107（「参数已被基类捕获」），
    /// 而两份引用还给了「它们不是同一个客户端」的错觉。
    /// </remarks>
    protected ISqlSugarClient Db => db;

    /// <summary>本订阅的水位标识（即 <c>SettlementSubscriptionExecution.subscription_code</c>）。</summary>
    protected abstract string SubscriptionCode { get; }

    /// <summary>日志与报错里用来称呼这套流量的词（<c>收入</c> / <c>支出</c>）。</summary>
    protected abstract string FlowLabel { get; }

    /// <summary>要汇总的交易类型（<see cref="TransactionType.Income"/> / <see cref="TransactionType.Expense"/>）。</summary>
    protected abstract TransactionType FlowType { get; }

    /// <summary>
    /// 取「每个成员已有记录的最后一天」（用于算起始下界，见 <see cref="ResolveFloorAsync"/>）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成员主键 → 该成员已有记录的最后一天；一行都没有的成员不在其中。</returns>
    protected abstract Task<IReadOnlyDictionary<int, DateTime>> LoadLastRecordedDayByUserAsync(
        int accountSetId,
        CancellationToken cancellationToken);

    /// <summary>删除某一天的全部记录（半开区间 <c>[dayStart, dayEnd)</c>，理由见 <see cref="SettleDayAsync"/>）。</summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="dayStart">当日 0 点。</param>
    /// <param name="dayEnd">次日 0 点。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    protected abstract Task DeleteDayAsync(
        int accountSetId,
        DateTime dayStart,
        DateTime dayEnd,
        CancellationToken cancellationToken);

    /// <summary>插入某一天的全新记录。</summary>
    /// <param name="accountSetId">账套主键（记录行必须带上它，而 <see cref="DailyFlowRow"/> 刻意不含它）。</param>
    /// <param name="localDay">目标日期（本地日期）。</param>
    /// <param name="rows">该日的记录（每个成员、每个币种各一条）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    protected abstract Task InsertDayAsync(
        int accountSetId,
        DateTime localDay,
        IReadOnlyList<DailyFlowRow> rows,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public abstract Task<IReadOnlyList<DailyFlowPoint>> ListDailyAsync(
        int accountSetId,
        int userId,
        string currencyCode,
        DateTime monthStart,
        CancellationToken cancellationToken = default);

    /// <inheritdoc />
    public async Task<DailyFlowSettlementRunResult> RunAsync(
        int accountSetId,
        CancellationToken cancellationToken = default)
    {
        var settledDays = await db.Queryable<SettlementTask>()
            .Where(task => task.AccountSetId == accountSetId)
            .Select(task => task.TransactionDate)
            .ToListAsync(cancellationToken);

        if (settledDays.Count == 0)
        {
            // 这个账套一天都没结算过 → 没有任何可算的日期。
            // 注意此处**不推进水位**：没有日期可处理与水位的取值无关，
            // 而推进一个凭空造出来的日期会在将来结算任务补上历史日期时把它们跳过。
            logger.LogInformation(
                "{FlowLabel}结算：账套 {AccountSetId} 尚无任何结算任务，跳过",
                FlowLabel,
                accountSetId);
            return new DailyFlowSettlementRunResult(accountSetId, 0, 0, null);
        }

        // 排序放在 C# 侧：Sqlite 把日期存成文本，「按文本排」与「按日期排」是否等价依赖存储格式，
        // 而这一处的顺序直接决定水位推进的顺序，不把它交给数据库（同 SettlementService.LoadWatermarksAsync）
        // **去重**：同一天现在可以有多条结算任务（复查补收会为补记到已结算日期的交易再建一条增量任务），
        // 不去重会让这一天被重算两遍——幂等但白做，日志里的「重算 N 天」也会虚高。
        var orderedDays = settledDays.Distinct().OrderBy(day => day).ToList();

        var moneyAccounts = await LoadMoneyAccountsAsync(accountSetId, cancellationToken);
        var memberIds = await LoadMemberIdsAsync(accountSetId, cancellationToken);

        var floor = await ResolveFloorAsync(
            accountSetId,
            moneyAccounts,
            memberIds,
            cancellationToken);
        var pendingDays = floor is { } start
            ? orderedDays.Where(day => day > start).ToList()
            : orderedDays;

        if (pendingDays.Count == 0)
        {
            logger.LogInformation(
                "{FlowLabel}结算：账套 {AccountSetId} 没有待处理的日期（水位已在 {Date}）",
                FlowLabel,
                accountSetId,
                floor?.ToString("yyyy-MM-dd") ?? "（无）");
            return new DailyFlowSettlementRunResult(accountSetId, 0, 0, null);
        }

        var recordCount = 0;
        DateTime? advancedTo = null;

        // 逐日从早到晚：一天的顺序就是任务描述里的「从最早日期到最晚日期」。
        // 当日失败会直接抛出 → 事件派发计为失败 → 结算任务不标记已执行 → 下一个执行日重投，
        // 届时水位只走到失败日的前一天，这一天会被重新算一遍（覆盖写保证幂等）。
        foreach (var day in pendingDays)
        {
            cancellationToken.ThrowIfCancellationRequested();

            recordCount += await SettleDayAsync(
                accountSetId,
                day,
                moneyAccounts,
                memberIds,
                cancellationToken);

            // **先落库、后推进水位**：水位是「已完成」而不是「已开始」
            // （见 SettlementSubscriptionExecution.LastExecutedDate）
            await executions.AdvanceAsync(SubscriptionCode, accountSetId, day, cancellationToken);

            advancedTo = day;
        }

        logger.LogInformation(
            "{FlowLabel}结算完成：账套 {AccountSetId} 重算 {DayCount} 天（{From} ~ {To}），写入 {RecordCount} 条记录" +
            "（{MemberCount} 个成员 × 币种），水位推进到 {Watermark}",
            FlowLabel,
            accountSetId,
            pendingDays.Count,
            pendingDays[0].ToString("yyyy-MM-dd"),
            pendingDays[^1].ToString("yyyy-MM-dd"),
            recordCount,
            memberIds.Count,
            advancedTo?.ToString("yyyy-MM-dd") ?? "（无）");

        return new DailyFlowSettlementRunResult(accountSetId, pendingDays.Count, recordCount, advancedTo);
    }

    /// <summary>
    /// 取该账套的**钱账户**（资金与负债），含已停用账户。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>钱账户列表，按主键升序。</returns>
    /// <remarks>
    /// **与总资产结算取的是同一批账户**（资金 + 负债，判据见 <c>AccountTypeExtensions.IsMoneyAccount</c>）：
    /// 任务要求「资产账户与负债账户的收入/支出总金额」，且两者同取一批账户，首页的两张图才可能互相对照
    /// （「这个月收了 5000」与「月末总资产涨了 5000」若建在不同的账户集合上，对不上时无从判断是谁错）。
    /// <para>
    /// **不过滤 <see cref="Account.IsActive"/>**：停用是「不再出现在候选列表里」，不是「这笔收支不曾发生」。
    /// 排除它会让历史曲线在停用账户的那一天**凭空掉一个台阶**（同 <c>TotalAssetSettlementService</c>）。
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Account>> LoadMoneyAccountsAsync(
        int accountSetId,
        CancellationToken cancellationToken) =>
        await db.Queryable<Account>()
            .Where(account => account.AccountSetId == accountSetId)
            .Where(account => account.Type == AccountType.Fund || account.Type == AccountType.Liability)
            .OrderBy(account => account.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// 取该账套的成员主键，按升序（记录的落库顺序随之确定）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成员主键列表。</returns>
    private async Task<IReadOnlyList<int>> LoadMemberIdsAsync(
        int accountSetId,
        CancellationToken cancellationToken)
    {
        var memberIds = await accountSets.ListMemberIdsAsync(accountSetId, cancellationToken);
        return [.. memberIds.OrderBy(id => id)];
    }

    /// <summary>
    /// 算出本次执行的**起始下界**：水位、「每个成员已有记录的最后一天」、
    /// 「最早的未执行结算任务日期减一天」三者中的**最早者**。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="moneyAccounts">该账套的钱账户（为空时直接按水位与未执行任务走，见 remarks）。</param>
    /// <param name="memberIds">该账套的成员主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>下界日期；<c>null</c> 表示**无下界**（全部结算日期都要处理）。</returns>
    /// <remarks>
    /// **为什么不能只看水位**：水位是按「订阅 + 账套」记的，而记录是按**用户**分行的。
    /// 一个新加入账套的成员在库里一行记录都没有，若只按水位推进，历史日期永远不会为它重算，
    /// 它的首页曲线会从加入之后才起头——而它的人个账户明明一直存在。
    /// 故下界取「水位」与「每个成员已有记录的最后一天」之中的**最早者**：
    /// 某个成员一行都没有时下界直接消失（该账套的全部日期重算一遍），
    /// 这次重算会把每个成员在每一天的行都补齐，之后下界重新回到水位、只做增量。
    /// <para>
    /// **第三项「最早的未执行结算任务日期减一天」是必须的**：复查补收建出的增量任务，其日期往往
    /// **早于水位**（补的正是已经结算过的那一天），而水位只会说「我已经算到那一天之后了」——
    /// 少了这一项，被补收的那一天会被「> 下界」这个条件挡在外面：事件照常派发、订阅照常成功、
    /// 首页数字却一动不动，是最难发现的那种失败。
    /// 减一天是因为待处理区间是**开区间**（<c>day &gt; 下界</c>），要让它自己被算进来。
    /// </para>
    /// <para>
    /// **同一个机制顺带自愈**：某一日写库失败而水位已推进（理论上不会发生，见 RunAsync 的注释）、
    /// 或记录表被人工清理过，都会表现为「成员覆盖度落后于水位」，下次执行自动补齐。
    /// </para>
    /// <para>
    /// **钱账户为空时退化为水位与未执行任务两者取早**：此时没有任何币种可记，一行都写不出来，
    /// 若仍沿用成员覆盖度，那个「永远为空」的覆盖度会让每个执行日都把历史重算一遍。
    /// </para>
    /// <para>
    /// **与总资产结算是各算各的**：两边读的是各自的记录表，故「收入表被清空」不会连带把总资产也全量重算，
    /// 反之亦然。这是刻意的——两个订阅本就该能独立重跑。
    /// </para>
    /// </remarks>
    private async Task<DateTime?> ResolveFloorAsync(
        int accountSetId,
        IReadOnlyList<Account> moneyAccounts,
        IReadOnlyList<int> memberIds,
        CancellationToken cancellationToken)
    {
        var watermark = await executions.FindLastExecutedDateAsync(
            SubscriptionCode,
            accountSetId,
            cancellationToken);

        var floor = watermark;

        // 「最早的未执行结算任务」本身可能不存在（常态：没有待执行的任务）——此时这一项**不参与**比较，
        // 而**不是**把下界变成「无下界」（那会让每次执行都把全部历史重算一遍）。
        // 待处理区间是开区间（day > 下界），故要让那一天自己被算进来，下界取它的前一天。
        var unexecutedDay = await settlements.FindEarliestUnexecutedDateAsync(accountSetId, cancellationToken);
        if (unexecutedDay is { } pendingDay)
        {
            var candidate = pendingDay.AddDays(-1);
            if (floor is null || candidate < floor.Value)
            {
                floor = candidate;
            }
        }

        if (moneyAccounts.Count == 0 || memberIds.Count == 0)
        {
            return floor;
        }

        var lastDayByUser = await LoadLastRecordedDayByUserAsync(accountSetId, cancellationToken);

        foreach (var memberId in memberIds)
        {
            if (!lastDayByUser.TryGetValue(memberId, out var lastDay))
            {
                // 该成员一天记录都没有 → 无下界（全量重算）
                return null;
            }

            if (floor is null || lastDay < floor.Value)
            {
                floor = lastDay;
            }
        }

        return floor;
    }

    /// <summary>
    /// 重算并落库**某一天**的记录。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="localDay">目标日期（本地日期）。</param>
    /// <param name="moneyAccounts">该账套的钱账户。</param>
    /// <param name="memberIds">该账套的成员主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的记录行数。</returns>
    /// <remarks>
    /// **「先删该日全部行、再插入」同处一个事务**：重算是**覆盖**而不是追加，
    /// 中途失败若留下半天的数据，读侧看到的是一个既不是新值也不是旧值的数字，
    /// 而它看起来完全正常。同事务则「要么整天都是新值、要么整天保持原样」。
    /// <para>
    /// **删除条件用「半开区间」而不是「等于当日」**，这是本方法最容易改错的一处：
    /// SQLite 把日期存成 **TEXT**，而**同一份实体在这张表上会写出两种文本形态**——
    /// 批量 INSERT 时 SqlSugar 把日期渲染成**字面量** <c>'2026-09-23 00:00:00.000'</c>（带三位小数），
    /// 而 <c>== localDay</c> 这类条件里的日期是**参数**、其文本形态为 <c>2026-09-23 00:00:00</c>（小数部分被截掉，
    /// 实测两者在 SQLite 里判不相等）——于是 DELETE 一行都删不掉，紧随其后的 INSERT 撞上唯一键、
    /// 整个事务回滚，而 <c>UseTranAsync</c> 默认把异常**吞掉**：重算表面上报「写入 N 条」，库里却还是旧行。
    /// 区间形式对两种形态都成立（两者都落在 <c>[当日 0 点, 次日 0 点)</c> 内），
    /// 故不依赖小数部分的有无；本条件与子类的取数条件也是同一个口径。
    /// </para>
    /// <para>
    /// **事务失败必须自己抛出**：<c>UseTranAsync</c> 只把异常记进 <c>DbResult</c> 而不重抛，
    /// 不检查的话「这一天写失败了」会伪装成成功——调用方（<see cref="RunAsync"/>）会照常推进水位、
    /// 订阅也会照常计为成功、结算任务照常标记已执行，这一天从此再也不会被重算。
    /// </para>
    /// <para>
    /// **一日只查一次库**：当日发生额由 <c>SumPrimaryAmountsAsync</c> 一次算清（按账户分组），
    /// 币种与「个人 / 公共」的拆分都在内存里完成——逐币种、逐成员各查一次是 O(币种×成员) 次查询，
    /// 且查的是同一段区间。
    /// </para>
    /// </remarks>
    private async Task<int> SettleDayAsync(
        int accountSetId,
        DateTime localDay,
        IReadOnlyList<Account> moneyAccounts,
        IReadOnlyList<int> memberIds,
        CancellationToken cancellationToken)
    {
        // 当日窗口的两个界点（本地 0 点 → 次日本地 0 点），半开区间：界点属于下一天
        var dayStartUtc = LocalDay.StartToUtc(localDay, _timeZone);
        var dayEndUtc = LocalDay.StartToUtc(localDay.AddDays(1), _timeZone);

        var totals = await transactions.SumPrimaryAmountsAsync(
            accountSetId,
            [.. moneyAccounts.Select(account => account.Id)],
            FlowType,
            dayStartUtc,
            dayEndUtc,
            cancellationToken);

        var currencies = moneyAccounts
            .Select(account => account.CurrencyCode)
            .Distinct()
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        var rows = new List<DailyFlowRow>(memberIds.Count * currencies.Count);

        foreach (var currency in currencies)
        {
            var accountsOfCurrency = moneyAccounts
                .Where(account => account.CurrencyCode == currency)
                .ToList();

            var publicTotal = SumTotals(accountsOfCurrency, totals, AccountScope.Public, null);

            foreach (var userId in memberIds)
            {
                rows.Add(new DailyFlowRow(
                    userId,
                    currency,
                    SumTotals(accountsOfCurrency, totals, AccountScope.Personal, userId),
                    publicTotal));
            }
        }

        var dayStart = localDay.Date;
        var dayEnd = dayStart.AddDays(1);

        var tran = await db.Ado.UseTranAsync(async () =>
        {
            await DeleteDayAsync(accountSetId, dayStart, dayEnd, cancellationToken);

            if (rows.Count > 0)
            {
                await InsertDayAsync(accountSetId, localDay, rows, cancellationToken);
            }
        });

        if (!tran.IsSuccess)
        {
            throw new InvalidOperationException(
                $"{FlowLabel}结算：账套 {accountSetId} 的 {localDay:yyyy-MM-dd} 记录落库失败" +
                "（该日记录保持原样，水位不会推进到这一天，下次执行会重算）",
                tran.ErrorException);
        }

        return rows.Count;
    }

    /// <summary>
    /// 把落在某个「归属范围」下的账户的当日发生额加总。
    /// </summary>
    /// <param name="accounts">同一币种下的钱账户。</param>
    /// <param name="totals">账户主键 → 当日发生额；**当日无发生的账户不在其中**，按 0 计。</param>
    /// <param name="scope">归属范围（个人 / 公共）。</param>
    /// <param name="ownerUserId">
    /// 归属人主键：<paramref name="scope"/> 为个人时只累加归属人等于它的账户；为公共时忽略本参数
    /// （公共账户的归属人恒为 <c>null</c>，拿它去比会一个都不匹配）。
    /// </param>
    /// <returns>合计金额（**恒为非负**，见 <c>SumPrimaryAmountsAsync</c> 的符号约定）。</returns>
    /// <remarks>
    /// **币种与账户类型都已由调用方筛过**：本方法只看归属范围，故不必再判
    /// <see cref="Account.CurrencyCode"/> 与 <see cref="Account.Type"/>——
    /// 后者是「钱账户」这条判据的一部分，已在取账户时完成（见 <see cref="LoadMoneyAccountsAsync"/>）。
    /// </remarks>
    private static decimal SumTotals(
        IReadOnlyList<Account> accounts,
        IReadOnlyDictionary<int, decimal> totals,
        AccountScope scope,
        int? ownerUserId)
    {
        var total = 0m;
        foreach (var account in accounts)
        {
            if (account.Scope != scope)
            {
                continue;
            }

            if (scope == AccountScope.Personal && account.OwnerUserId != ownerUserId)
            {
                continue;
            }

            total += totals.GetValueOrDefault(account.Id);
        }

        return total;
    }
}
