using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 交易标签关联：一笔交易挂着的一个标签（多对多的中间行）。
/// </summary>
/// <remarks>
/// 这是**交易的子表**，与 <see cref="TransactionEntry"/> 并列——表名已经写明归属：
/// 关联行是交易的一部分，不是标签的一部分。由此推出本表的读写分工：
/// 写入在 <c>TransactionService</c>（与交易头、两条明细**同一个 <c>UseTranAsync</c>**，
/// 保证「交易记下了标签却没挂上」这种半成品不可能存在）；
/// 读取在 <c>EntryQueryService</c>（它本来就在读 <see cref="TransactionEntry"/>）。
/// 标签字典本身的增删改则归 <c>TagService</c>，它不碰本表。
/// <para>
/// **刻意不设 <c>created_at</c>**：关联行与其父交易同一事务写入，时刻必然与父行的
/// <see cref="Transaction.CreatedAt"/> 相同，该列不含任何新信息。
/// </para>
/// <para>
/// **同样刻意不设 <c>account_set_id</c>**：账套归属已由父交易唯一确定，
/// 两处都存账套归属，就有出现「交易的账套与标签的账套不一致」的孤儿数据的可能
/// （与 <see cref="TransactionEntry"/> 同一取舍）。标签与交易同账套这条不变量
/// 因此只能设在**写入路径**上（<c>TransactionService.EnsureWriteInvariants</c>），
/// 与「分类不会跨账套」是同一处守卫、同一份判据。
/// </para>
/// <para>
/// **关联行没有查询侧的排序依赖**：明细主键是分页排序键，故改账时只能就地 UPDATE、不得删旧插新；
/// 关联行不参与任何排序或翻页，故改账时**整体替换**（先删后插）是安全的。
/// 这两条口径不同源自「有没有别的东西依赖这个主键」，不是随意的松紧不一。
/// </para>
/// </remarks>
[SugarTable("hamster_transaction_tag")]
[SugarIndex(
    "uk_hamster_transaction_tag",
    nameof(TransactionId),
    OrderByType.Asc,
    nameof(TagId),
    OrderByType.Asc,
    true)]
[SugarIndex("idx_hamster_transaction_tag_tag", nameof(TagId), OrderByType.Asc)]
public sealed class TransactionTag
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
    /// 挂靠标签主键。
    /// 标签采用软删除（见 <see cref="Tag.IsActive"/>），故该外键不会悬空。
    /// </summary>
    /// <remarks>
    /// 「同一笔交易不落重复标签」由 <c>(transaction_id, tag_id)</c> 的**唯一索引**保证
    /// （同 <see cref="AccountSetMember"/> 用唯一索引杜绝重复关联的先例）——
    /// 写入侧另有一道按主键的去重，两道一起才挡住「同一个标签传两次」。
    /// 唯一索引在这里不是「数量上限」：一笔交易想挂多少标签都行，
    /// 它挡的是**同一事实记两遍**。
    /// </remarks>
    [SugarColumn(ColumnName = "tag_id")]
    public int TagId { get; set; }
}
