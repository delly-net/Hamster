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

    /// <summary>账户表表名（与 <see cref="Account"/> 上的 <c>SugarTable</c> 保持一致）。</summary>
    private const string ACCOUNT_TABLE = "hamster_account";

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
            // 分两次调用：SqlSugar 的 InitTables 范型重载最多只到 5 个类型参数
            db.CodeFirst.InitTables<SampleAccount, User, AccountSet, AccountSetMember, Account>();
            db.CodeFirst.InitTables<Transaction, TransactionEntry, Currency, Category>();
            logger.LogInformation(
                "CodeFirst 自动建表完成：数据库类型 {DbType}，已就绪表 sample_account、hamster_user、hamster_account_set、hamster_account_set_member、hamster_account、hamster_transaction、hamster_transaction_entry、hamster_currency、hamster_category",
                options.DbTypeLabel);

            // 播种**必须先于账户币种回填**：回填要用默认币种代码，而默认币种正是播种时标出来的
            var seeded = CurrencySeeder.SeedIfEmpty(db, logger);
            if (seeded > 0)
            {
                logger.LogInformation(
                    "已写入 {Count} 个常见币种，默认币种为 {Code}",
                    seeded,
                    CurrencySeeder.DefaultCode);
            }

            BackfillUserFlags(db, options.DbType, logger);
            BackfillAccountFlags(db, options.DbType, logger);
            BackfillAccountCurrency(db, options.DbType, logger);

            // 分类**刻意不播种、也不回填**：
            // - 不播种：分类是各家的业务语义（「餐饮」在两个账套里覆盖的范围可以完全不同），
            //   不存在「所有人都需要的那几个」这种共识（币种有，故 CurrencySeeder 成立）；
            //   且播种后用户删掉的分类会在下次启动被塞回来，管理页的改动形同虚设。
            //   空字典 + 记账时手工输入自动创建，正好覆盖「开箱可用」。
            // - 不回填：hamster_transaction.category_id 是可空 int，既有行取到 NULL 正是
            //   「未分类」这一合法语义（见 Transaction.CategoryId）。上面三处回填之所以必需，
            //   是因为那些列是非空 bool / 非空 string，NULL 会让实体绑定失败而整个列表查询 500。
            //   勿照先例给本列补一段无用的 UPDATE。
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
    /// **既有账号一律视为「非管理员 + 未激活」**——需由管理员在用户管理页激活后才能登录。
    /// </remarks>
    private static void BackfillUserFlags(ISqlSugarClient db, HamsterDbType dbType, ILogger logger)
    {
        var affected = BackfillBoolColumns(db, dbType, USER_TABLE, ["is_admin", "is_active"]);

        if (affected > 0)
        {
            logger.LogInformation(
                "已回填 {Count} 处历史用户标志（既有账号一律为「非管理员 + 未激活」，需管理员在用户管理页激活）",
                affected);
        }
    }

    /// <summary>
    /// 回填既有账户行的布尔标志列。
    /// </summary>
    /// <param name="db">SqlSugar 客户端。</param>
    /// <param name="dbType">当前数据库类型，决定「不可绑定的空值」如何判定。</param>
    /// <param name="logger">日志记录器。</param>
    /// <remarks>
    /// **既有账户一律视为「非系统账户」**：<c>is_system</c> 是随本任务才引入的列，
    /// 升级前存在的账户不可能有系统账户，而期初账本账户由期初余额入账时按需创建。
    /// 不补这一步的话，<c>is_system</c> 为 NULL 会让账户列表查询因无法绑定到非空 <c>bool</c> 而 500。
    /// </remarks>
    private static void BackfillAccountFlags(ISqlSugarClient db, HamsterDbType dbType, ILogger logger)
    {
        var affected = BackfillBoolColumns(db, dbType, ACCOUNT_TABLE, ["is_system"]);

        if (affected > 0)
        {
            logger.LogInformation(
                "已回填 {Count} 处历史账户标志（既有账户一律为「非系统账户」，期初账本账户在期初余额入账时按需创建）",
                affected);
        }
    }

    /// <summary>
    /// 回填既有账户行的币种列。
    /// </summary>
    /// <param name="db">SqlSugar 客户端。</param>
    /// <param name="dbType">当前数据库类型，决定「不可绑定的空值」如何判定。</param>
    /// <param name="logger">日志记录器。</param>
    /// <remarks>
    /// **既有账户一律回填为默认币种**：<c>currency_code</c> 是随币种绑定才引入的列，
    /// 升级前存在的账户没有币种信息，而「账户必须有币种」是记账端点的硬约束
    /// （跨币种无法交易），留空会让这些账户在记账时被自己的校验挡住。
    /// <para>
    /// 回填成默认币种而非某个写死的代码：默认币种可由管理员改，回填要跟随当时生效的取值。
    /// 也正因如此，<see cref="CurrencySeeder.SeedIfEmpty"/> 必须先于本方法执行。
    /// </para>
    /// </remarks>
    private static void BackfillAccountCurrency(ISqlSugarClient db, HamsterDbType dbType, ILogger logger)
    {
        const string column = "currency_code";
        var affected = db.Ado.ExecuteCommand(
            $"UPDATE {ACCOUNT_TABLE} SET {column} = '{CurrencySeeder.DefaultCode}' " +
            $"WHERE {UnbindableWhere(dbType, column)}");

        if (affected > 0)
        {
            logger.LogInformation(
                "已把 {Count} 个历史账户的币种回填为默认币种 {Code}",
                affected,
                CurrencySeeder.DefaultCode);
        }
    }

    /// <summary>
    /// 拼出「该列取值无法绑定到实体上的非空类型」的判定条件。
    /// </summary>
    /// <param name="dbType">当前数据库类型。</param>
    /// <param name="column">目标列名。</param>
    /// <returns>可直接嵌入 WHERE 的判定片段。</returns>
    /// <remarks>
    /// 待回填的取值有两种形态：<c>NULL</c>（Sqlite 增量加列的实测结果）与空串 <c>''</c>
    /// （由带 <c>DefaultValue</c> 标注的中间版本升级出的库，实测于 hamster.db）。两者都无法绑定到
    /// 非空的 <c>bool</c> / <c>string</c>，故一并处理。空串判定仅对 Sqlite 生效：
    /// PostgreSQL 的 <c>boolean</c> 列不可能是空串，且 <c>boolean = ''</c> 会直接报类型错误。
    /// </remarks>
    private static string UnbindableWhere(HamsterDbType dbType, string column) => dbType == HamsterDbType.Sqlite
        ? $"{column} IS NULL OR {column} = ''"
        : $"{column} IS NULL";

    /// <summary>
    /// 把指定表的布尔标志列中「无法绑定到非空 <c>bool</c>」的历史取值回填为 <c>false</c>。
    /// </summary>
    /// <param name="db">SqlSugar 客户端。</param>
    /// <param name="dbType">当前数据库类型，决定「不可绑定的空值」如何判定。</param>
    /// <param name="table">目标表名。</param>
    /// <param name="columns">目标列名。</param>
    /// <returns>受影响的行数合计。</returns>
    /// <remarks>
    /// SqlSugar 的增量加列只把新列追加为**可空**，不会为既有行补值（即使实体上标了 <c>DefaultValue</c>），
    /// 于是升级前已存在的行在这些列上是 NULL；读回时无法绑定到非空 <c>bool</c>，
    /// 会让整个列表查询抛异常。
    /// <para>
    /// 哪些取值算「不可绑定」见 <see cref="UnbindableWhere"/>。
    /// </para>
    /// <para>
    /// 回填用 ANSI 的 <c>false</c> 而非 <c>0</c>，以便 Sqlite 与 PostgreSQL 两种库都能执行。
    /// </para>
    /// </remarks>
    private static int BackfillBoolColumns(
        ISqlSugarClient db,
        HamsterDbType dbType,
        string table,
        IReadOnlyList<string> columns)
    {
        var affected = 0;
        foreach (var column in columns)
        {
            affected += db.Ado.ExecuteCommand(
                $"UPDATE {table} SET {column} = false WHERE {UnbindableWhere(dbType, column)}");
        }

        return affected;
    }
}
