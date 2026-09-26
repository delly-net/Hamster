using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 结算明细快照：结算时某笔交易的**每一条**明细在本表中的冻结副本。
/// </summary>
/// <remarks>
/// 与 <see cref="SettlementTransaction"/> 同属「冗余存储」，关系是
/// <see cref="SettlementTransaction"/> 1 : N 本表（一笔交易恒有借贷两条明细）。
/// <para>
/// 冗余的**是整行数据而不是结构**：本表仍是关系型的一张普通表，明细的账户、方向、金额
/// 各自是可查询的列，故「某账户某天发生额合计」这类统计可以直接用 SQL 聚合，
/// 不必把 JSON 读进内存再解析（这正是三张表相对单张 JSON 大字段的取舍所在）。
/// </para>
/// <para>
/// **冗余 <see cref="AccountName"/> 与 <see cref="Transaction"/> 侧冗余分类名同理**：
/// 账户可改名，只存主键会让改名后回看历史结算显示新名字，等于用今天的改动改写昨天的账。
/// </para>
/// <para>
/// **刻意不设 <c>created_at</c>**：本表与其父快照交易同一事务写入，时刻必然一致
/// （同 <see cref="TransactionEntry"/> 不设 <c>created_at</c> 的理由）。
/// 同样**不设 <c>account_set_id</c>**：账套归属由父快照交易唯一确定。
/// </para>
/// <para>
/// 本表**只增不改**，与其父 <see cref="SettlementTransaction"/> 同一事务写入。
/// </para>
/// </remarks>
[SugarTable("hamster_settlement_entry")]
[SugarIndex("idx_hamster_settlement_entry_transaction", nameof(SettlementTransactionId), OrderByType.Asc)]
[SugarIndex("idx_hamster_settlement_entry_account", nameof(AccountId), OrderByType.Asc)]
public sealed class SettlementEntry
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>所属结算交易快照主键（<see cref="SettlementTransaction"/>）。</summary>
    [SugarColumn(ColumnName = "settlement_transaction_id")]
    public int SettlementTransactionId { get; set; }

    /// <summary>源明细主键（<see cref="TransactionEntry"/>），**仅作溯源**，不建外键约束。</summary>
    [SugarColumn(ColumnName = "source_entry_id")]
    public int SourceEntryId { get; set; }

    /// <summary>账户主键（快照原值）。</summary>
    [SugarColumn(ColumnName = "account_id")]
    public int AccountId { get; set; }

    /// <summary>账户名（**冗余**，快照时点的取值）。见类头注释：账户改名不改写历史结算。</summary>
    [SugarColumn(ColumnName = "account_name", Length = 64)]
    public string AccountName { get; set; } = string.Empty;

    /// <summary>借贷方向（快照原值），决定该明细对账户余额是增加还是减少。</summary>
    [SugarColumn(ColumnName = "direction")]
    public EntryDirection Direction { get; set; }

    /// <summary>金额（快照原值），单位「元」，两位小数，**恒为正**；方向由 <see cref="Direction"/> 表达。</summary>
    [SugarColumn(ColumnName = "amount", DecimalDigits = 2)]
    public decimal Amount { get; set; }
}
