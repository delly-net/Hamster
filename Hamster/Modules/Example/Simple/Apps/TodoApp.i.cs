namespace Hamster.Modules.Example.Simple.Controller;

using Hamster.Modules.Example.Simple;
using Hamster.Modules.Example.Simple.Constant;
using Hamster.Core;
using Hamster.Modules.Example.Simple.Services;

/// <summary>
/// Todo 控制器
/// </summary>
public sealed partial class TodoApp : IMiniApp
{
    /// <summary>
    /// 路由注册
    /// </summary>
    /// <param name="app"></param>
    public void Map(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/todo");
        group.MapGet("/get-all-todos", (IDatabaseService databaseService) =>
        {
            var controller = new TodoApp();
            return controller.GetAllTodos(databaseService);
        });
    }
}