using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的总资产结算实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <param name="transactions">交易业务服务：账户余额的**唯一**来源（<c>SumSignedAmountsAsync</c>）。</param>
/// <param name="accountSets">账套服务：取账套成员列表（记录按用户分行，成员口径即此）。</param>
/// <param name="executions">订阅执行水位。</param>
/// <param name="logger">日志记录器。</param>
/// <remarks>
/// **金额一律取自 <see cref="ITransactionService.SumSignedAmountsAsync"/>，本服务不自己查明细**：
/// 「明细 → 账户余额」的换算（含期初金额已是一笔分录、不再叠加
/// <see cref="Account.InitialBalance"/> 那条）全项目只有那一处，
/// 本服务若另写一份汇总，首页的总资产与账户页的余额就会是两个口径。
/// <para>
/// **日期一律取自 <see cref="SettlementTask"/>**：任务描述要求「从结算任务表中捞取未执行过的日期数据」，
/// 而结算任务正是「某账套的某一天」的权威清单——它由收集任务按交易日期的口径建立，
/// 本服务不重复做「哪些天算有账」的判断（那需要时区换算与日界判定，见 <c>SettlementService</c>）。
/// </para>
/// </remarks>
public sealed class TotalAssetSettlementService(
    ISqlSugarClient db,
    ITransactionService transactions,
    IAccountSetService accountSets,
    ISettlementSubscriptionExecutionService executions,
    ILogger<TotalAssetSettlementService> logger) : ITotalAssetSettlementService
{
    /// <summary>
    /// 日界换算用的本地时区，构造时解析一次并缓存（口径见 <see cref="LocalDay"/>）。
    /// </summary>
    private readonly TimeZoneInfo _timeZone = LocalDay.ResolveTimeZone(logger);

    /// <inheritdoc />
    public async Task<TotalAssetSettlementRunResult> RunAsync(
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
                "总资产结算：账套 {AccountSetId} 尚无任何结算任务，跳过",
                accountSetId);
            return new TotalAssetSettlementRunResult(accountSetId, 0, 0, null);
        }

        // 排序放在 C# 侧：Sqlite 把日期存成文本，「按文本排」与「按日期排」是否等价依赖存储格式，
        // 而这一处的顺序直接决定水位推进的顺序，不把它交给数据库（同 SettlementService.LoadWatermarksAsync）
        var orderedDays = settledDays.OrderBy(day => day).ToList();

        var moneyAccounts = await LoadMoneyAccountsAsync(accountSetId, cancellationToken);
        var memberIds = await LoadMemberIdsAsync(accountSetId, cancellationToken);

        var floor = await ResolveFloorAsync(accountSetId, moneyAccounts, memberIds, cancellationToken);
        var pendingDays = floor is { } start
            ? orderedDays.Where(day => day > start).ToList()
            : orderedDays;

        if (pendingDays.Count == 0)
        {
            logger.LogInformation(
                "总资产结算：账套 {AccountSetId} 没有待处理的日期（水位已在 {Date}）",
                accountSetId,
                floor?.ToString("yyyy-MM-dd") ?? "（无）");
            return new TotalAssetSettlementRunResult(accountSetId, 0, 0, null);
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
            await executions.AdvanceAsync(
                ITotalAssetSettlementService.SUBSCRIPTION_CODE,
                accountSetId,
                day,
                cancellationToken);

            advancedTo = day;
        }

        logger.LogInformation(
            "总资产结算完成：账套 {AccountSetId} 重算 {DayCount} 天（{From} ~ {To}），写入 {RecordCount} 条记录" +
            "（{MemberCount} 个成员 × 币种），水位推进到 {Watermark}",
            accountSetId,
            pendingDays.Count,
            pendingDays[0].ToString("yyyy-MM-dd"),
            pendingDays[^1].ToString("yyyy-MM-dd"),
            recordCount,
            memberIds.Count,
            advancedTo?.ToString("yyyy-MM-dd") ?? "（无）");

        return new TotalAssetSettlementRunResult(accountSetId, pendingDays.Count, recordCount, advancedTo);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TotalAssetSettlementRecord>> ListDailyAsync(
        int accountSetId,
        int userId,
        string currencyCode,
        DateTime monthStart,
        CancellationToken cancellationToken = default)
    {
        var start = monthStart.Date;
        var end = start.AddMonths(1);

        // 区间直接比较、不做 ±1s 预筛：本表的时间列由本表自己写入、恒为本地日 0 点，
        // 与这里的两个参数逐字同格式（日期列没有「用户手工输入的时间戳」那种精度差异，
        // 见 LocalDay.PrefilterMargin 所针对的情形）。
        return await db.Queryable<TotalAssetSettlementRecord>()
            .Where(record => record.AccountSetId == accountSetId &&
                             record.UserId == userId &&
                             record.CurrencyCode == currencyCode)
            .Where(record => record.TransactionDate >= start && record.TransactionDate < end)
            .OrderBy(record => record.TransactionDate)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// 取该账套的**钱账户**（资金与负债），含已停用账户。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>钱账户列表，按主键升序。</returns>
    /// <remarks>
    /// **只认资金与负债两类**，与 <c>AccountTypeExtensions.IsMoneyAccount</c> 是同一条判据
    /// （「资产 / 负债」的总金额说的就是这两类的总金额）：账本账户是复式记账的对手方、
    /// 往来账户记的是「谁欠谁」，两者都不是钱放在哪里。此处用**枚举字面量**而不是那个扩展方法——
    /// 扩展方法无法被 SqlSugar 翻译成 SQL（见 <see cref="AccountTypeExtensions.IsMoneyAccount"/> 的注释）。
    /// <para>
    /// **不过滤 <see cref="Account.IsActive"/>**：停用是「不再出现在候选列表里」，不是「这笔钱不曾存在」。
    /// 停用账户上仍有历史明细，余额也照常参与账户页的汇总；本表若把它排除，
    /// 同一天的「总资产」就会与账户页对不上，且停用一个账户会让历史曲线**凭空掉一个台阶**。
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
    /// 算出本次执行的**起始下界**（水位与「成员各自的记录覆盖度」中更早的那个）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="moneyAccounts">该账套的钱账户（为空时直接按水位走，见 remarks）。</param>
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
    /// **同一个机制顺带自愈**：某一日写库失败而水位已推进（理论上不会发生，见 RunAsync 的注释）、
    /// 或记录表被人工清理过，都会表现为「成员覆盖度落后于水位」，下次执行自动补齐。
    /// </para>
    /// <para>
    /// **钱账户为空时退化为只看水位**：此时没有任何币种可记，一行都写不出来，
    /// 若仍沿用成员覆盖度，那个「永远为空」的覆盖度会让每个执行日都把历史重算一遍。
    /// </para>
    /// </remarks>
    private async Task<DateTime?> ResolveFloorAsync(
        int accountSetId,
        IReadOnlyList<Account> moneyAccounts,
        IReadOnlyList<int> memberIds,
        CancellationToken cancellationToken)
    {
        var watermark = await executions.FindLastExecutedDateAsync(
            ITotalAssetSettlementService.SUBSCRIPTION_CODE,
            accountSetId,
            cancellationToken);

        if (moneyAccounts.Count == 0 || memberIds.Count == 0)
        {
            return watermark;
        }

        var stored = await db.Queryable<TotalAssetSettlementRecord>()
            .Where(record => record.AccountSetId == accountSetId)
            .Select(record => new { record.UserId, record.TransactionDate })
            .ToListAsync(cancellationToken);

        // 取每个成员的最后一天，比较在 C# 侧（同 LoadWatermarksAsync 的取舍）
        var lastDayByUser = new Dictionary<int, DateTime>();
        foreach (var row in stored)
        {
            if (!lastDayByUser.TryGetValue(row.UserId, out var current) || row.TransactionDate > current)
            {
                lastDayByUser[row.UserId] = row.TransactionDate;
            }
        }

        var floor = watermark;
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
    /// **删除条件用「半开区间」而不是「等于当日」**，这是本方法最容易被改错的一处：
    /// SQLite 把日期存成 **TEXT**，而**同一份实体在这张表上会写出两种文本形态**——
    /// 批量 INSERT 时 SqlSugar 把日期渲染成**字面量** <c>'2026-09-23 00:00:00.000'</c>（带三位小数），
    /// 而 <c>== localDay</c> 这类条件里的日期是**参数**、其文本形态为 <c>2026-09-23 00:00:00</c>（小数部分被截掉，
    /// 实测两者在 SQLite 里判不相等）——于是 DELETE 一行都删不掉，紧随其后的 INSERT 撞上
    /// <c>uk_hamster_total_asset_settlement_record_scope</c>、整个事务回滚，
    /// 而 <c>UseTranAsync</c> 默认把异常**吞掉**：重算表面上报「写入 N 条」，库里却还是旧行。
    /// 区间形式对两种形态都成立（两者都落在 <c>[当日 0 点, 次日 0 点)</c> 内），
    /// 故不依赖小数部分的有无；本条件与 <see cref="ListDailyAsync"/> 的取数条件也是同一个口径。
    /// </para>
    /// <para>
    /// **事务失败必须自己抛出**：<c>UseTranAsync</c> 只把异常记进 <c>DbResult</c> 而不重抛，
    /// 不检查的话「这一天写失败了」会伪装成成功——调用方（<see cref="RunAsync"/>）会照常推进水位、
    /// 订阅也会照常计为成功、结算任务照常标记已执行，这一天从此再也不会被重算。
    /// </para>
    /// </remarks>
    private async Task<int> SettleDayAsync(
        int accountSetId,
        DateTime localDay,
        IReadOnlyList<Account> moneyAccounts,
        IReadOnlyList<int> memberIds,
        CancellationToken cancellationToken)
    {
        // 该日结束的时刻（次日本地 0 点），余额累计到它为止——半开区间，界点属于下一天
        var dayEndUtc = LocalDay.StartToUtc(localDay.AddDays(1), _timeZone);

        var balances = await transactions.SumSignedAmountsAsync(
            accountSetId,
            [.. moneyAccounts.Select(account => account.Id)],
            dayEndUtc,
            cancellationToken);

        var currencies = moneyAccounts
            .Select(account => account.CurrencyCode)
            .Distinct()
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        var recordedAt = DateTime.UtcNow;
        var records = new List<TotalAssetSettlementRecord>(memberIds.Count * currencies.Count);

        foreach (var currency in currencies)
        {
            var accountsOfCurrency = moneyAccounts
                .Where(account => account.CurrencyCode == currency)
                .ToList();

            foreach (var userId in memberIds)
            {
                records.Add(new TotalAssetSettlementRecord
                {
                    AccountSetId = accountSetId,
                    UserId = userId,
                    CurrencyCode = currency,
                    TransactionDate = localDay,
                    PersonalAssetTotal = SumTotals(
                        accountsOfCurrency, balances, AccountScope.Personal, AccountType.Fund, userId),
                    PersonalLiabilityTotal = SumTotals(
                        accountsOfCurrency, balances, AccountScope.Personal, AccountType.Liability, userId),
                    PublicAssetTotal = SumTotals(
                        accountsOfCurrency, balances, AccountScope.Public, AccountType.Fund, null),
                    PublicLiabilityTotal = SumTotals(
                        accountsOfCurrency, balances, AccountScope.Public, AccountType.Liability, null),
                    CreatedAt = recordedAt,
                    UpdatedAt = recordedAt,
                });
            }
        }

        var dayStart = localDay.Date;
        var dayEnd = dayStart.AddDays(1);

        var tran = await db.Ado.UseTranAsync(async () =>
        {
            await db.Deleteable<TotalAssetSettlementRecord>()
                .Where(record => record.AccountSetId == accountSetId &&
                                 record.TransactionDate >= dayStart &&
                                 record.TransactionDate < dayEnd)
                .ExecuteCommandAsync(cancellationToken);

            if (records.Count > 0)
            {
                await db.Insertable(records).ExecuteCommandAsync(cancellationToken);
            }
        });

        if (!tran.IsSuccess)
        {
            throw new InvalidOperationException(
                $"总资产结算：账套 {accountSetId} 的 {localDay:yyyy-MM-dd} 记录落库失败" +
                "（该日记录保持原样，水位不会推进到这一天，下次执行会重算）",
                tran.ErrorException);
        }

        return records.Count;
    }

    /// <summary>
    /// 把落在某个「归属范围 + 账户类型」下的账户余额加总。
    /// </summary>
    /// <param name="accounts">同一币种下的钱账户。</param>
    /// <param name="balances">账户主键 → 有符号余额；**无明细的账户不在其中**，按 0 计。</param>
    /// <param name="scope">归属范围（个人 / 公共）。</param>
    /// <param name="type">账户类型（资金 / 负债）。</param>
    /// <param name="ownerUserId">
    /// 归属人主键：<paramref name="scope"/> 为个人时只累加归属人等于它的账户；为公共时忽略本参数
    /// （公共账户的归属人恒为 <c>null</c>，拿它去比会一个都不匹配）。
    /// </param>
    /// <returns>合计金额（负债账户为带符号值，欠款为负）。</returns>
    private static decimal SumTotals(
        IReadOnlyList<Account> accounts,
        IReadOnlyDictionary<int, decimal> balances,
        AccountScope scope,
        AccountType type,
        int? ownerUserId)
    {
        var total = 0m;
        foreach (var account in accounts)
        {
            if (account.Scope != scope || account.Type != type)
            {
                continue;
            }

            if (scope == AccountScope.Personal && account.OwnerUserId != ownerUserId)
            {
                continue;
            }

            total += balances.GetValueOrDefault(account.Id);
        }

        return total;
    }
}
