namespace Hamster.Modules.Example;

/// <summary>
/// Example 模块路由器
/// </summary>
public static class ExampleRouter
{
    /// <summary>
    /// 注册Example模块的所有路由
    /// </summary>
    /// <param name="app">Web应用</param>
    public static void Register(WebApplication app)
    {
        Simple.SimpleRouter.Register(app);
    }
}