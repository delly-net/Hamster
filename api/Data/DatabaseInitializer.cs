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

    /// <summary>交易表表名（与 <see cref="Transaction"/> 上的 <c>SugarTable</c> 保持一致）。</summary>
    private const string TRANSACTION_TABLE = "hamster_transaction";

    /// <summary>
    /// 全部实体，供**逐表**建表使用；顺序沿用「先账号体系、再账套账目、后交易、最后标签与结算」。
    /// </summary>
    /// <remarks>
    /// 用一份显式清单而不是四个 <c>InitTables&lt;…&gt;</c> 分组调用，理由有两条：
    /// <list type="number">
    /// <item>
    /// **分组只是绕开编译期限制**：SqlSugar 的泛型重载最多 5 个类型参数，14 张表因此被迫分四组。
    /// 改为逐表调用（<c>InitTables(Type)</c>）后这条限制自然消失——分组调用在库内部本就是
    /// 「for 每个类型各调一次 <c>InitTables(type)</c>」，逐表调用与它**做的是同一件事**，不是另起一套。
    /// </item>
    /// <item>
    /// **逐表才能把失败隔离开**：分组调用一抛异常，其后**所有组**都不再执行。本清单的顺序里，
    /// 交易组在标签组与结算组之前，于是「交易表加列失败」会连带标签表与结算表从未被建立，
    /// 而建表异常只被记成一条告警、应用照常启动——缺陷要到运行期才以「某张表不存在」的面目出现
    /// （PostgreSQL 部署上实际发生过的 <c>relation "hamster_tag" does not exist</c> 就是这么来的）。
    /// 逐表 + 每表各自 try/catch 之后，一张表失败**不再牵连任何其它表**。
    /// </item>
    /// </list>
    /// <para>
    /// 表之间没有外键约束（本项目一律用应用层不变量代替），故本顺序不含依赖含义，调整它是安全的。
    /// </para>
    /// </remarks>
    private static readonly Type[] TABLE_TYPES =
    [
        typeof(SampleAccount),
        typeof(User),
        typeof(AccountSet),
        typeof(AccountSetMember),
        typeof(Account),
        typeof(Transaction),
        typeof(TransactionEntry),
        typeof(Currency),
        typeof(Category),
        typeof(Tag),
        typeof(TransactionTag),
        typeof(SettlementTask),
        typeof(SettlementTransaction),
        typeof(SettlementEntry),
    ];

    /// <summary>
    /// 执行数据库初始化：建表 → 播种 → 回填，逐表（逐步骤）独立执行。
    /// **任何一步失败都只记录错误、不阻断应用启动，也不影响其余步骤**（见 <see cref="RunStep"/>）。
    /// </summary>
    /// <param name="app">Web 应用实例。</param>
    /// <remarks>
    /// 本方法写库的每一步都**互相隔离**：一处失败既不阻断启动，也不牵连其余步骤。
    /// 这条口径是「一次真实的运行期故障」换来的——原先 14 张表挤在四次分组调用里、连同播种与回填
    /// 共用一个 try/catch，PostgreSQL 上给既有交易表加非空列被拒后，标签表与结算表**从未被建立**，
    /// 而日志只有一条告警，直到用户点开标签页才以 <c>relation "hamster_tag" does not exist</c> 暴露。
    /// 隔离之后同类故障的后果被限制在「那一张表」上，且日志里点名道姓。
    /// </remarks>
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

        var db = app.Services.GetRequiredService<ISqlSugarClient>();
        // 失败清单：每步失败时记下「表名/步骤名」，最后统一报一条 ERROR，
        // 使「这次启动到底缺了什么」一眼可见，而不必去翻每步各自的堆栈。
        var failures = new List<string>();

        foreach (var entityType in TABLE_TYPES)
        {
            var tableName = ResolveTableName(db, entityType);
            RunStep(logger, failures, $"建表 {tableName}", () => db.CodeFirst.InitTables(entityType));
        }

        if (failures.Count == 0)
        {
            logger.LogInformation(
                "CodeFirst 自动建表完成：数据库类型 {DbType}，{Count} 张表已就绪：{Tables}",
                options.DbTypeLabel,
                TABLE_TYPES.Length,
                string.Join("、", TABLE_TYPES.Select(type => ResolveTableName(db, type))));
        }

        // 播种**必须先于账户币种回填**：回填要用默认币种代码，而默认币种正是播种时标出来的。
        // 顺序依赖只存在于这两步之间，故它们相邻；其余回填彼此无关。
        RunStep(logger, failures, "币种播种", () =>
        {
            var seeded = CurrencySeeder.SeedIfEmpty(db, logger);
            if (seeded > 0)
            {
                logger.LogInformation(
                    "已写入 {Count} 个常见币种，默认币种为 {Code}",
                    seeded,
                    CurrencySeeder.DefaultCode);
            }
        });

        RunStep(logger, failures, $"回填 {USER_TABLE} 标志列", () => BackfillUserFlags(db, options.DbType, logger));
        RunStep(logger, failures, $"回填 {ACCOUNT_TABLE} 标志列", () => BackfillAccountFlags(db, options.DbType, logger));
        RunStep(logger, failures, $"回填 {ACCOUNT_TABLE} 币种列", () => BackfillAccountCurrency(db, options.DbType, logger));
        RunStep(logger, failures, $"回填 {TRANSACTION_TABLE} 修改时间列", () => BackfillTransactionUpdatedAt(db, options.DbType, logger));

        // 分类**刻意不播种、也不回填**：
        // - 不播种：分类是各家的业务语义（「餐饮」在两个账套里覆盖的范围可以完全不同），
        //   不存在「所有人都需要的那几个」这种共识（币种有，故 CurrencySeeder 成立）；
        //   且播种后用户删掉的分类会在下次启动被塞回来，管理页的改动形同虚设。
        //   空字典 + 记账时手工输入自动创建，正好覆盖「开箱可用」。
        // - 不回填：hamster_transaction.category_id 是可空 int，既有行取到 NULL 正是
        //   「未分类」这一合法语义（见 Transaction.CategoryId）。上面几处回填之所以必需，
        //   是因为那些列是非空 bool / 非空 string，NULL 会让实体绑定失败而整个列表查询 500。
        //   勿照先例给本列补一段无用的 UPDATE。

        // 最后统一报一条：成功时报「全部就绪」，有失败时报 ERROR 并逐个点名，
        // 使运维只看这一条就知道本次启动到底缺了什么。
        if (failures.Count > 0)
        {
            logger.LogError(
                "{DbType} 数据库初始化有 {Count} 步失败（应用继续启动，但相应功能会在运行期报错）：{Failures}",
                options.DbTypeLabel,
                failures.Count,
                string.Join("；", failures));
        }
        else
        {
            logger.LogInformation("{DbType} 数据库初始化完成：建表、播种、回填全部成功", options.DbTypeLabel);
        }
    }

    /// <summary>
    /// 执行一个初始化步骤，失败时记下步骤名并记录错误日志，**不向上抛出**。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="failures">失败清单，失败时追加 <paramref name="stepName"/>。</param>
    /// <param name="stepName">步骤名（建表步骤为表名），用于日志与最终汇总。</param>
    /// <param name="action">步骤本体。</param>
    /// <remarks>
    /// **一步失败不得影响其余步骤**：建表、播种、回填各自都是独立可失败的动作，
    /// 让「交易表加列失败」连带标签表从未被建立，是本次修复要根除的形态。
    /// <para>
    /// 日志取 **Error** 而非 Warning：这批步骤失败意味着**功能在用的时候会报错**，
    /// 不是「可以忽略的提示」——原先的 Warning 在运维的告警规则里通常不会触发通知。
    /// </para>
    /// </remarks>
    private static void RunStep(ILogger logger, List<string> failures, string stepName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add(stepName);
            logger.LogError(ex, "数据库初始化步骤失败：{Step}（本步失败不影响其余步骤）", stepName);
        }
    }

    /// <summary>
    /// 取实体对应的真实表名，用于日志。
    /// </summary>
    /// <param name="db">SqlSugar 客户端。</param>
    /// <param name="entityType">实体类型。</param>
    /// <returns>库里的表名；取不到时退回实体类名。</returns>
    /// <remarks>
    /// 表名以实体上的 <c>SugarTable</c> 标注为准（如 <c>hamster_tag</c>）。
    /// 日志里写表名而非类名：用户报的是「表不存在」，报错信息与日志必须能对上同一个词。
    /// <para>
    /// 取不到时静默退回类名、**不改判成败**：日志取名的失败不该让建表本身失败。
    /// </para>
    /// </remarks>
    private static string ResolveTableName(ISqlSugarClient db, Type entityType)
    {
        try
        {
            return db.EntityMaintenance.GetEntityInfo(entityType).DbTableName;
        }
        catch (Exception)
        {
            return entityType.Name;
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
    /// 回填既有交易行的最后修改时间列。
    /// </summary>
    /// <param name="db">SqlSugar 客户端。</param>
    /// <param name="dbType">当前数据库类型，决定「不可绑定的空值」如何判定。</param>
    /// <param name="logger">日志记录器。</param>
    /// <remarks>
    /// **既有交易一律回填为它自己的创建时刻**：<c>updated_at</c> 是随本任务才引入的列，
    /// 升级前的交易没有修改时间信息，而「没有修改记录」的正确表达正是「从未被改过」，
    /// 即修改时间等于创建时刻——这也是新建交易时本列取值的口径（见
    /// <c>TransactionService.WriteBalancedTransactionAsync</c>）。
    /// <para>
    /// **必须回填、不可留空**：<c>updated_at</c> 在实体上是非空 <see cref="DateTime"/>，
    /// 而 SqlSugar 的增量加列只把新列追加为可空、不会为既有行补值，
    /// 于是升级后每一笔历史交易在这一列上都是 NULL，读回时无法绑定、
    /// 会让**整个交易列表查询**抛异常（同 <c>is_admin</c> / <c>is_system</c> 那两处回填的理由）。
    /// </para>
    /// <para>
    /// 取值直接抄 <c>created_at</c> 而不是取当前时刻：取当前时刻会把所有历史交易
    /// 一并说成「刚刚被改过」，那是一条不实的信息，而本列的全部意义就在于如实记录修改。
    /// </para>
    /// <para>
    /// 判定条件是 <see cref="UnbindableWhere"/>，即 NULL 与空串都算：Sqlite 的
    /// <c>datetime</c> 列若由更早的中间版本以空串形态建出，同样无法绑定。
    /// </para>
    /// </remarks>
    private static void BackfillTransactionUpdatedAt(ISqlSugarClient db, HamsterDbType dbType, ILogger logger)
    {
        const string updatedColumn = "updated_at";
        var affected = db.Ado.ExecuteCommand(
            $"UPDATE {TRANSACTION_TABLE} SET {updatedColumn} = created_at " +
            $"WHERE {UnbindableWhere(dbType, updatedColumn)}");

        if (affected > 0)
        {
            logger.LogInformation(
                "已把 {Count} 笔历史交易的最后修改时间回填为各自的创建时刻（「从未被修改过」的如实表达）",
                affected);
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
