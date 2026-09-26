using Microsoft.Extensions.DependencyInjection;

namespace Hamster.Api.Events;

/// <summary>
/// 基于依赖注入的事件总线实现。
/// </summary>
/// <param name="services">服务提供者，用于解析该事件类型的全部订阅者。</param>
/// <param name="logger">日志记录器。</param>
/// <remarks>
/// 订阅者取自 <c>IEnumerable&lt;IEventHandler&lt;TEvent&gt;&gt;</c>，因此**顺序即注册顺序**
/// （微软内置容器的多注册按注册先后返回）。订阅者由
/// <see cref="EventBusRegistration.AddHamsterEventHandlers"/> 反射注册。
/// <para>
/// 用 <see cref="IServiceProvider"/> 而不是构造注入某个具体事件类型：
/// 总线要能派发**任意**事件类型，构造注入会把它的契约钉死在某一种事件上，
/// 每加一类事件就得改一次总线的构造函数。
/// </para>
/// <para>
/// 注册为**单例**：总线本身无状态（状态都在订阅者里），单例可避免每次派发重新构建对象图。
/// 订阅者各自的生命周期由注册时决定。
/// </para>
/// </remarks>
public sealed class EventBus(IServiceProvider services, ILogger<EventBus> logger) : IEventBus
{
    /// <inheritdoc />
    public async Task<EventDispatchResult> PublishAsync<TEvent>(
        TEvent @event,
        CancellationToken cancellationToken = default)
    {
        var handlers = services.GetServices<IEventHandler<TEvent>>().ToList();
        if (handlers.Count == 0)
        {
            // 无订阅者是**正常状态**：机制先行、订阅者随后按需添加（本期的结算事件即如此）。
            // 调用方据 FailureCount == 0 判定派发成功，故此处不打告警，只留一条可追溯的记录。
            logger.LogInformation("事件 {EventType} 没有订阅者，已跳过派发", typeof(TEvent).Name);
            return new EventDispatchResult(0, 0);
        }

        var failures = 0;
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(@event, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // 停机导致的中断不算订阅者失败：把它计入失败会让一次正常关闭
                // 在库里留下「结算未执行」的假象，下一个执行日再投一遍
                throw;
            }
            catch (Exception ex)
            {
                // 单个订阅者失败不阻断其余订阅者：各订阅者是彼此独立的功能扩展，
                // 一个坏掉不应该让另一个也跟着不执行（结算执行任务据 FailureCount 重试）
                failures++;
                logger.LogError(
                    ex,
                    "事件 {EventType} 的订阅者 {Handler} 处理失败（本次派发共 {Total} 个订阅者，已失败 {Failures} 个）",
                    typeof(TEvent).Name,
                    handler.GetType().Name,
                    handlers.Count,
                    failures);
            }
        }

        logger.LogInformation(
            "事件 {EventType} 派发完成：订阅者 {Total} 个，失败 {Failures} 个",
            typeof(TEvent).Name,
            handlers.Count,
            failures);

        return new EventDispatchResult(handlers.Count, failures);
    }
}
