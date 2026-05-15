namespace Hamster.Core;

/// <summary>
/// EndpointRouteBuilder 扩展
/// </summary>
public static class EndpointRouteBuilderExtension
{
    /// <summary>
    /// 映射 Api Endpoint
    /// </summary>
    /// <typeparam name="TEndpoint"></typeparam>
    public static void MapApiEndpoints<TEndpoint>(this IEndpointRouteBuilder routeBuilder)
        where TEndpoint : IApiEndpoint, new()
    {
        var endpoint = new TEndpoint();
        endpoint.Map(routeBuilder);
    }

    /// <summary>
    /// 映射 Api Endpoint
    /// </summary>
    /// <typeparam name="TEndpoint"></typeparam>
    public static void MapApiApp<TEndpoint>(this IEndpointRouteBuilder routeBuilder)
        where TEndpoint : IMiniApp, new()
    {
        var endpoint = new TEndpoint();
        endpoint.Map(routeBuilder);
    }
}
