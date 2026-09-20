namespace Hamster.Api.Config;

/// <summary>
/// 框架级常量：环境变量名、默认值与连接串脱敏工具。
/// </summary>
public static class ConfigConst
{
    /// <summary>数据库连接串环境变量名（优先级高于 appsettings.json）。</summary>
    public const string DB_CONNECTION_ENV = "HAMSTER_DB_CONNECTION";

    /// <summary>自动建表开关环境变量名。</summary>
    public const string DB_AUTOMIGRATE_ENV = "HAMSTER_DB_AUTOMIGRATE";

    /// <summary>前端开发服务器地址，用于开发环境 CORS 放行。</summary>
    public const string FRONTEND_DEV_ORIGIN = "http://localhost:5173";

    /// <summary>开发环境 CORS 策略名。</summary>
    public const string DEV_CORS_POLICY = "hamster-dev";

    /// <summary>本地默认连接串（仅开发示例，生产环境请用环境变量覆盖）。</summary>
    public const string DEFAULT_CONNECTION_STRING =
        "Host=localhost;Port=5432;Database=hamster;Username=postgres;Password=postgres";

    /// <summary>
    /// 连接串脱敏：隐藏密码段，避免日志泄漏凭据。
    /// </summary>
    /// <param name="connectionString">原始连接串。</param>
    /// <returns>脱敏后的连接串。</returns>
    public static string MaskConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return string.Empty;
        }

        var segments = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var separatorIndex = segments[i].IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = segments[i][..separatorIndex].Trim();
            if (key.Equals("Password", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Pwd", StringComparison.OrdinalIgnoreCase))
            {
                segments[i] = $"{key}=***";
            }
        }

        return string.Join(';', segments);
    }
}
