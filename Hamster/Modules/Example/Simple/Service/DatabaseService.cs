using Microsoft.Data.Sqlite;

namespace Hamster.Modules.Example.Simple.Service;

/// <summary>
/// 数据库服务
/// </summary>
public interface IDatabaseService
{
    /// <summary>
    /// 获取数据库连接
    /// </summary>
    /// <returns>数据库连接</returns>
    SqliteConnection GetConnection();
}

/// <summary>
/// 数据库服务实现
/// </summary>
public class DatabaseService : IDatabaseService
{
    private readonly string _connectionString;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="connectionString">连接字符串</param>
    public DatabaseService(string connectionString)
    {
        _connectionString = connectionString;
        InitializeDatabase();
    }

    /// <summary>
    /// 初始化数据库
    /// </summary>
    private void InitializeDatabase()
    {
        using var connection = GetConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS todos (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT,
                DueBy TEXT,
                IsComplete INTEGER NOT NULL DEFAULT 0
            )";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 获取数据库连接
    /// </summary>
    /// <returns>数据库连接</returns>
    public SqliteConnection GetConnection()
    {
        return new SqliteConnection(_connectionString);
    }
}