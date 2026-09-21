using Hamster.Api.Config;
using SqlSugar;

namespace Hamster.Api.Data;

/// <summary>
/// SqlSugar 数据访问层注册。
/// 支持 Sqlite 与 PostgreSQL 两种数据库，具体类型由 <see cref="DatabaseOptions.DbType"/> 决定。
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
        services.AddSingleton(provider => DatabaseOptions.From(
            configuration,
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Hamster.Api.Data")));

        services.AddSingleton<ISqlSugarClient>(provider =>
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Hamster.Api.Data");
            var options = provider.GetRequiredService<DatabaseOptions>();

            return new SqlSugarScope(
                new ConnectionConfig
                {
                    ConnectionString = options.ResolvedConnectionString,
                    DbType = ResolveDbType(options.DbType),
                    IsAutoCloseConnection = true,
                    InitKeyType = InitKeyType.Attribute,
                },
                db =>
                {
                    db.Aop.OnLogExecuting = (sql, _) => logger.LogDebug("执行 SQL：{Sql}", sql);
                    db.Aop.OnError = ex => logger.LogError(ex, "SQL 执行异常");
                });
        });

        return services;
    }

    /// <summary>把项目内的数据库类型映射为 SqlSugar 的 <see cref="DbType"/>。</summary>
    /// <param name="dbType">数据库类型。</param>
    /// <returns>SqlSugar 数据库类型。</returns>
    private static DbType ResolveDbType(HamsterDbType dbType) =>
        dbType == HamsterDbType.Sqlite ? DbType.Sqlite : DbType.PostgreSQL;
}
