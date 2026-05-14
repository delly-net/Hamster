namespace Hamster.Constant;

/// <summary>
/// 配置相关常量
/// </summary>
public static class ConfigConst
{
    /// <summary>
    /// 数据库连接字符串环境变量名
    /// </summary>
    public const string DB_CONNECTION_ENV = "DB_CONNECTION_STRING";

    /// <summary>
    /// 日志存储目录环境变量名
    /// </summary>
    public const string LOG_DIR_ENV = "LOG_DIR";

    /// <summary>
    /// 默认数据库连接字符串
    /// </summary>
    public const string DEFAULT_DB_CONNECTION = "Data Source=hamster.db";

    /// <summary>
    /// 默认日志目录
    /// </summary>
    public const string DEFAULT_LOG_DIR = "logs";

    /// <summary>
    /// 日志文件模板
    /// </summary>
    public const string LOG_FILE_TEMPLATE = "log-.txt";
}