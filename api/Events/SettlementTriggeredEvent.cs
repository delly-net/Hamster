namespace Hamster.Api.Events;

/// <summary>
/// 结算事件：一个结算任务（某账套的某一天）被结算执行任务触发。
/// </summary>
/// <param name="SettlementTaskId">结算任务主键（<c>hamster_settlement_task.id</c>）。</param>
/// <param name="AccountSetId">所属账套主键。</param>
/// <param name="TransactionDate">
/// 交易日期（**本地日期**，时刻部分恒为 00:00:00）。
/// 口径与 <c>SettlementTask.TransactionDate</c> 完全一致，读到后直接取日期部分，
/// **不要**再当作 UTC 去转时区。
/// </param>
/// <param name="SettledAt">结算执行时间（UTC）。</param>
/// <param name="TransactionCount">该结算任务下的快照交易条数。</param>
/// <param name="EntryCount">该结算任务下的快照明细条数。</param>
/// <remarks>
/// 这是**统一事件订阅机制的第一个使用者**（见 <see cref="IEventBus"/>）。
/// 订阅者实现 <see cref="IEventHandler{TEvent}"/> 并在
/// <c>Hamster.Api</c> 程序集内即可被自动注册，无需改 <c>Program.cs</c>。
/// <para>
/// **载荷刻意不含交易明细本身**：明细可能有成千上万条，塞进事件会让每一次派发都付出
/// 整批数据的构造成本，而绝大多数订阅者并不需要它。需要细节的订阅者按
/// <see cref="SettlementTaskId"/> 自行到 <c>ISettlementService</c> 取
/// （快照表是冻结的，任何时候取到的都是当次结算的内容）。
/// </para>
/// <para>
/// **订阅者必须幂等**：见 <see cref="IEventHandler{TEvent}"/> 的接口注释。
/// </para>
/// </remarks>
public sealed record SettlementTriggeredEvent(
    int SettlementTaskId,
    int AccountSetId,
    DateTime TransactionDate,
    DateTime SettledAt,
    int TransactionCount,
    int EntryCount);
