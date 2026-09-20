namespace Hamster.Api.Config;

/// <summary>
/// 数据库配置项，对应 appsettings.json 的 <c>Database</c> 节点。
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Database";

    /// <summary>PostgreSQL 连接串。</summary>
    public string ConnectionString { get; set; } = ConfigConst.DEFAULT_CONNECTION_STRING;

    /// <summary>
    /// 是否在启动时执行 CodeFirst 自动建表。
    /// 默认关闭：无可用数据库时开启会导致启动失败。
    /// </summary>
    public bool AutoMigrate { get; set; }

    /// <summary>脱敏后的连接串，用于日志输出。</summary>
    public string MaskedConnectionString => ConfigConst.MaskConnectionString(ConnectionString);

    /// <summary>
    /// 从配置与环境变量解析数据库配置，环境变量优先。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <returns>数据库配置实例。</returns>
    public static DatabaseOptions From(IConfiguration configuration)
    {
        var options = new DatabaseOptions();
        configuration.GetSection(SectionName).Bind(options);

        var envConnectionString = Environment.GetEnvironmentVariable(ConfigConst.DB_CONNECTION_ENV);
        if (!string.IsNullOrWhiteSpace(envConnectionString))
        {
            options.ConnectionString = envConnectionString;
        }

        var envAutoMigrate = Environment.GetEnvironmentVariable(ConfigConst.DB_AUTOMIGRATE_ENV);
        if (bool.TryParse(envAutoMigrate, out var autoMigrate))
        {
            options.AutoMigrate = autoMigrate;
        }

        return options;
    }
}
