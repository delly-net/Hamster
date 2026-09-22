using Hamster.Api.Config;
using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Data;

/// <summary>
/// 数据库初始化：按 <see cref="DatabaseOptions.AutoMigrate"/> 开关决定是否执行 CodeFirst 建表。
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>用户表表名（与 <see cref="User"/> 上的 <c>SugarTable</c> 保持一致）。</summary>
    private const string USER_TABLE = "hamster_user";

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
            db.CodeFirst.InitTables<SampleAccount, User, AccountSet, AccountSetMember, Account>();
            logger.LogInformation(
                "CodeFirst 自动建表完成：数据库类型 {DbType}，已就绪表 sample_account、hamster_user、hamster_account_set、hamster_account_set_member、hamster_account",
                options.DbTypeLabel);

            BackfillUserFlags(db, options.DbType, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "CodeFirst 自动建表失败，应用继续启动，请检查 {DbType} 连接配置是否可用",
                options.DbTypeLabel);
        }
    }

    /// <summary>
    /// 回填既有用户的布尔标志列。
    /// </summary>
    /// <param name="db">SqlSugar 客户端。</param>
    /// <param name="dbType">当前数据库类型，决定「不可绑定的空值」如何判定。</param>
    /// <param name="logger">日志记录器。</param>
    /// <remarks>
    /// SqlSugar 的增量加列只把新列追加为**可空**，不会为既有行补值（即使实体上标了 <c>DefaultValue</c>），
    /// 于是升级前已存在的用户在 <c>is_admin</c> / <c>is_active</c> 上是 NULL；读回时无法绑定到非空 <c>bool</c>，
    /// 会让整个用户列表查询抛异常。这里显式回填一次：
    /// **既有账号一律视为「非管理员 + 未激活」**——需由管理员在用户管理页激活后才能登录。
    /// <para>
    /// 待回填的取值有两种形态：<c>NULL</c>（Sqlite 增量加列的实测结果）与空串 <c>''</c>
    /// （由带 <c>DefaultValue</c> 标注的中间版本升级出的库，实测于 hamster.db）。两者都无法绑定到
    /// <c>bool</c>，故一并处理。空串判定仅对 Sqlite 生效：PostgreSQL 的 <c>boolean</c> 列不可能是空串，
    /// 且 <c>boolean = ''</c> 会直接报类型错误。
    /// </para>
    /// <para>
    /// 回填用 ANSI 的 <c>false</c> 而非 <c>0</c>，以便 Sqlite 与 PostgreSQL 两种库都能执行。
    /// </para>
    /// </remarks>
    private static void BackfillUserFlags(ISqlSugarClient db, HamsterDbType dbType, ILogger logger)
    {
        var affected = 0;
        foreach (var column in new[] { "is_admin", "is_active" })
        {
            var where = dbType == HamsterDbType.Sqlite
                ? $"{column} IS NULL OR {column} = ''"
                : $"{column} IS NULL";

            affected += db.Ado.ExecuteCommand(
                $"UPDATE {USER_TABLE} SET {column} = false WHERE {where}");
        }

        if (affected > 0)
        {
            logger.LogInformation(
                "已回填 {Count} 处历史用户标志（既有账号一律为「非管理员 + 未激活」，需管理员在用户管理页激活）",
                affected);
        }
    }
}
