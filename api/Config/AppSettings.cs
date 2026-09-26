namespace Hamster.Api.Config;

/// <summary>
/// 支持的数据库类型。
/// </summary>
public enum HamsterDbType
{
    /// <summary>Sqlite：默认类型，单文件、零外部依赖。</summary>
    Sqlite,

    /// <summary>PostgreSQL：需要外部实例，连接串通过配置或环境变量提供。</summary>
    PostgreSql,
}

/// <summary>
/// 数据库配置项，对应 appsettings.json 的 <c>Database</c> 节点。
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Database";

    private static readonly string[] SqliteAliases = ["sqlite", "sqlite3"];
    private static readonly string[] PostgreSqlAliases = ["postgresql", "postgres", "pgsql", "npgsql"];

    /// <summary>数据库类型，默认 Sqlite。</summary>
    public HamsterDbType DbType { get; set; } = HamsterDbType.Sqlite;

    /// <summary>
    /// 数据库连接串。留空时按 <see cref="DbType"/> 生成默认连接串
    /// （Sqlite 指向当前执行目录下的 hamster.db）。
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// 是否在启动时执行 CodeFirst 自动建表，默认开启。
    /// </summary>
    /// <remarks>
    /// 建表失败**不阻断应用启动**，且初始化各步**互相隔离**——某一步失败只记 ERROR、
    /// 不影响其余步骤（见 <c>DatabaseInitializer.RunStep</c>）。
    /// </remarks>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>数据库类型标签，用于日志与健康探针输出。</summary>
    public string DbTypeLabel => DbType == HamsterDbType.Sqlite ? "sqlite" : "postgresql";

    /// <summary>最终生效的连接串：显式配置优先，缺省时按数据库类型生成。</summary>
    public string ResolvedConnectionString =>
        string.IsNullOrWhiteSpace(ConnectionString) ? BuildDefaultConnectionString() : ConnectionString;

    /// <summary>脱敏后的连接串，用于日志输出。</summary>
    public string MaskedConnectionString => ConfigConst.MaskConnectionString(ResolvedConnectionString);

    /// <summary>Sqlite 数据库文件位置；非 Sqlite 或无法解析时为 <c>null</c>。</summary>
    public string? SqliteFilePath
    {
        get
        {
            if (DbType != HamsterDbType.Sqlite)
            {
                return null;
            }

            foreach (var segment in ResolvedConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separatorIndex = segment.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = segment[..separatorIndex].Trim();
                if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("DataSource", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
                {
                    return segment[(separatorIndex + 1)..].Trim();
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 从配置与环境变量解析数据库配置，环境变量优先。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <param name="logger">日志记录器，用于提示无法识别的数据库类型。</param>
    /// <returns>数据库配置实例。</returns>
    public static DatabaseOptions From(IConfiguration configuration, ILogger logger)
    {
        var options = new DatabaseOptions();
        configuration.GetSection(SectionName).Bind(options);

        var envDbType = Environment.GetEnvironmentVariable(ConfigConst.DB_TYPE_ENV);
        if (!string.IsNullOrWhiteSpace(envDbType))
        {
            options.DbType = ParseDbType(envDbType, logger);
        }

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

    /// <summary>
    /// 解析数据库类型字面量，无法识别时回落到默认类型并记录告警。
    /// </summary>
    /// <param name="value">数据库类型字面量。</param>
    /// <param name="logger">日志记录器。</param>
    /// <returns>解析结果。</returns>
    private static HamsterDbType ParseDbType(string value, ILogger logger)
    {
        var normalized = value.Trim().ToLowerInvariant();

        if (SqliteAliases.Contains(normalized))
        {
            return HamsterDbType.Sqlite;
        }

        if (PostgreSqlAliases.Contains(normalized))
        {
            return HamsterDbType.PostgreSql;
        }

        logger.LogWarning(
            "无法识别的数据库类型 {Value}（来自 {Env}），已回落到默认类型 {Default}；可选值：Sqlite、PostgreSql",
            value,
            ConfigConst.DB_TYPE_ENV,
            ConfigConst.DEFAULT_DB_TYPE);

        return HamsterDbType.Sqlite;
    }

    /// <summary>按当前数据库类型生成默认连接串。</summary>
    private string BuildDefaultConnectionString()
    {
        if (DbType == HamsterDbType.PostgreSql)
        {
            return ConfigConst.DEFAULT_POSTGRES_CONNECTION_STRING;
        }

        var databasePath = Path.Combine(Directory.GetCurrentDirectory(), ConfigConst.DEFAULT_SQLITE_FILE_NAME);
        return $"DataSource={databasePath}";
    }
}
