namespace Hamster.Api.Events;

/// <summary>
/// 事件总线：把事件派发给该事件类型的全部订阅者。
/// </summary>
/// <remarks>
/// 这是本项目的**统一事件订阅机制**：新增一类事件只需定义事件类型（一个 <c>record</c>），
/// 新增一个订阅者只需实现 <see cref="IEventHandler{TEvent}"/>，两者都不必改动总线本身。
/// 结算事件（<see cref="SettlementTriggeredEvent"/>）是第一个使用者。
/// <para>
/// **进程内派发、同步等待**：不引入消息队列或后台队列——结算执行任务需要一个
/// 「这次派发到底成没成」的即时结论，异步投递会让那个结论无从取得。
/// 本机制面向的是**同进程内的功能扩展**（生成报表、发通知、写审计），
/// 不是跨服务的集成通道。
/// </para>
/// <para>
/// 订阅者**依次串行**执行（注册顺序）：并发派发会让「哪个订阅者失败了」与
/// 「失败发生在第几步」变得难以复现，而结算事件本身是低频的，串行没有性能压力。
/// </para>
/// </remarks>
public interface IEventBus
{
    /// <summary>
    /// 把事件派发给该事件类型的全部订阅者。
    /// </summary>
    /// <typeparam name="TEvent">事件类型。</typeparam>
    /// <param name="event">事件载荷。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 派发结果统计。订阅者的异常被逐个接住并计入失败，**不向外抛出**；
    /// 唯一的例外是 <paramref name="cancellationToken"/> 已被请求时的
    /// <see cref="OperationCanceledException"/>——那表示进程正在停机，
    /// 应当立即向上传播让调用方干净退出，而不是把一次正常关闭记成订阅者失败。
    /// </returns>
    Task<EventDispatchResult> PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default);
}
