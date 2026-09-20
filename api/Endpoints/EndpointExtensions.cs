namespace Hamster.Api.Endpoints;

/// <summary>
/// 端点模块统一注册。
/// </summary>
public static class EndpointExtensions
{
    /// <summary>
    /// 反射扫描当前程序集中所有 <see cref="IEndpoint"/> 实现并注册其路由。
    /// </summary>
    /// <param name="app">端点路由构建器。</param>
    /// <returns>端点路由构建器，便于链式调用。</returns>
    public static IEndpointRouteBuilder MapHamsterEndpoints(this IEndpointRouteBuilder app)
    {
        var endpointTypes = typeof(EndpointExtensions).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } &&
                           typeof(IEndpoint).IsAssignableFrom(type))
            .OrderBy(type => type.Name)
            .ToArray();

        var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Hamster.Api.Endpoints");

        foreach (var endpointType in endpointTypes)
        {
            var endpoint = (IEndpoint)Activator.CreateInstance(endpointType)!;
            endpoint.Map(app);
            logger.LogDebug("已注册端点模块：{EndpointModule}", endpointType.Name);
        }

        logger.LogInformation("端点模块注册完成，共 {Count} 个", endpointTypes.Length);
        return app;
    }
}
