using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 标签：一笔交易的自由标注（如「出差」「报销」「待核销」），由用户自己建立与维护。
/// </summary>
/// <remarks>
/// **与 <see cref="Category"/> 逐条同构**：按账套隔离、账套内所有成员共用一份、软删除、
/// 查重口径是「账套内 + 去空白 + 不区分大小写」、记账时手工输入的新名字会被自动创建。
/// 故本表的列与索引也与分类表一一对应，改动其一时**必须同时想到另一张**。
/// <para>
/// 与分类的**唯一结构性差异是基数**：分类挂在交易头上（<see cref="Transaction.CategoryId"/> 一列），
/// 一笔交易至多一个；标签落在 <see cref="TransactionTag"/> 子表里，一笔交易可以有多个。
/// 「多值」是标签不能再占交易表一列的原因——把「出差,报销」塞进一个字符串列，
/// 既无法按标签精确筛选，改名后历史也会停在旧名字上。
/// </para>
/// <para>
/// 与 <see cref="Currency"/> 的取舍**刚好相反**（与分类一致）：币种是全系统共用的字典，
/// 而标签是各家的业务词汇——「出差」在不同账套里覆盖的范围可以完全不同。
/// 勿为「统一风格」把本表改成全局字典。
/// </para>
/// <para>
/// 删除采用**软删除**：以 <see cref="IsActive"/> 的停用/启用取代物理删除。
/// 历史流水通过 <see cref="TransactionTag"/> 挂靠标签，物理删除会让既有明细的标签凭空消失。
/// </para>
/// </remarks>
[SugarTable("hamster_tag")]
[SugarIndex("idx_hamster_tag_account_set", nameof(AccountSetId), OrderByType.Asc)]
public sealed class Tag
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
    /// 索引非唯一：一个账套下自然有多个标签。
    /// </summary>
    /// <remarks>
    /// 不可修改的理由与分类同一性质：标签是**账套内**的公共语义，
    /// 中途改归属等于把它从一家的词汇表搬到另一家，而挂着它的历史流水并不跟着搬家。
    /// 修改端点因此不接受本字段（见 <c>TagEndpoints</c>）。
    /// </remarks>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>
    /// 标签名称。
    /// </summary>
    /// <remarks>
    /// 列长与 <see cref="Category.Name"/> 一致（32），唯一性同样是「**账套内 + 不区分大小写**」，
    /// 由服务层判定而不做数据库约束——数据库唯一索引无法表达「不区分大小写」，
    /// 且跨 Sqlite 与 PostgreSQL 写法不一致。
    /// <para>
    /// 这条判定直接支撑「记账时手工输入自动创建」：查重漏掉，同一个标签名被输入两次就会建出两行，
    /// 一笔交易按标签归集时同一件事会被拆成两处。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "name", Length = 32)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用。停用即软删除：停用后不再出现在记账表单与筛选区的标签候选中，
    /// 但挂着它的历史流水照常可用（否则过去的账会失去这一层标注）。
    /// 列名与语义沿用 <see cref="User.IsActive"/>。
    /// </summary>
    /// <remarks>
    /// **停用标签的名称照常在明细页呈现**：停用是「不再供新记账选择」，不是「历史上从未用过」。
    /// 这与「账目明细页的分类名不过滤 <see cref="Category.IsActive"/>」是同一条口径。
    /// </remarks>
    [SugarColumn(ColumnName = "is_active")]
    public bool IsActive { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// 用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>：Sqlite 以文本存储时间且不保留偏移量，
    /// DateTimeOffset 读回时会被按本地时区重新解释，导致时刻偏移。
    /// </summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
