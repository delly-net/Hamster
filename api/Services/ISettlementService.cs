using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 结算业务服务：按「交易日期」把某个账套某一天的交易与明细**冻结**成结算任务与快照。
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
    /// 逐组建立结算任务并冗余存储其全部交易与明细。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本次收集的成果概要。</returns>
    /// <remarks>
    /// **窗口**：<c>[各账套水位 + 1 天, 今天 0 点)</c>。水位即该账套已有结算任务里最大的
    /// <see cref="SettlementTask.TransactionDate"/>——「上一次执行日期」这一说法在本系统里
    /// **不是靠单独记录一个时刻来承载的**，而是从结算任务本身推导出来的：
    /// 结算任务一旦存在就代表那一天已经被收集过，最大值即最后一次成功收集的那一天。
    /// 这样「已收集到哪天」与「库里有哪几天的结算任务」永远不会互相矛盾，
    /// 也不存在一个需要在多处同步维护的水位字段。
    /// <para>
    /// **首次执行不限初始时间**：该账套一条结算任务都没有时，水位视为无穷小，
    /// 于是「有史以来到昨天为止」的全部交易都会被收集——这正是任务描述里
    /// 「第一次执行不限初始时间」的含义。
    /// </para>
    /// <para>
    /// **幂等**：当天已经建立过结算任务的日期会被跳过（包括「同一天内被触发两次」）。
    /// 已结算的日子**冻结**、不做重算——即使那天的某笔交易事后被改账，快照仍保留结算当时的样貌，
    /// 这正是「快照」相对「实时查询」的意义（同 <see cref="SettlementTransaction"/> 的类头注释）。
    /// </para>
    /// <para>
    /// **没有交易的日子不建结算任务**：任务描述说的是「收集…所有的交易信息，并按照交易日期分组，
    /// 建立结算任务」，分组的结果里本来就不含空组。给空日子建一个空任务只会让
    /// 「结算任务表」里堆满不含信息量的行，而「那天没有账」这件事从「那天没有任务」即可读出一半——
    /// 之所以说一半，是因为它也可能表示「那天的任务还没建」，这正是下面那条宽限期的用途。
    /// </para>
    /// <para>
    /// **不做任何补偿动作**：上一个执行日没跑成，今天的窗口会自动从水位处续上，
    /// 中间漏掉的日子一次补齐（窗口是「水位到今天」这一整段，而不是固定的一天）。
    /// </para>
    /// </remarks>
    Task<SettlementCollectionResult> CollectAsync(CancellationToken cancellationToken = default);

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
/// <param name="CreatedTasks">本次新建的结算任务（按账套、再按交易日期升序）。</param>
/// <param name="TransactionCount">本次冗余存储的快照交易条数合计。</param>
/// <param name="EntryCount">本次冗余存储的快照明细条数合计。</param>
/// <remarks>
/// 两个条数是**合计**而不是分账套的明细：调用方（定时任务）要用它们判断
/// 「这次是不是什么都没做」（两者皆为 0 且没有新任务），日志里给一个总量已足够定位问题，
/// 需要细节时按 <see cref="CreatedTasks"/> 里的主键到库里查即可。
/// </remarks>
public sealed record SettlementCollectionResult(
    IReadOnlyList<SettlementTask> CreatedTasks,
    int TransactionCount,
    int EntryCount)
{
    /// <summary>本次是否什么都没新建（窗口内没有待收集的交易）。</summary>
    public bool IsEmpty => CreatedTasks.Count == 0;
}

/// <summary>
/// 某个结算任务下的快照条数。
/// </summary>
/// <param name="TransactionCount">快照交易条数。</param>
/// <param name="EntryCount">快照明细条数。</param>
public sealed record SettlementSnapshotCounts(int TransactionCount, int EntryCount);
