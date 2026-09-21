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
    /// 是否系统管理员，可进入用户管理（激活/停用、密码重置链接、删除）。
    /// 管理员身份**不写入 JWT**，每个管理端点回查数据库，避免管理员被停用或删除后旧令牌继续提权。
    /// </summary>
    /// <remarks>
    /// 刻意**不设 <c>DefaultValue</c>**：SqlSugar 的增量加列只会把新列补成可空、并不为既有行填值，
    /// 该标注给不出「既有行自动为 0」的保证，反而掩盖了升级路径。既有行的 NULL 由
    /// <see cref="DatabaseInitializer"/> 的回填写成「非管理员 + 未激活」。
    /// </remarks>
    [SugarColumn(ColumnName = "is_admin")]
    public bool IsAdmin { get; set; }

    /// <summary>
    /// 是否已激活。注册后默认未激活，须由管理员激活后才能登录。
    /// </summary>
    [SugarColumn(ColumnName = "is_active")]
    public bool IsActive { get; set; }

    /// <summary>
    /// 密码重置令牌的 SHA-256 哈希（Base64），无待处理的重置请求时为 <c>null</c>。
    /// 只存哈希不存明文：令牌本身是高熵随机串，无需慢哈希，SHA-256 足以避免库被读走后直接可用。
    /// </summary>
    [SugarColumn(ColumnName = "reset_token_hash", Length = 64, IsNullable = true)]
    public string? ResetTokenHash { get; set; }

    /// <summary>
    /// 密码重置令牌的过期时间（UTC）；无待处理的重置请求时为 <c>null</c>。
    /// 与 <see cref="CreatedAt"/> 同样用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>。
    /// </summary>
    [SugarColumn(ColumnName = "reset_token_expires_at", IsNullable = true)]
    public DateTime? ResetTokenExpiresAt { get; set; }

    /// <summary>
    /// 注册时间（UTC）。
    /// 用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>：Sqlite 以文本存储时间且不保留偏移量，
    /// DateTimeOffset 读回时会被按本地时区重新解释，导致时刻偏移。
    /// </summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
