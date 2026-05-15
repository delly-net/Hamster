using Hamster.Core;
using Hamster.Modules.Example.Simple.Constant;
using Hamster.Modules.Example.Simple.Controller;
using Hamster.Modules.Example.Simple.Services;

namespace Hamster.Modules.Example.Simple.Endpoints;

/// <summary>
/// ToDo Endpoint
/// </summary>
public partial class ToDoEndpoint : IApiEndpoint
{
    /// <summary>
    /// 路由注册
    /// </summary>
    /// <param name="app"></param>
    public void Map(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet("/get-all-todos", (IDatabaseService databaseService) =>
        {
            var controller = new TodoApp();
            return controller.GetAllTodos(databaseService);
        });
    }
}
