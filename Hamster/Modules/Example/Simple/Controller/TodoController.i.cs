namespace Hamster.Modules.Example.Simple.Controller;

using Hamster.Modules.Example.Simple;
using Hamster.Modules.Example.Simple.Service;
using Hamster.Modules.Example.Simple.Constant;
using Hamster.Core;

/// <summary>
/// Todo 控制器
/// </summary>
public sealed partial class TodoController : IAotController
{
    /// <summary>
    /// 路由注册
    /// </summary>
    /// <param name="app"></param>
    public void RouteRegister(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(SimpleApiPathConst.GET_ALL_TODOS, (IDatabaseService databaseService) =>
        {
            var controller = new TodoController();
            return controller.GetAllTodos(databaseService);
        });
    }
}