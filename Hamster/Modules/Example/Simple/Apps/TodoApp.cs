namespace Hamster.Modules.Example.Simple.Controller;

using Hamster.Attributing;
using Hamster.Core;
using Hamster.Modules.Example.Simple;
using Hamster.Modules.Example.Simple.Constant;
using Hamster.Modules.Example.Simple.Entities;
using Hamster.Modules.Example.Simple.Services;
using Microsoft.AspNetCore.Mvc;

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
    [HttpGet("/get-all-todos")]
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
    [HttpGet("/get-todo-by-id/{id}")]
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
    [HttpPost("/create-todo")]
    public IResult CreateTodo(IDatabaseService databaseService, Todo todo)
    {
        var id = TodoService.CreateTodo(databaseService, todo);
        var createdTodo = new Todo()
        {
            Id = id,
            Title = todo.Title,
            DueBy = todo.DueBy,
            IsComplete = todo.IsComplete
        };
        return Results.Created($"/todo/{id}", createdTodo);
    }

    /// <summary>
    /// 更新Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="todo">Todo实体</param>
    [HttpPut("/update-todo")]
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
    [HttpDelete("/delete-todo/{id}")]
    public IResult DeleteTodo(IDatabaseService databaseService, int id)
    {
        TodoService.DeleteTodo(databaseService, id);
        return Results.NoContent();
    }
}