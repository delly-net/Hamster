using Hamster.Api.Config;
using SqlSugar;

namespace Hamster.Api.Data;

/// <summary>
/// SqlSugar 数据访问层注册。
/// 采用 PostgreSQL（DbType.PostgreSQL），底层由 Npgsql 驱动连接。
/// </summary>
public static class SqlSugarSetup
{
    /// <summary>
    /// 注册 <see cref="ISqlSugarClient"/>。
    /// 按 SqlSugar 官方建议使用 <see cref="SqlSugarScope"/> 单例：其内部按异步上下文隔离连接，可安全并发使用。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configuration">应用配置。</param>
    /// <returns>服务集合，便于链式调用。</returns>
    public static IServiceCollection AddHamsterDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var options = DatabaseOptions.From(configuration);

        services.AddSingleton(options);
        services.AddSingleton<ISqlSugarClient>(provider =>
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Hamster.Api.Data");

            var scope = new SqlSugarScope(
                new ConnectionConfig
                {
                    ConnectionString = options.ConnectionString,
                    DbType = DbType.PostgreSQL,
                    IsAutoCloseConnection = true,
                    InitKeyType = InitKeyType.Attribute,
                },
                db =>
                {
                    db.Aop.OnLogExecuting = (sql, _) => logger.LogDebug("执行 SQL：{Sql}", sql);
                    db.Aop.OnError = ex => logger.LogError(ex, "SQL 执行异常");
                });

            logger.LogInformation(
                "数据库已注册：{DbType}，连接串（已脱敏）：{ConnectionString}",
                DbType.PostgreSQL,
                options.MaskedConnectionString);

            return scope;
        });

        return services;
    }
}
