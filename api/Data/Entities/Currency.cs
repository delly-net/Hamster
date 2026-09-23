using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 币种：账户的计价单位，也是「两个账户能否互相交易」的判定依据。
/// </summary>
/// <remarks>
/// 币种是**全系统共用的字典**（不归属账套）：<see cref="Code"/> 取自 ISO 4217 三字母代码，
/// 各账套的账户都指向同一份币种表与同一个默认币种，避免「同一币种在不同账套里名称与符号不一致」。
/// <para>
/// 与账户同一约定，删除采用**软删除**：以 <see cref="IsActive"/> 的停用/启用取代物理删除。
/// 账户会绑定币种，物理删除会让既有账户指向一个不存在的币种，且历史金额失去计价单位。
/// </para>
/// </remarks>
[SugarTable("hamster_currency")]
[SugarIndex("uk_hamster_currency_code", nameof(Code), OrderByType.Asc, true)]
public sealed class Currency
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>
    /// 币种代码：ISO 4217 三字母代码（如 <c>CNY</c>、<c>USD</c>），**大写存储**。
    /// </summary>
    /// <remarks>
    /// 代码是币种的**身份**，一经创建不可修改（见 <c>CurrencyService.UpdateAsync</c> 只更新名称/符号/排序）：
    /// 账户按代码绑定币种，中途改代码等于让所有已绑定的账户指向另一个币种。
    /// 查重不区分大小写，判定见 <c>CurrencyService.IsCodeTakenAsync</c>。
    /// </remarks>
    [SugarColumn(ColumnName = "code", Length = 8)]
    public string Code { get; set; } = string.Empty;

    /// <summary>币种中文名，如「人民币」「美元」。</summary>
    [SugarColumn(ColumnName = "name", Length = 32)]
    public string Name { get; set; } = string.Empty;

    /// <summary>币种符号，如 <c>¥</c>、<c>$</c>；无符号时为 <c>null</c>。</summary>
    [SugarColumn(ColumnName = "symbol", Length = 8, IsNullable = true)]
    public string? Symbol { get; set; }

    /// <summary>
    /// 是否为系统默认币种。
    /// </summary>
    /// <remarks>
    /// 默认币种是**新建账户的兜底取值**（用户在账户表单里未改即用它），也是升级既有数据库时
    /// 历史账户的币种回填来源。**全表至多一个为 <c>true</c>**——该不变量由
    /// <c>CurrencyService.SetDefaultAsync</c> 在一个事务内「先清后置」保证，而不是靠数据库约束，
    /// 因为 Sqlite 与 PostgreSQL 都不支持「带部分条件的唯一索引」之外的跨库一致写法。
    /// </remarks>
    [SugarColumn(ColumnName = "is_default")]
    public bool IsDefault { get; set; }

    /// <summary>
    /// 是否启用。停用即软删除：停用后不再出现在记账与账户表单的币种候选中，
    /// 但既有账户与流水照常可用（否则历史金额会失去计价单位）。列名与语义沿用 <see cref="User.IsActive"/>。
    /// </summary>
    [SugarColumn(ColumnName = "is_active")]
    public bool IsActive { get; set; }

    /// <summary>呈现顺序，越小越靠前；同值时按主键升序。</summary>
    /// <remarks>
    /// 与账户按主键升序不同：币种有公认的「常用在前」次序（人民币、美元、欧元…），
    /// 而这个次序与建库时的播种次序恰好一致，故用一列显式表达，便于管理员调整。
    /// </remarks>
    [SugarColumn(ColumnName = "sort_order")]
    public int SortOrder { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// 用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>：Sqlite 以文本存储时间且不保留偏移量，
    /// DateTimeOffset 读回时会被按本地时区重新解释，导致时刻偏移。
    /// </summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
