using Hamster.Api.Config;
using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Data;

/// <summary>
/// 数据库初始化：按 <see cref="DatabaseOptions.AutoMigrate"/> 开关决定是否执行 CodeFirst 建表。
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// 执行数据库初始化。
    /// 建表失败（如连接不可用）时仅记录告警，不阻断应用启动。
    /// </summary>
    /// <param name="app">Web 应用实例。</param>
    public static void InitializeDatabase(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<DatabaseOptions>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hamster.Api.Data");

        if (!options.AutoMigrate)
        {
            logger.LogInformation(
                "自动建表已关闭，跳过 CodeFirst 初始化（置 Database:AutoMigrate=true 或环境变量 {Env}=true 可开启）",
                ConfigConst.DB_AUTOMIGRATE_ENV);
            return;
        }

        try
        {
            var db = app.Services.GetRequiredService<ISqlSugarClient>();
            db.CodeFirst.InitTables<SampleAccount, User>();
            logger.LogInformation("CodeFirst 自动建表完成：数据库类型 {DbType}，已就绪表 sample_account、hamster_user", options.DbTypeLabel);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "CodeFirst 自动建表失败，应用继续启动，请检查 {DbType} 连接配置是否可用",
                options.DbTypeLabel);
        }
    }
}
