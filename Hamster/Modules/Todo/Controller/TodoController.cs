using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Hamster.Modules.Todo;
using Hamster.Modules.Todo.Constant;
using Hamster.Modules.Todo.Service;

namespace Hamster.Modules.Todo.Controller;

/// <summary>
/// Todo 控制器
/// </summary>
public static class TodoController
{
    /// <summary>
    /// 获取所有 Todo
    /// </summary>
    /// <param name="todoService">Todo 服务</param>
    /// <returns>Todo 列表</returns>
    public static async Task<Ok<Todo[]>> GetAll(ITodoService todoService)
    {
        var todos = await todoService.GetAllAsync();
        return TypedResults.Ok(todos);
    }

    /// <summary>
    /// 根据 ID 获取 Todo
    /// </summary>
    /// <param name="id">Todo ID</param>
    /// <param name="todoService">Todo 服务</param>
    /// <returns>Todo 实体或 404</returns>
    public static async Task<Results<Ok<Todo>, NotFound>> GetById(int id, ITodoService todoService)
    {
        var todo = await todoService.GetByIdAsync(id);
        if (todo is null)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(todo);
    }

    /// <summary>
    /// 注册 Todo 路由
    /// </summary>
    /// <param name="app">Web 应用</param>
    public static void RegisterRoutes(IEndpointRouteBuilder app)
    {
        var todosApi = app.MapGroup(TodoApiPathConst.TODOS);
        todosApi.MapGet("/", GetAll)
                .WithName(TodoApiNameConst.GET_TODOS);

        todosApi.MapGet(TodoApiPathConst.TODO_BY_ID, GetById)
                .WithName(TodoApiNameConst.GET_TODO_BY_ID);
    }
}