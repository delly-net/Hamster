namespace Hamster.Api.Config;

/// <summary>
/// 默认管理员账户播种配置，对应 appsettings.json 的 <c>AdminSeed</c> 节点。
/// </summary>
/// <remarks>
/// 默认口令仅用于开箱启动，生产部署务必通过环境变量覆盖或启动后立即改密。
/// 播种**只在同名用户不存在时**创建，绝不覆盖已存在账户的密码。
/// </remarks>
public sealed class AdminSeedOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "AdminSeed";

    /// <summary>是否启用默认管理员播种，默认开启。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>默认管理员用户名。</summary>
    public string Username { get; set; } = ConfigConst.DEFAULT_ADMIN_USERNAME;

    /// <summary>默认管理员密码（仅用于首次创建）。</summary>
    public string Password { get; set; } = ConfigConst.DEFAULT_ADMIN_PASSWORD;

    /// <summary>
    /// 从配置与环境变量解析，环境变量优先。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <returns>播种配置实例。</returns>
    public static AdminSeedOptions From(IConfiguration configuration)
    {
        var options = new AdminSeedOptions();
        configuration.GetSection(SectionName).Bind(options);

        var envEnabled = Environment.GetEnvironmentVariable(ConfigConst.ADMIN_SEED_ENABLED_ENV);
        if (bool.TryParse(envEnabled, out var enabled))
        {
            options.Enabled = enabled;
        }

        var envUsername = Environment.GetEnvironmentVariable(ConfigConst.ADMIN_USERNAME_ENV);
        if (!string.IsNullOrWhiteSpace(envUsername))
        {
            options.Username = envUsername.Trim();
        }

        var envPassword = Environment.GetEnvironmentVariable(ConfigConst.ADMIN_PASSWORD_ENV);
        if (!string.IsNullOrWhiteSpace(envPassword))
        {
            options.Password = envPassword;
        }

        return options;
    }

    /// <summary>默认口令是否仍在使用（用于启动告警）。</summary>
    public bool IsUsingDefaultPassword =>
        string.Equals(Password, ConfigConst.DEFAULT_ADMIN_PASSWORD, StringComparison.Ordinal);
}
