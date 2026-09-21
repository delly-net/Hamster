using Hamster.Api.Config;
using Hamster.Api.Services;

namespace Hamster.Api.Data;

/// <summary>
/// 默认管理员账户播种：首次启动时创建 <c>admin</c> 账户（已激活、系统管理员）。
/// </summary>
public static class AdminSeeder
{
    /// <summary>
    /// 按配置播种默认管理员。
    /// </summary>
    /// <param name="app">Web 应用实例。</param>
    /// <remarks>
    /// **只在同名用户不存在时创建**——若已存在则原样保留（含其密码），
    /// 否则「每次启动都重置管理员口令」会成为一个固定的后门口子。
    /// 失败仅记录告警，不阻断应用启动（与 <see cref="DatabaseInitializer"/> 一致）。
    /// </remarks>
    public static void SeedDefaultAdmin(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<AdminSeedOptions>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hamster.Api.Data");

        if (!options.Enabled)
        {
            logger.LogInformation(
                "默认管理员播种已关闭（置 {Section}:Enabled=true 或环境变量 {Env}=true 可开启）",
                AdminSeedOptions.SectionName,
                ConfigConst.ADMIN_SEED_ENABLED_ENV);
            return;
        }

        try
        {
            var users = app.Services.GetRequiredService<IUserService>();

            // 播种发生在启动阶段、无请求上下文，此处同步等待是安全的
            var existing = users.FindByUsernameAsync(options.Username).GetAwaiter().GetResult();
            if (existing is not null)
            {
                logger.LogInformation("默认管理员 {Username} 已存在，跳过播种", existing.Username);
                return;
            }

            var admin = users.CreateAdminAsync(options.Username, options.Password).GetAwaiter().GetResult();
            logger.LogInformation("已创建默认管理员账户：{Username}（系统管理员，已激活）", admin.Username);

            if (options.IsUsingDefaultPassword)
            {
                // 默认口令仅用于开箱启动，部署到任何可被访问的环境前必须更换
                logger.LogWarning(
                    "默认管理员正在使用默认口令「{Password}」，请尽快登录后重置密码，或改用环境变量 {Env} 指定初始口令",
                    ConfigConst.DEFAULT_ADMIN_PASSWORD,
                    ConfigConst.ADMIN_PASSWORD_ENV);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "默认管理员播种失败，应用继续启动；可登录后手动创建管理员，或检查数据库连接");
        }
    }
}
