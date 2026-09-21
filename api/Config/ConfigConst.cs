namespace Hamster.Api.Config;

/// <summary>
/// 框架级常量：环境变量名、默认值与连接串脱敏工具。
/// </summary>
public static class ConfigConst
{
    /// <summary>数据库类型环境变量名（取值为 Sqlite / PostgreSql，优先级高于 appsettings.json）。</summary>
    public const string DB_TYPE_ENV = "HAMSTER_DB_TYPE";

    /// <summary>数据库连接串环境变量名（优先级高于 appsettings.json）。</summary>
    public const string DB_CONNECTION_ENV = "HAMSTER_DB_CONNECTION";

    /// <summary>自动建表开关环境变量名。</summary>
    public const string DB_AUTOMIGRATE_ENV = "HAMSTER_DB_AUTOMIGRATE";

    /// <summary>JWT 签名密钥环境变量名；未配置时启动阶段随机生成。</summary>
    public const string JWT_KEY_ENV = "HAMSTER_JWT_KEY";

    /// <summary>默认管理员用户名环境变量名。</summary>
    public const string ADMIN_USERNAME_ENV = "HAMSTER_ADMIN_USERNAME";

    /// <summary>默认管理员密码环境变量名（生产部署务必覆盖）。</summary>
    public const string ADMIN_PASSWORD_ENV = "HAMSTER_ADMIN_PASSWORD";

    /// <summary>默认管理员播种开关环境变量名。</summary>
    public const string ADMIN_SEED_ENABLED_ENV = "HAMSTER_ADMIN_SEED_ENABLED";

    /// <summary>前端公开访问基址环境变量名，用于拼装密码重置链接。</summary>
    public const string PUBLIC_BASE_URL_ENV = "HAMSTER_PUBLIC_BASE_URL";

    /// <summary>默认管理员用户名。</summary>
    public const string DEFAULT_ADMIN_USERNAME = "admin";

    /// <summary>默认管理员密码（仅用于首次创建，生产环境必须覆盖或立即改密）。</summary>
    public const string DEFAULT_ADMIN_PASSWORD = "admin123";

    /// <summary>前端公开访问基址默认值（开发环境 Vite 开发服务器）。</summary>
    public const string DEFAULT_PUBLIC_BASE_URL = FRONTEND_DEV_ORIGIN;

    /// <summary>前端开发服务器地址，用于开发环境 CORS 放行。</summary>
    public const string FRONTEND_DEV_ORIGIN = "http://localhost:5173";

    /// <summary>开发环境 CORS 策略名。</summary>
    public const string DEV_CORS_POLICY = "hamster-dev";

    /// <summary>默认数据库类型：Sqlite，开箱即用、无需外部服务。</summary>
    public const string DEFAULT_DB_TYPE = "Sqlite";

    /// <summary>默认 Sqlite 数据库文件名，位于当前执行目录下。</summary>
    public const string DEFAULT_SQLITE_FILE_NAME = "hamster.db";

    /// <summary>默认 PostgreSQL 连接串（仅开发示例，生产环境请用环境变量覆盖）。</summary>
    public const string DEFAULT_POSTGRES_CONNECTION_STRING =
        "Host=localhost;Port=5432;Database=hamster;Username=postgres;Password=postgres";

    /// <summary>JWT 中承载用户主键的声明名。</summary>
    public const string CLAIM_USER_ID = "sub";

    /// <summary>JWT 中承载用户名的声明名。</summary>
    public const string CLAIM_USER_NAME = "name";

    /// <summary>
    /// 承载当前账套主键的请求头名。
    /// 账套刻意**不写入 JWT**：写进令牌后，管理员在账套管理页调整关联关系须等令牌过期（1 天）才对用户生效；
    /// 由请求头承载 + 后端逐请求回查，关联调整在下一次请求即生效。
    /// </summary>
    public const string ACCOUNT_SET_HEADER = "X-Account-Set-Id";

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
