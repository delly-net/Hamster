namespace Hamster.Modules.Example.Simple.Controller;

using Hamster.Modules.Example.Simple;
using Hamster.Modules.Example.Simple.Service;
using Hamster.Modules.Example.Simple.Constant;
using Hamster.Modules.Example.Simple.Services;
using Hamster.Attributing;

/// <summary>
/// Todo 应用
/// </summary>
[MiniApp("/todo")]
public sealed partial class TodoApp
{
    /// <summary>
    /// 获取所有Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    public IResult GetAllTodos(IDatabaseService databaseService)
    {
        var todos = TodoService.GetAllTodos(databaseService);
        return Results.Ok(todos);
    }

    /// <summary>
    /// 根据ID获取Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="id">Todo ID</param>
    public IResult GetTodoById(IDatabaseService databaseService, int id)
    {
        var todo = TodoService.GetTodoById(databaseService, id);
        if (todo is null)
        {
            return Results.NotFound();
        }
        return Results.Ok(todo);
    }

    /// <summary>
    /// 创建Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="todo">Todo实体</param>
    public IResult CreateTodo(IDatabaseService databaseService, Todo todo)
    {
        var id = TodoService.CreateTodo(databaseService, todo);
        var createdTodo = new Todo(id, todo.Title, todo.DueBy, todo.IsComplete);
        return Results.Created($"/todo/{id}", createdTodo);
    }

    /// <summary>
    /// 更新Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="todo">Todo实体</param>
    public IResult UpdateTodo(IDatabaseService databaseService, Todo todo)
    {
        TodoService.UpdateTodo(databaseService, todo);
        return Results.NoContent();
    }

    /// <summary>
    /// 删除Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="id">Todo ID</param>
    public IResult DeleteTodo(IDatabaseService databaseService, int id)
    {
        TodoService.DeleteTodo(databaseService, id);
        return Results.NoContent();
    }
}