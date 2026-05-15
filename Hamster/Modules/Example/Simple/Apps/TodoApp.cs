namespace Hamster.Modules.Example.Simple.Controller;

using Hamster.Modules.Example.Simple;
using Hamster.Modules.Example.Simple.Service;
using Hamster.Modules.Example.Simple.Constant;
using Hamster.Core;
using Hamster.Modules.Example.Simple.Services;

/// <summary>
/// Todo 应用
/// </summary>
[MiniApp("/todo")]
public sealed partial class TodoApp
{
    /// <summary>
    /// 获取所有Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public IResult GetAllTodos(IDatabaseService databaseService)
    {
        var todos = TodoService.GetAllTodos(databaseService);
        return Results.Ok(todos);
    }

    /// <summary>
    /// 获取所有Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public static void MapGetAllTodos(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(SimpleApiPathConst.GET_ALL_TODOS, (IDatabaseService databaseService) =>
        {
            var todos = TodoService.GetAllTodos(databaseService);
            return Results.Ok(todos);
        });
    }

    /// <summary>
    /// 根据ID获取Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public static void MapGetTodoById(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(SimpleApiPathConst.GET_TODO_BY_ID, (IDatabaseService databaseService, int id) =>
        {
            var todo = TodoService.GetTodoById(databaseService, id);
            if (todo is null)
            {
                return Results.NotFound();
            }
            return Results.Ok(todo);
        });
    }

    /// <summary>
    /// 创建Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public static void MapCreateTodo(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPost(SimpleApiPathConst.CREATE_TODO, (IDatabaseService databaseService, Todo todo) =>
        {
            var id = TodoService.CreateTodo(databaseService, todo);
            var createdTodo = new Todo(id, todo.Title, todo.DueBy, todo.IsComplete);
            return Results.Created($"/todo/{id}", createdTodo);
        });
    }

    /// <summary>
    /// 更新Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public static void MapUpdateTodo(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPut(SimpleApiPathConst.UPDATE_TODO, (IDatabaseService databaseService, Todo todo) =>
        {
            TodoService.UpdateTodo(databaseService, todo);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// 删除Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public static void MapDeleteTodo(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapDelete(SimpleApiPathConst.DELETE_TODO, (IDatabaseService databaseService, int id) =>
        {
            TodoService.DeleteTodo(databaseService, id);
            return Results.NoContent();
        });
    }
}