namespace Hamster.Config;

/// <summary>
/// 应用配置
/// </summary>
public class AppSettings
{
    /// <summary>
    /// 数据库配置
    /// </summary>
    public DatabaseConfig Database { get; set; } = new();

    /// <summary>
    /// 日志配置
    /// </summary>
    public LogConfig Logging { get; set; } = new();
}

/// <summary>
/// 数据库配置
/// </summary>
public class DatabaseConfig
{
    /// <summary>
    /// 连接字符串
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}

/// <summary>
/// 日志配置
/// </summary>
public class LogConfig
{
    /// <summary>
    /// 日志目录
    /// </summary>
    public string Directory { get; set; } = string.Empty;
}