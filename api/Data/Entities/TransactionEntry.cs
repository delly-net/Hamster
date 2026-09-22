using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 交易明细：交易落在某个账户上的一条金额记录，带借贷方向。
/// </summary>
/// <remarks>
/// 一笔交易由若干条明细构成，**借方与贷方各自合计必然相等**（复式记账的配平约束）。
/// 期初余额场景下恰有两条（目标账户一条、账本账户一条），但表结构不限制条数。
/// <para>
/// <see cref="Amount"/> **恒为正数**，方向只由 <see cref="Direction"/> 表达，
/// 不采用「带符号金额」表示法：金额的符号一旦承载语义，
/// 「−500 的借方」这类自相矛盾的组合就会成为合法数据，配平校验也不得不先取绝对值。
/// 方向与金额分开后，「借方合计 == 贷方合计」可以不加任何预处理地直接比对。
/// </para>
/// <para>
/// **刻意不设 <c>created_at</c>**：明细与其父交易同一事务写入，时刻必然与父行的
/// <see cref="Transaction.CreatedAt"/> 相同，该列不含任何新信息（与 <see cref="Account"/>
/// 不设余额列是同一取舍——不为省一次读取而引入可能与父行漂移的重复数据）。
/// </para>
/// <para>
/// **同样刻意不设 <c>account_set_id</c>**：账套归属已由父交易唯一确定，明细经
/// <see cref="TransactionId"/> 关联合成即可；两处都存账套归属，就有出现
/// 「交易的账套与明细的账套不一致」的孤儿数据的可能。
/// </para>
/// </remarks>
[SugarTable("hamster_transaction_entry")]
[SugarIndex("idx_hamster_transaction_entry_transaction", nameof(TransactionId), OrderByType.Asc)]
[SugarIndex("idx_hamster_transaction_entry_account", nameof(AccountId), OrderByType.Asc)]
public sealed class TransactionEntry
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>所属交易主键。</summary>
    [SugarColumn(ColumnName = "transaction_id")]
    public int TransactionId { get; set; }

    /// <summary>
    /// 挂靠账户主键。
    /// 账户采用软删除（见 <see cref="Account.IsActive"/>），故该外键不会悬空。
    /// </summary>
    [SugarColumn(ColumnName = "account_id")]
    public int AccountId { get; set; }

    /// <summary>借贷方向，决定该明细对账户余额是增加还是减少。</summary>
    [SugarColumn(ColumnName = "direction")]
    public EntryDirection Direction { get; set; }

    /// <summary>
    /// 金额，单位「元」，两位小数，**恒为正**；方向由 <see cref="Direction"/> 表达。
    /// </summary>
    [SugarColumn(ColumnName = "amount", DecimalDigits = 2)]
    public decimal Amount { get; set; }
}
