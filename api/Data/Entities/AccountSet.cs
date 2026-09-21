using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 账套：一组业务数据的归属单位（如「家庭账本」「A 公司账本」）。
/// 与用户为**多对多**关系，关联行见 <see cref="AccountSetMember"/>。
/// </summary>
/// <remarks>
/// 系统管理员无需关联即可访问全部账套（判定在服务层按 <see cref="User.IsAdmin"/> 分支），
/// 普通用户只能访问被显式关联的账套。
/// </remarks>
[SugarTable("hamster_account_set")]
[SugarIndex("uk_hamster_account_set_name", nameof(Name), OrderByType.Asc, true)]
public sealed class AccountSet
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>账套名称，全局唯一（查重时不区分大小写，判定见 <c>AccountSetService.IsNameTakenAsync</c>）。</summary>
    [SugarColumn(ColumnName = "name", Length = 64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>备注，用于说明账套用途；无备注时为 <c>null</c>。</summary>
    [SugarColumn(ColumnName = "remark", Length = 256, IsNullable = true)]
    public string? Remark { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// 用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>：Sqlite 以文本存储时间且不保留偏移量，
    /// DateTimeOffset 读回时会被按本地时区重新解释，导致时刻偏移。
    /// </summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
