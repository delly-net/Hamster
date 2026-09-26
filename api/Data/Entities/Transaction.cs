using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 交易：一次记账行为，其金额分布由若干条 <see cref="TransactionEntry"/> 明细描述。
/// </summary>
/// <remarks>
/// 本表只承载「这笔交易是什么、什么时候发生、由谁记的」，**不含任何金额列**：
/// 金额一律落在明细上，交易自身没有可漂移的汇总值。复式记账的配平约束
/// （借方金额合计 == 贷方金额合计）因此是对明细的约束，而不是对交易的约束。
/// <para>
/// 每笔交易由**借贷两条明细**构成（见 <see cref="EntryDirection"/>）：
/// 一条记在目标账户、一条记在对手方账户。期初余额的写法见
/// <c>TransactionService.RecordOpeningBalanceAsync</c>；
/// 期初交易的 <see cref="OccurredAt"/> 就是用户在建账户时选定的**期初时间**，
/// 也是该时间在库里的唯一落点（账户表不存此列，见 <see cref="Account"/> 的类头注释）。
/// </para>
/// <para>
/// 交易归属且仅归属一个账套（<see cref="AccountSetId"/>），与 <see cref="Account"/> 同一约定：
/// 流水按当前账套过滤。
/// </para>
/// </remarks>
[SugarTable("hamster_transaction")]
[SugarIndex("idx_hamster_transaction_account_set", nameof(AccountSetId), OrderByType.Asc)]
public sealed class Transaction
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>
    /// 所属账套主键，创建后不可修改。
    /// 索引非唯一：一个账套下自然有多笔交易。
    /// </summary>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>交易类型。</summary>
    [SugarColumn(ColumnName = "type")]
    public TransactionType Type { get; set; }

    /// <summary>
    /// 业务发生时间（UTC）。
    /// 与 <see cref="CreatedAt"/> 刻意分开：业务时间由记账人指定（补记昨天的支出、期初取账户创建时刻），
    /// 落库时间则由系统写入，两者不是一回事。
    /// </summary>
    [SugarColumn(ColumnName = "occurred_at")]
    public DateTime OccurredAt { get; set; }

    /// <summary>交易摘要。</summary>
    [SugarColumn(ColumnName = "summary", Length = 128)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>备注，无备注时为 <c>null</c>。</summary>
    [SugarColumn(ColumnName = "remark", Length = 256, IsNullable = true)]
    public string? Remark { get; set; }

    /// <summary>
    /// 分类主键（<see cref="Category"/>），**可空**；<c>null</c> 即「未分类」。
    /// </summary>
    /// <remarks>
    /// 分类挂在**交易**而非明细上：它描述的是「这笔账因何而发生」，是记账行为的属性。
    /// 一笔转账会落借贷两条明细，若分类挂在明细上，一笔转账就要选两个分类——
    /// 而转账的分类（如「还信用卡」）天然是整笔的，不是某一条明细的。
    /// <para>
    /// **可空是本表的正常状态**，不是待补的空缺：记账时分类是可选的（用户可不选），
    /// 且列是随分类能力才引入的，此前的历史流水一律为 <c>null</c>。
    /// </para>
    /// <para>
    /// 存**主键**而非分类名：分类可改名，存名称会让改名前的历史明细停留在旧名字上，
    /// 同一个分类在账面上裂成两个。挂主键则改名后历史明细自动跟着显示新名字。
    /// </para>
    /// <para>
    /// **本列不需要回填**（与 <see cref="Account.IsSystem"/>、<see cref="Account.CurrencyCode"/> 不同）：
    /// 那两列是非空 <c>bool</c> / 非空 <c>string</c>，NULL 会让实体绑定失败而整个列表查询 500；
    /// 而 <c>int?</c> 的既有行取到 NULL 正是「未分类」这一合法语义，直接可用。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "category_id", IsNullable = true)]
    public int? CategoryId { get; set; }

    /// <summary>
    /// 记账人主键；**可空**。
    /// </summary>
    /// <remarks>
    /// 期初交易由创建账户的那位用户写入，此时有值；但升级既有数据库时回填出来的期初交易
    /// **没有记账人可考**（账户表本身不记录创建者，见 <see cref="Account"/>）。
    /// 此处用 <c>null</c> 如实表达「无记账人」，而不是填 <c>0</c> 之类的哨兵值——
    /// 哨兵值会在「按记账人筛选」的查询里变成一个需要额外排除的特例。
    /// </remarks>
    [SugarColumn(ColumnName = "created_by_user_id", IsNullable = true)]
    public int? CreatedByUserId { get; set; }

    /// <summary>
    /// 落库时间（UTC）。
    /// 用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>：Sqlite 以文本存储时间且不保留偏移量，
    /// DateTimeOffset 读回时会被按本地时区重新解释，导致时刻偏移。
    /// </summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 最后修改时间（UTC），**业务语义上永不为空**（数据库列**可空**，见下方remarks）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="CreatedAt"/> 的分工：前者是「这笔账什么时候落库的」，后者是「这笔账最后一次被改动是什么时候」。
    /// 从未被改动过的交易，两者**逐字相等**——新建时直接把 <see cref="CreatedAt"/> 赋给它，
    /// 而不是再取一次 <c>UtcNow</c>（两次取时刻会差几微秒，让一笔全新落库的账看起来像「刚被人改过」）。
    /// <para>
    /// **只有改账会更新本列**（<c>TransactionService.UpdateUserTransactionAsync</c>），
    /// 且 <see cref="CreatedAt"/> 一律不动。来源、目标账户、金额、发生时间、摘要、备注、分类的改动都算「修改」。
    /// </para>
    /// <para>
    /// **列标为可空（<c>IsNullable = true</c>）是为了让增量加列在 PostgreSQL 上能跑通**：
    /// 本列是随功能迭代才加到**既有表**上的，SqlSugar 的增量加列会生成
    /// <c>ALTER TABLE … ADD COLUMN "updated_at" timestamp NOT NULL</c>，而 PostgreSQL 拒绝
    /// 在非空表上新增 NOT NULL 列（无默认值可回填）——语句被拒 → 建表步骤抛异常。
    /// Sqlite 侧无此限制（它的加列语句根本没有 NOT NULL 槽位），故该故障只在 PostgreSQL 部署上出现。
    /// 结果就是标签表与结算表**从未被建立**，直到用户点开标签页才以
    /// <c>relation "hamster_tag" does not exist</c> 暴露（建表各步现已互相隔离，见
    /// <c>DatabaseInitializer</c>）。
    /// </para>
    /// <para>
    /// **勿把本标注删掉改成「非空列 + 默认值」**：SqlSugar 只有在实体列带 <c>DefaultValue</c> 时
    /// 才把加列语句写成可空，而带 <c>DefaultValue</c> 的加列在既有库上实测会留下空串形态的脏值
    /// （见 <c>DatabaseInitializer.UnbindableWhere</c> 对两种脏值形态的处理）。
    /// 把「数据库列可空」与「业务上永不为空」分开表达，比给列塞一个默认值干净。
    /// </para>
    /// <para>
    /// **可空不等于可以留空**：写入侧一律赋非空值（新建取 <see cref="CreatedAt"/>、改账取当前时刻），
    /// 且 <c>DatabaseInitializer.BackfillTransactionUpdatedAt</c> 把升级前既有的行回填为
    /// <c>updated_at = created_at</c>——「无法考证的历史交易视作从未被改过」，
    /// 与本列引入前的语义完全一致。此前认为「增量加列只会追加为可空、故不会破坏既有行」的推论
    /// **是错的**（那只是 Sqlite 的实现细节），本列的可空标注与这段回填才是让读取侧永远拿到
    /// 可绑定 <see cref="DateTime"/> 的那对保证。
    /// </para>
    /// <para>
    /// **刻意不进出参 DTO**：界面是否呈现（明细页加列、修改时间列等）由后续任务决定，
    /// 故不牵出前端的类型与列。
    /// </para>
    /// <para>
    /// **明细表不加本列**（见 <see cref="TransactionEntry"/>）：改账时明细与交易头在同一
    /// <c>UseTranAsync</c> 内就地改写、没有各自的写入路径，与本类不加余额列是同一理由——
    /// 同一时刻在库里存两遍不含新信息。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "updated_at", IsNullable = true)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
