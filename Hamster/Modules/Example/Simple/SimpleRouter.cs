namespace Hamster.Modules.Example.Simple;

using Hamster.Core;
using Hamster.Modules.Example.Simple.Controller;
using Hamster.Modules.Example.Simple.Endpoints;

/// <summary>
/// Simple 分类路由器
/// </summary>
public static class SimpleRouter
{
    /// <summary>
    /// 注册Simple分类的所有路由
    /// </summary>
    /// <param name="app">Web应用</param>
    public static void Register(IEndpointRouteBuilder routeBuilder)
    {
        var group = routeBuilder.MapGroup("/simple");
        //group.MapApiEndpoints<ToDoEndpoint>();
        group.MapApiApp<TodoApp>();
        //var todoController = new TodoController();
        //todoController.RouteRegister(group);
        // TodoController.MapGetAllTodos(group);
        TodoApp.MapGetTodoById(group);
        TodoApp.MapCreateTodo(group);
        TodoApp.MapUpdateTodo(group);
        TodoApp.MapDeleteTodo(group);
    }
}