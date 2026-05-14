namespace Hamster.Modules.Example.Simple.Controller;

using SqlSugar;
using Hamster.Modules.Example.Simple;
using Hamster.Modules.Example.Simple.Service;
using Hamster.Modules.Example.Simple.Constant;

/// <summary>
/// Todo 控制器
/// </summary>
public static class TodoController
{
    /// <summary>
    /// 获取所有Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public static void MapGetAllTodos(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(SimpleApiPathConst.GET_ALL_TODOS, (ISqlSugarClient db) =>
        {
            var todos = TodoService.GetAllTodos(db);
            return Results.Ok(todos);
        });
    }

    /// <summary>
    /// 根据ID获取Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public static void MapGetTodoById(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGet(SimpleApiPathConst.GET_TODO_BY_ID, (ISqlSugarClient db, int id) =>
        {
            var todo = TodoService.GetTodoById(db, id);
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
        routeBuilder.MapPost(SimpleApiPathConst.CREATE_TODO, (ISqlSugarClient db, Todo todo) =>
        {
            var id = TodoService.CreateTodo(db, todo);
            var createdTodo = new Todo
            {
                Id = id,
                Title = todo.Title,
                DueBy = todo.DueBy,
                IsComplete = todo.IsComplete
            };
            return Results.Created($"/todo/{id}", createdTodo);
        });
    }

    /// <summary>
    /// 更新Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public static void MapUpdateTodo(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPut(SimpleApiPathConst.UPDATE_TODO, (ISqlSugarClient db, Todo todo) =>
        {
            TodoService.UpdateTodo(db, todo);
            return Results.NoContent();
        });
    }

    /// <summary>
    /// 删除Todo
    /// </summary>
    /// <param name="routeBuilder">路由构建器</param>
    public static void MapDeleteTodo(IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapDelete(SimpleApiPathConst.DELETE_TODO, (ISqlSugarClient db, int id) =>
        {
            TodoService.DeleteTodo(db, id);
            return Results.NoContent();
        });
    }
}