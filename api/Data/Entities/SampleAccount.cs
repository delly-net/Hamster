using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 【示例实体】资金账户。
/// 仅用于验证框架接线（建表、查询、写入链路是否打通），不是最终业务模型，
/// 后续业务建模时请按实际领域模型替换或扩展。
/// </summary>
[SugarTable("sample_account")]
public sealed class SampleAccount
{
    /// <summary>主键。</summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public long Id { get; set; }

    /// <summary>账户名称。</summary>
    [SugarColumn(ColumnName = "name", Length = 64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>账户余额。建议业务侧以「分」为单位存储，避免浮点误差。</summary>
    [SugarColumn(ColumnName = "balance", DecimalDigits = 2)]
    public decimal Balance { get; set; }

    /// <summary>创建时间（UTC）。</summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
