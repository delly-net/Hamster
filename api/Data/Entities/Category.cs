using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 分类：一笔交易的业务归集口径（如「餐饮」「交通」「工资」），由用户自己建立与维护。
/// </summary>
/// <remarks>
/// **分类按账套隔离**（<see cref="AccountSetId"/>）：每个账套各维护一份分类，
/// 账户列表与明细查询都按「当前账套」过滤。这与 <see cref="Currency"/> 的取舍**刚好相反**
/// ——币种是全系统共用的字典（同一币种在各账套里不该有两个含义），而分类是各家的业务语义
/// （「餐饮」在不同账套里覆盖的范围可以完全不同）。勿为「统一风格」把本表改成全局字典。
/// <para>
/// **不区分记账类型**：一份分类字典通用，收入、支出、转账三种记账都能选用同一个分类。
/// 故本表没有类型列——「用『工资』记一笔支出」是用户自己赋予的语义，系统不替他判对错。
/// </para>
/// <para>
/// 删除采用**软删除**：以 <see cref="IsActive"/> 的停用/启用取代物理删除。
/// 历史流水挂靠分类，物理删除会让既有明细的分类凭空消失。
/// </para>
/// </remarks>
[SugarTable("hamster_category")]
[SugarIndex("idx_hamster_category_account_set", nameof(AccountSetId), OrderByType.Asc)]
public sealed class Category
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
    /// 索引非唯一：一个账套下自然有多个分类。
    /// </summary>
    /// <remarks>
    /// 不可修改的理由与账户同一性质：分类是**账套内**的公共语义，
    /// 中途改归属等于把它从一家的字典搬到另一家，而挂在它上面的历史流水并不跟着搬家。
    /// 修改端点因此不接受本字段（见 <c>CategoryEndpoints</c>）。
    /// </remarks>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>
    /// 分类名称。
    /// </summary>
    /// <remarks>
    /// 唯一性是「**账套内 + 不区分大小写**」，由服务层判定而不做数据库约束——
    /// 与 <see cref="Account.Name"/> 同一取舍（数据库唯一索引无法表达「不区分大小写」且跨 Sqlite
    /// 与 PostgreSQL 写法不一致）。
    /// <para>
    /// 这条判定直接支撑「记账时手工输入自动创建」：查重漏掉，同一个分类名被输入两次就会建出两行，
    /// 明细页按分类归集时同一类会被拆成两处。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "name", Length = 32)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用。停用即软删除：停用后不再出现在记账表单的分类候选中，
    /// 但已用它的历史流水照常可用（否则过去的账会失去归集口径）。列名与语义沿用 <see cref="User.IsActive"/>。
    /// </summary>
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
