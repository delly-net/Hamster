namespace Hamster.Modules.Example.Simple.Service;

using SqlSugar;
using Hamster.Modules.Example.Simple;
using Hamster.Modules.Example.Simple.Constant;

/// <summary>
/// Todo服务
/// </summary>
public static class TodoService
{
    /// <summary>
    /// 获取所有Todo
    /// </summary>
    /// <param name="db">数据库客户端</param>
    /// <returns>Todo列表</returns>
    public static List<Todo> GetAllTodos(ISqlSugarClient db)
    {
        return db.Queryable<Todo>().ToList();
    }

    /// <summary>
    /// 根据ID获取Todo
    /// </summary>
    /// <param name="db">数据库客户端</param>
    /// <param name="id">Todo ID</param>
    /// <returns>Todo对象，如果不存在则返回null</returns>
    public static Todo? GetTodoById(ISqlSugarClient db, int id)
    {
        return db.Queryable<Todo>().InSingle(id);
    }

    /// <summary>
    /// 创建Todo
    /// </summary>
    /// <param name="db">数据库客户端</param>
    /// <param name="todo">Todo对象</param>
    /// <returns>创建的Todo ID</returns>
    public static int CreateTodo(ISqlSugarClient db, Todo todo)
    {
        return db.Insertable(todo).ExecuteReturnIdentity();
    }

    /// <summary>
    /// 更新Todo
    /// </summary>
    /// <param name="db">数据库客户端</param>
    /// <param name="todo">Todo对象</param>
    /// <returns>影响的行数</returns>
    public static int UpdateTodo(ISqlSugarClient db, Todo todo)
    {
        return db.Updateable(todo).ExecuteCommand();
    }

    /// <summary>
    /// 删除Todo
    /// </summary>
    /// <param name="db">数据库客户端</param>
    /// <param name="id">Todo ID</param>
    /// <returns>影响的行数</returns>
    public static int DeleteTodo(ISqlSugarClient db, int id)
    {
        return db.Deleteable<Todo>().In(id).ExecuteCommand();
    }
}