using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 结算任务：一个账套在**某一个交易日期**上的结算批次。
/// 由交易统计定时任务按「交易日期」分组建立，其下冗余存储该日期的全部交易与明细
/// （见 <see cref="SettlementTransaction"/> / <see cref="SettlementEntry"/>）。
/// </summary>
/// <remarks>
/// 本表是**结算的事实**：一条记录代表「某个账套的某一天，已经被结算过一次」。
/// 它只增不改（<see cref="ExecutedAt"/> 是唯一的例外），删除它等于抹掉一次结算记录。
/// <para>
/// **按账套隔离**：交易归属账套，故结算也归属账套。唯一索引是复合的
/// <c>(account_set_id, transaction_date)</c> 而不是 <c>transaction_date</c> 单列——
/// 单列唯一会让两个账套的同一天互相顶掉。
/// </para>
/// <para>
/// **粒度是「天」，不设更细的时间字段**：交易日期的口径由收集窗口固定为整天
/// （本地 00:00 至次日 00:00，见 <c>SettlementService</c>），任务本身就是「这一天的结算」。
/// </para>
/// </remarks>
[SugarTable("hamster_settlement_task")]
[SugarIndex(
    "uk_hamster_settlement_task_account_set_date",
    nameof(AccountSetId),
    OrderByType.Asc,
    nameof(TransactionDate),
    OrderByType.Asc,
    true)]
[SugarIndex("idx_hamster_settlement_task_account_set", nameof(AccountSetId), OrderByType.Asc)]
[SugarIndex("idx_hamster_settlement_task_pending", nameof(ExecutedAt), OrderByType.Asc)]
public sealed class SettlementTask
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>所属账套主键，创建后不可修改（同 <see cref="Transaction.AccountSetId"/>）。</summary>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>
    /// 交易日期：本结算任务覆盖的那一天（**本地日期**，时刻部分恒为 00:00:00）。
    /// </summary>
    /// <remarks>
    /// **这是全项目唯一一处刻意不以 UTC 存储的时间列**，理由如下：
    /// <list type="bullet">
    /// <item>它是「日期」而不是「时刻」。用户问「9 月 25 号结算了吗」，指的是**他看到的**那一天，
    /// 而用户看到的日期来自本地时区（账目明细页按本地时区呈现）。</item>
    /// <item>若存成 UTC，则东八区 2026-09-25 00:00～08:00 发生的账会被归到 UTC 的 09-24，
    /// 结算日与用户在界面上看到的那一天对不上；读回时还得再转回本地一次，凭空多一道
    /// 容易写漏的换算。</item>
    /// <item>它不参与任何与 UTC 时刻的比较——收集窗口的边界在 <c>SettlementService</c> 里
    /// 由本地 0 点转成 UTC 后再与 <see cref="Transaction.OccurredAt"/> 比对，
    /// 本列只用于**分组、去重与水位**，三者都在本地日期语义下进行。</item>
    /// </list>
    /// <para>
    /// 读到本列时 <c>Kind</c> 为 <c>Unspecified</c>（两种库都不保存 Kind），
    /// **不要再把它当 UTC 去转本地时区**，直接取 <c>Date</c> 即当天的日期。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "transaction_date")]
    public DateTime TransactionDate { get; set; }

    /// <summary>
    /// 创建时间（UTC）：本结算任务由收集任务建立的那一刻。
    /// </summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 结算执行时间（UTC）；<c>null</c> 即「尚未执行」。
    /// </summary>
    /// <remarks>
    /// **本列超出任务描述里点名的两个字段（交易日期、创建时间），但它是必需的**：
    /// 没有它，结算执行任务就无法区分「已结算过」与「尚未结算」，
    /// 每个执行日都会把**全部历史**结算任务重新触发一遍。
    /// <para>
    /// 语义是「结算事件已成功派发给全部订阅者」：有订阅者失败时**不标记**，
    /// 留待下一个执行日重试（见 <c>Job</c> 层）。因此**订阅者必须幂等**——
    /// 同一个结算事件可能被投递两次。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "executed_at", IsNullable = true)]
    public DateTime? ExecutedAt { get; set; }
}
