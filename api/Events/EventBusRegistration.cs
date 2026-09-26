using Microsoft.Extensions.DependencyInjection;

namespace Hamster.Api.Events;

/// <summary>
/// 事件订阅者统一注册。
/// </summary>
/// <remarks>
/// 与 <c>EndpointExtensions.MapHamsterEndpoints</c> 同构：都是「写一个实现类就自动生效」，
/// 不让调用方维护一份手写的注册清单——清单迟早会漏（新增订阅者时忘了加一行，
/// 表现为「代码明明写了却从不被调用」，且编译期毫无提示）。
/// </remarks>
public static class EventBusRegistration
{
    /// <summary>
    /// 反射扫描当前程序集中所有 <see cref="IEventHandler{TEvent}"/> 实现，并按其实现的接口类型注册。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>服务集合，便于链式调用。</returns>
    public static IServiceCollection AddHamsterEventHandlers(this IServiceCollection services)
    {
        var handlerTypes = typeof(EventBusRegistration).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .SelectMany(type => type.GetInterfaces()
                .Where(@interface => @interface.IsGenericType &&
                                     @interface.GetGenericTypeDefinition() == typeof(IEventHandler<>))
                .Select(@interface => (Service: @interface, Implementation: type)))
            .OrderBy(pair => pair.Implementation.Name)
            .ToArray();

        foreach (var (service, implementation) in handlerTypes)
        {
            // 单个订阅者注册为单例：订阅者通常无状态，且结算事件是低频的
            // （一天至多一次），无需按请求或按作用域重建。
            // 订阅者若要持有有状态资源，应自行注入相应生命周期的依赖，而不是把本类改成 Scoped。
            services.AddSingleton(service, implementation);
        }

        return services;
    }
}
