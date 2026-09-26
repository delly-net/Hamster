using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 结算交易快照：结算时任一一笔交易在本表中的**冻结副本**。
/// </summary>
/// <remarks>
/// 本表是**冗余存储**的承担者：字段与 <see cref="Transaction"/> 一一对应（外加溯源与分类名），
/// 但它是**历史副本**而不是源表的一个视图——源交易日后被改账、分类日后被改名，
/// 本表的内容都不随之变化。这正是「结算」相对「实时查询」的意义：
/// 结算过的账要能在任何时候被逐字还原成当时的样子。
/// <para>
/// **不设数据库外键指向源交易**：源行可能被改账改写（<c>UpdatedAt</c> 变、账户与金额变），
/// 快照的职责恰恰是不受影响。<see cref="SourceTransactionId"/> 仅作**溯源**之用，
/// 让对账时能回答「这条快照对应的是库里哪一笔」。
/// </para>
/// <para>
/// **冗余 <see cref="CategoryName"/> 是刻意的反范式化**：分类可改名、可停用、可软删除，
/// 只存 <see cref="CategoryId"/> 的话，改名后回看历史结算会显示新名字——
/// 那等于让今天的改名改写昨天的结算。存下当时的名字，快照才自足。
/// </para>
/// <para>
/// 本表**只增不改**，与其父 <see cref="SettlementTask"/> 同一事务写入。
/// </para>
/// </remarks>
[SugarTable("hamster_settlement_transaction")]
[SugarIndex("idx_hamster_settlement_transaction_task", nameof(SettlementTaskId), OrderByType.Asc)]
[SugarIndex("idx_hamster_settlement_transaction_source", nameof(SourceTransactionId), OrderByType.Asc)]
public sealed class SettlementTransaction
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>所属结算任务主键（<see cref="SettlementTask"/>）。</summary>
    [SugarColumn(ColumnName = "settlement_task_id")]
    public int SettlementTaskId { get; set; }

    /// <summary>源交易主键（<see cref="Transaction"/>），**仅作溯源**，不建外键约束。</summary>
    [SugarColumn(ColumnName = "source_transaction_id")]
    public int SourceTransactionId { get; set; }

    /// <summary>
    /// 账套主键。
    /// 与其父结算任务上的同名列冗余：本表可能被单独查询（按账套统计），
    /// 冗余一份可省去一次与任务表的联查；值在写入时取自源交易，与父任务必然一致。
    /// </summary>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>交易类型（快照原值）。</summary>
    [SugarColumn(ColumnName = "type")]
    public TransactionType Type { get; set; }

    /// <summary>业务发生时间（UTC，快照原值）。</summary>
    [SugarColumn(ColumnName = "occurred_at")]
    public DateTime OccurredAt { get; set; }

    /// <summary>交易摘要（快照原值）。</summary>
    [SugarColumn(ColumnName = "summary", Length = 128)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>备注（快照原值），无备注时为 <c>null</c>。</summary>
    [SugarColumn(ColumnName = "remark", Length = 256, IsNullable = true)]
    public string? Remark { get; set; }

    /// <summary>分类主键（快照原值），<c>null</c> 即「未分类」。</summary>
    [SugarColumn(ColumnName = "category_id", IsNullable = true)]
    public int? CategoryId { get; set; }

    /// <summary>
    /// 分类名（**冗余**，快照时点的取值）；未分类时为 <c>null</c>。
    /// 见类头注释：存名字是为了让分类改名不改写历史结算。
    /// </summary>
    [SugarColumn(ColumnName = "category_name", Length = 32, IsNullable = true)]
    public string? CategoryName { get; set; }

    /// <summary>记账人主键（快照原值），可空（见 <see cref="Transaction.CreatedByUserId"/>）。</summary>
    [SugarColumn(ColumnName = "created_by_user_id", IsNullable = true)]
    public int? CreatedByUserId { get; set; }

    /// <summary>源交易的落库时间（UTC，快照原值）。</summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// 源交易的**最后修改时间**（UTC，快照原值）。
    /// 冗余本列让「这笔账在结算时是否已被改过」可从快照直接读出，无需回查源表。
    /// </summary>
    [SugarColumn(ColumnName = "updated_at")]
    public DateTime UpdatedAt { get; set; }
}
