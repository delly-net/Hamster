using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 账套与用户的关联行（多对多中间表）。
/// </summary>
/// <remarks>
/// 关联集合以**覆盖式**整体替换（见 <c>AccountSetService.ReplaceMembersAsync</c>），
/// 配合 <c>(account_set_id, user_id)</c> 唯一索引从源头杜绝重复关联——重复行会让前端的
/// 账套切换列表出现重复项。
/// </remarks>
[SugarTable("hamster_account_set_member")]
[SugarIndex(
    "uk_hamster_account_set_member",
    nameof(AccountSetId),
    OrderByType.Asc,
    nameof(UserId),
    OrderByType.Asc,
    true)]
public sealed class AccountSetMember
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>账套主键。</summary>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>被关联的用户主键。</summary>
    [SugarColumn(ColumnName = "user_id")]
    public int UserId { get; set; }

    /// <summary>
    /// 关联建立时间（UTC）。
    /// 用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>，理由同 <see cref="AccountSet.CreatedAt"/>。
    /// </summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
