using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 用户：以「用户名 + 密码」作为登录凭据。
/// 密码仅以 PBKDF2 哈希形式存储，任何接口响应都不得回传 <see cref="PasswordHash"/>。
/// </summary>
[SugarTable("hamster_user")]
[SugarIndex("uk_hamster_user_username", nameof(Username), OrderByType.Asc, true)]
public sealed class User
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>用户名，全局唯一（查重时不区分大小写）。</summary>
    [SugarColumn(ColumnName = "username", Length = 64)]
    public string Username { get; set; } = string.Empty;

    /// <summary>密码哈希，格式见 <c>PasswordHasher.Hash</c>。</summary>
    [SugarColumn(ColumnName = "password_hash", Length = 256)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// 注册时间（UTC）。
    /// 用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>：Sqlite 以文本存储时间且不保留偏移量，
    /// DateTimeOffset 读回时会被按本地时区重新解释，导致时刻偏移。
    /// </summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
