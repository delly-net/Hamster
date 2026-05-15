using Hamster.Modules.Example.Simple.Entities;
using Hamster.Modules.Kernel.Data.Services;

namespace Hamster.Modules.Example.Simple.Services;

/// <summary>
/// Todo服务
/// </summary>
public static class TodoService
{
    /// <summary>
    /// 获取所有Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <returns>Todo列表</returns>
    public static List<Todo> GetAllTodos(IDatabaseService databaseService)
    {
        var todo = Todo.Named.Instance;
        var r = Todo.Defined.Instance;
        var t = new Todo.Named("t");
        var sql = @$"
            SELECT
                {t.Id} AS {r.Id},
                {t.Title} AS {r.Title},
                {t.DueBy} AS {r.DueBy},
                {t.IsComplete} AS {r.IsComplete}
            FROM {todo} {t}
        ";

        using var connection = databaseService.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var todos = new List<Todo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            todos.Add(MapTodo(reader));
        }
        return todos;
    }

    /// <summary>
    /// 根据ID获取Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="id">Todo ID</param>
    /// <returns>Todo对象，如果不存在则返回null</returns>
    public static Todo? GetTodoById(IDatabaseService databaseService, int id)
    {
        var todo = Todo.Named.Instance;
        var r = Todo.Defined.Instance;
        var t = new Todo.Named("t");
        var sql = @$"
            SELECT
                {t.Id} AS {r.Id},
                {t.Title} AS {r.Title},
                {t.DueBy} AS {r.DueBy},
                {t.IsComplete} AS {r.IsComplete}
            FROM {todo} {t}
            WHERE {t.Id} = @Id
        ";

        using var connection = databaseService.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@Id";
        parameter.Value = id;
        command.Parameters.Add(parameter);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return MapTodo(reader);
        }
        return null;
    }

    /// <summary>
    /// 创建Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="todo">Todo对象</param>
    /// <returns>创建的Todo ID</returns>
    public static int CreateTodo(IDatabaseService databaseService, Todo todo)
    {
        var tn = Todo.Named.Instance;
        var td = Todo.Defined.Instance;
        var sql = @$"
            INSERT INTO {tn} ({tn.Title}, {tn.DueBy}, {tn.IsComplete})
            VALUES (@{td.Title}, @{td.DueBy}, @{td.IsComplete});
            SELECT last_insert_rowid();";

        using var connection = databaseService.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var titleParam = command.CreateParameter();
        titleParam.ParameterName = $"@{td.Title}";
        titleParam.Value = (object?)todo.Title ?? DBNull.Value;
        command.Parameters.Add(titleParam);

        var dueByParam = command.CreateParameter();
        dueByParam.ParameterName = $"@{td.DueBy}";
        dueByParam.Value = todo.DueBy.HasValue ? (object)todo.DueBy.Value.ToString("O") : DBNull.Value;
        command.Parameters.Add(dueByParam);

        var isCompleteParam = command.CreateParameter();
        isCompleteParam.ParameterName = $"@{td.IsComplete}";
        isCompleteParam.Value = todo.IsComplete ? 1 : 0;
        command.Parameters.Add(isCompleteParam);

        var result = command.ExecuteScalar();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    /// <summary>
    /// 更新Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="todo">Todo对象</param>
    /// <returns>影响的行数</returns>
    public static int UpdateTodo(IDatabaseService databaseService, Todo todo)
    {
        var tn = Todo.Named.Instance;
        var td = Todo.Defined.Instance;
        var sql = @$"
            UPDATE {tn} SET {tn.Title} = @{td.Title}, {tn.DueBy} = @{td.DueBy}, {tn.IsComplete} = @{td.IsComplete}
            WHERE {tn.Id} = @{td.Id}";

        using var connection = databaseService.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var idParam = command.CreateParameter();
        idParam.ParameterName = $"@{td.Id}";
        idParam.Value = todo.Id;
        command.Parameters.Add(idParam);

        var titleParam = command.CreateParameter();
        titleParam.ParameterName = $"@{td.Title}";
        titleParam.Value = (object?)todo.Title ?? DBNull.Value;
        command.Parameters.Add(titleParam);

        var dueByParam = command.CreateParameter();
        dueByParam.ParameterName = $"@{td.DueBy}";
        dueByParam.Value = todo.DueBy.HasValue ? (object)todo.DueBy.Value.ToString("O") : DBNull.Value;
        command.Parameters.Add(dueByParam);

        var isCompleteParam = command.CreateParameter();
        isCompleteParam.ParameterName = $"@{td.IsComplete}";
        isCompleteParam.Value = todo.IsComplete ? 1 : 0;
        command.Parameters.Add(isCompleteParam);

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除Todo
    /// </summary>
    /// <param name="databaseService">数据库服务</param>
    /// <param name="id">Todo ID</param>
    /// <returns>影响的行数</returns>
    public static int DeleteTodo(IDatabaseService databaseService, int id)
    {
        var tn = Todo.Named.Instance;
        var td = Todo.Defined.Instance;
        var sql = @$"DELETE FROM {tn} WHERE {tn.Id} = @{td.Id}";

        using var connection = databaseService.GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = $"@{td.Id}";
        parameter.Value = id;
        command.Parameters.Add(parameter);

        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// 映射Reader到Todo对象
    /// </summary>
    private static Todo MapTodo(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var id = reader.GetInt32(0);
        var title = reader.IsDBNull(1) ? null : reader.GetString(1);
        var isComplete = reader.GetInt32(3) != 0;

        DateOnly? dueBy = null;
        if (!reader.IsDBNull(2))
        {
            var dateString = reader.GetString(2);
            dueBy = DateOnly.Parse(dateString);
        }

        return new Todo()
        {
            Id = id,
            Title = title,
            DueBy = dueBy,
            IsComplete = isComplete
        };
    }
}