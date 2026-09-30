using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 结算业务服务：按「交易日期」把某个账套某一天的交易与明细**留档**成结算任务与快照，
/// 并为「事后才补记/改动到已过去那些日子」的交易补建同日的增量结算任务。
/// </summary>
/// <remarks>
/// 本服务是**两个定时任务的共同底座**，它自己不认识「定时」二字：
/// <list type="bullet">
/// <item>交易统计定时任务调 <see cref="CollectAsync"/>（建立结算任务 + 冗余存储）；</item>
/// <item>结算执行定时任务调 <see cref="FindPendingExecutionsAsync"/> 取待执行的结算任务、
/// 逐个派发结算事件、再调 <see cref="MarkExecutedAsync"/> 记账「这次结算已派发成功」。</item>
/// </list>
/// 把「何时跑」留给 <c>Jobs</c> 层、把「跑什么」收在本服务里，是为了让结算逻辑能被
/// 单独调用与单独验证（隔离实例里可以直接触发一次收集，而不必等到当天 0 点 5 分）。
/// <para>
/// **与交易写入的关系**：本服务**只读** <see cref="Transaction"/> 与 <see cref="TransactionEntry"/>，
/// 从不改写它们——结算是「抄一份留档」，不是「调整账目」。写出去的只有三张结算表。
/// 这一点决定了本服务可以放心在后台线程里跑，不会与用户正在进行的记账互相干扰。
/// </para>
/// <para>
/// **按账套隔离**：交易归属账套，故结算任务与快照也都归属账套，
/// 一次收集会为每个有交易的账套分别建立结算任务（见 <see cref="CollectAsync"/>）。
/// </para>
/// </remarks>
public interface ISettlementService
{
    /// <summary>
    /// 执行一次交易统计：把「尚未结算过的、且早于今天 0 点」的交易按「账套 + 交易日期」分组，
    /// 逐组建立结算任务并冗余存储其全部交易与明细；随后再做一次**全量复查补收**，
    /// 把「事后补记/改动到已结算日期」的交易补收成同日的增量结算任务。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次收集的成果概要。</returns>
    /// <remarks>
    /// 一次收集由**两趟**组成，两趟的判据不同，缺任何一趟都会漏账：
    /// <list type="number">
    /// <item>
    /// <b>窗口趟</b>（<c>[各账套水位 + 1 天, 今天 0 点)</c>）：水位即该账套已有结算任务里最大的
    /// <see cref="SettlementTask.TransactionDate"/>——「上一次执行日期」这一说法在本系统里
    /// **不是靠单独记录一个时刻来承载的**，而是从结算任务本身推导出来的：
    /// 结算任务一旦存在就代表那一天已经被收集过，最大值即最后一次成功收集的那一天。
    /// 该趟内**已有结算任务的日期整日跳过**，故它只解决「往前推进」。
    /// </item>
    /// <item>
    /// <b>复查趟</b>（全量，见 <see cref="CollectAsync"/> 的实现）：凡「一笔快照留档都没有」或
    /// 「已有快照的 <c>updated_at</c> 与交易当前的 <c>updated_at</c> 不一致」的交易，
    /// 都是**尚未被如实留档**的交易，按本地日分组后各建一条**同日的增量结算任务**。
    /// </item>
    /// </list>
    /// <para>
    /// **为什么必须有复查趟**：水位是「推进到哪天」，而不是「哪天的账已经全部留档」。
    /// 以「29 日补记 28 日的账」为例——28 日的任务在 29 日 0 点 5 分就已建立、水位随之到了 28 日，
    /// 于是 30 日这次收集的窗口是 <c>[29 日, 30 日)</c>：这笔账日期是 28 日、落在窗口之外，
    /// 即便落在窗口内也会被「该日已有任务」跳过，**从此永远不会被收集**。
    /// 「某天当时没有账（因此没建任务）、事后才补记」是同一性质的另一条路径，复查趟一并覆盖。
    /// </para>
    /// <para>
    /// **首次执行不限初始时间**：该账套一条结算任务都没有时，水位视为无穷小，
    /// 于是「有史以来到昨天为止」的全部交易都会被窗口趟收集——这正是任务描述里
    /// 「第一次执行不限初始时间」的含义（此时复查趟也找不到任何未留档的交易）。
    /// </para>
    /// <para>
    /// **幂等**：窗口趟内已有任务的日期会跳过（包括「同一天内被触发两次」）；复查趟的判据是
    /// 「快照是否与交易一致」，补收任务一旦落库，那些交易就有了相符的快照，再跑一次不会重复补收。
    /// 注意**原结算任务与既有的快照行一律不改写**：补收只**新增**同日任务，
    /// 已执行的任务（含 <see cref="SettlementTask.ExecutedAt"/>）保持原样——
    /// 改写它等于篡改历史结算记录，而「某天的留档」由该天全部任务行的并集构成。
    /// </para>
    /// <para>
    /// **没有交易的日子不建结算任务**：任务描述说的是「收集…所有的交易信息，并按照交易日期分组，
    /// 建立结算任务」，分组的结果里本来就不含空组。给空日子建一个空任务只会让
    /// 「结算任务表」里堆满不含信息量的行——而这类空日子事后若真被补了账，复查趟会把它建出来。
    /// </para>
    /// <para>
    /// **不做重算、不删任何行**：上一个执行日没跑成，今天的窗口会自动从水位处续上，
    /// 中间漏掉的日子一次补齐（窗口是「水位到今天」这一整段，而不是固定的一天）。
    /// </para>
    /// </remarks>
    Task<SettlementCollectionResult> CollectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 取某账套**最早的、尚未执行结算**的日期（即 <see cref="SettlementTask.ExecutedAt"/> 为空的任务里
    /// 最小的 <see cref="SettlementTask.TransactionDate"/>）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最早未执行的日期；该账套没有未执行的任务时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 供订阅者算**起始下界**之用：复查补收建出的增量任务，其日期往往**早于订阅水位**
    /// （补的正是已经结算过的那一天），只按水位取下界会让这一天被挡在待处理区间之外——
    /// 事件照常派发、订阅照常「成功」，首页数字却一动不动，是最难发现的那种失败。
    /// 故下界要再取一次「最早未执行任务的日期」。
    /// <para>
    /// 比较在 C# 侧完成（同 <c>SettlementService.LoadWatermarksAsync</c> 的取舍）：
    /// 一天的比较不差这一次查询的代价，换来的是不依赖两种库对日期文本/时间的排序行为。
    /// </para>
    /// </remarks>
    Task<DateTime?> FindEarliestUnexecutedDateAsync(
        int accountSetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 取出全部**尚未执行结算**的结算任务，按交易日期升序（同日按主键升序）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>待执行的结算任务列表；已全部执行完时为空列表。</returns>
    /// <remarks>
    /// 判定依据是 <see cref="SettlementTask.ExecutedAt"/> 为空，
    /// **没有另设状态列或次数列**：一次结算只有「已派发」与「未派发」两种状态，
    /// 再引入一个枚举迟早会与这个时间列打架（谁说了算？）。
    /// <para>
    /// 顺序刻意是「交易日期升序」而不是主键升序：增量消费的订阅者（如把结算结果同步到外部报表）
    /// 依赖事件按时间先后到达，而这与主键顺序在「补跑漏掉的日子」时并不一致——
    /// 那种场景下先收集的是更晚的日期（当天的窗口先跑），主键顺序因此可能与日期顺序相反。
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<SettlementTask>> FindPendingExecutionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 统计某个结算任务下的快照交易与快照明细条数。
    /// </summary>
    /// <param name="settlementTaskId">结算任务主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条数；结算任务不存在（或已被删除）时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 供结算事件载荷携带「这次结算了多少」之用（见 <c>SettlementTriggeredEvent</c>）：
    /// 订阅者常常只需要一个规模数字，而载荷里刻意不放明细本身。
    /// </remarks>
    Task<SettlementSnapshotCounts?> CountSnapshotsAsync(
        int settlementTaskId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 标记结算任务「已成功派发结算事件」。
    /// </summary>
    /// <param name="task">目标结算任务。</param>
    /// <param name="executedAt">结算执行时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>确实由本次调用改写了该行返回 <c>true</c>；已被其它实例标记时返回 <c>false</c>。</returns>
    /// <remarks>
    /// **带 <c>ExecutedAt</c> 为空的前提条件**：更新语句是
    /// <c>WHERE id = @id AND executed_at IS NULL</c>，故两个实例（或同一实例的两次触发）同时结算同一个任务时，
    /// 只有一个会返回 <c>true</c>。返回 <c>false</c> 表示「这次结算已经被记过账了」，
    /// 调用方据此知道本轮的派发是**重复**的——事件的幂等要求因此有一个可观测的落点。
    /// </remarks>
    Task<bool> MarkExecutedAsync(
        SettlementTask task,
        DateTime executedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 一次交易统计（结算收集）的成果概要。
/// </summary>
/// <param name="CreatedTasks">本次由**窗口趟**新建的结算任务（按账套、再按交易日期升序）。</param>
/// <param name="RecheckTasks">
/// 本次由**复查趟**（补收）新建的增量结算任务（按账套、再按交易日期升序）；
/// 与 <paramref name="CreatedTasks"/> 不会有交集——同一趟里新写的快照会被另一趟如实认作「已留档」。
/// </param>
/// <param name="TransactionCount">本次冗余存储的快照交易条数合计（两趟合计）。</param>
/// <param name="EntryCount">本次冗余存储的快照明细条数合计（两趟合计）。</param>
/// <remarks>
/// 两类任务**分开列出**而不是合并成一个列表：调用方的日志要说清「哪些是正常推进、
/// 哪些是补收」，因为补收代表库里出现了「已结算日期又被改动」这件事，值得一眼看见
/// （只有日期会重复出现时才必须区分，而两类任务的日期恰恰可能重合）。
/// <para>
/// 两个条数是**合计**而不是分账套的明细：调用方（定时任务）要用它们判断
/// 「这次是不是什么都没做」（两者皆为 0 且没有新任务），日志里给一个总量已足够定位问题，
/// 需要细节时按任务里的主键到库里查即可。
/// </para>
/// </remarks>
public sealed record SettlementCollectionResult(
    IReadOnlyList<SettlementTask> CreatedTasks,
    IReadOnlyList<SettlementTask> RecheckTasks,
    int TransactionCount,
    int EntryCount)
{
    /// <summary>本次是否什么都没新建（窗口与复查两趟都没有待收集的交易）。</summary>
    public bool IsEmpty => CreatedTasks.Count == 0 && RecheckTasks.Count == 0;
}

/// <summary>
/// 某个结算任务下的快照条数。
/// </summary>
/// <param name="TransactionCount">快照交易条数。</param>
/// <param name="EntryCount">快照明细条数。</param>
public sealed record SettlementSnapshotCounts(int TransactionCount, int EntryCount);
