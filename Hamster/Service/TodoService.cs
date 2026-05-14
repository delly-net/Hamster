using SqlSugar;
using Hamster.Constant;

namespace Hamster.Service;

/// <summary>
/// Todo 服务
/// </summary>
public interface ITodoService
{
    /// <summary>
    /// 获取所有 Todo
    /// </summary>
    /// <returns>Todo 列表</returns>
    Task<Todo[]> GetAllAsync();

    /// <summary>
    /// 根据 ID 获取 Todo
    /// </summary>
    /// <param name="id">Todo ID</param>
    /// <returns>Todo 实体或 null</returns>
    Task<Todo?> GetByIdAsync(int id);
}

/// <summary>
/// Todo 服务实现
/// </summary>
public class TodoService : ITodoService
{
    private readonly ISqlSugarClient _db;

    public TodoService(ISqlSugarClient db)
    {
        _db = db;
    }

    public async Task<Todo[]> GetAllAsync()
    {
        return await _db.Queryable<Todo>().ToArrayAsync();
    }

    public async Task<Todo?> GetByIdAsync(int id)
    {
        return await _db.Queryable<Todo>().InSingleAsync(id);
    }
}