namespace Hamster.Modules.Example.Simple;

using Hamster.Modules.Example.Simple.Controller;

/// <summary>
/// Simple 分类路由器
/// </summary>
public static class SimpleRouter
{
    /// <summary>
    /// 注册Simple分类的所有路由
    /// </summary>
    /// <param name="app">Web应用</param>
    public static void Register(WebApplication app)
    {
        var group = app.MapGroup("/example/simple/todo");
        TodoController.MapGetAllTodos(group);
        TodoController.MapGetTodoById(group);
        TodoController.MapCreateTodo(group);
        TodoController.MapUpdateTodo(group);
        TodoController.MapDeleteTodo(group);
    }
}