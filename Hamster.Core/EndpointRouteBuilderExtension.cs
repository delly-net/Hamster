namespace Hamster.Core;

/// <summary>
/// EndpointRouteBuilder 扩展
/// </summary>
public static class EndpointRouteBuilderExtension
{

    /// <summary>
    /// 映射 Api 应用
    /// </summary>
    /// <typeparam name="TMiniApp"></typeparam>
    public static void MapMiniApp<TMiniApp>(this IEndpointRouteBuilder routeBuilder)
        where TMiniApp : IMiniApp, new()
    {
        var app = new TMiniApp();
        app.Map(routeBuilder);
    }

    /// <summary>
    /// 映射 Api 应用组
    /// </summary>
    /// <typeparam name="TMiniGroup"></typeparam>
    public static void MapMiniGroup<TMiniGroup>(this IEndpointRouteBuilder routeBuilder)
        where TMiniGroup : IMiniGroup, new()
    {
        var group = new TMiniGroup();
        group.Map(routeBuilder);
    }

    /// <summary>
    /// 映射 Api 应用模块
    /// </summary>
    /// <typeparam name="TMiniModule"></typeparam>
    public static void MapMiniModule<TMiniModule>(this IEndpointRouteBuilder routeBuilder)
        where TMiniModule : IMiniModule, new()
    {
        var module = new TMiniModule();
        module.Map(routeBuilder);
    }
}
