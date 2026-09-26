namespace Hamster.Api.Events;

/// <summary>
/// 事件订阅者：处理某一类事件（<typeparamref name="TEvent"/>）。
/// </summary>
/// <typeparam name="TEvent">订阅的事件类型。</typeparam>
/// <remarks>
/// **新增一个订阅者只需实现本接口**：把实现类放进 <c>Hamster.Api</c> 程序集即可，
/// 无需改 <c>Program.cs</c>——注册由 <see cref="EventBusRegistration.AddHamsterEventHandlers"/>
/// 反射完成（与端点 <c>IEndpoint</c> 的自动注册同构，见 <c>EndpointExtensions</c>）。
/// <para>
/// 同一事件类型可以有**任意多个**订阅者，它们按注册顺序被依次调用（见 <see cref="IEventBus"/>）。
/// </para>
/// <para>
/// **订阅者必须幂等**：事件可能被投递不止一次。结算执行任务在「有订阅者失败」时
/// **不会**把结算任务标记为已执行，于是下一个执行日会把同一个事件**重新投递**一遍
/// （见 <c>SettlementTask.ExecutedAt</c>）。这不是缺陷，而是「宁可重投也不丢事件」的取舍——
/// 订阅者按「结算任务主键」做一次去重即可，例如先查自己是否已处理过这个
/// <c>SettlementTaskId</c>。
/// </para>
/// <para>
/// **抛出的异常由事件总线接住并记录**，不会传播给调用方，也**不会阻止其余订阅者执行**；
/// 该次派发会被计为一次失败（见 <see cref="EventDispatchResult"/>）。
/// </para>
/// </remarks>
public interface IEventHandler<in TEvent>
{
    /// <summary>
    /// 处理事件。
    /// </summary>
    /// <param name="event">事件载荷。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处理完成的任务。异常会被总线接住，见接口注释。</returns>
    Task HandleAsync(TEvent @event, CancellationToken cancellationToken = default);
}
