using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 用户业务服务：注册、登录校验与查询。
/// </summary>
public interface IUserService
{
    /// <summary>
    /// 按用户名查询用户，不区分大小写。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的用户；不存在时返回 <c>null</c>。</returns>
    Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键查询用户。
    /// </summary>
    /// <param name="id">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的用户；不存在时返回 <c>null</c>。</returns>
    Task<User?> FindByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建用户，密码以 PBKDF2 哈希入库。
    /// 新用户一律为「非管理员 + 未激活」，须由管理员激活后才能登录。
    /// </summary>
    /// <param name="username">用户名（调用方需保证已 Trim 且未被占用）。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的用户实体。</returns>
    Task<User> RegisterAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 创建默认管理员账户（已激活）。
    /// </summary>
    /// <param name="username">管理员用户名。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的用户实体。</returns>
    Task<User> CreateAdminAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 校验登录凭据。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>校验通过返回用户，否则返回 <c>null</c>。</returns>
    Task<User?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出全部用户，按主键升序。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>用户列表。</returns>
    Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 设置用户的激活状态。
    /// </summary>
    /// <param name="id">用户主键。</param>
    /// <param name="isActive">是否激活。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>；用户不存在返回 <c>false</c>。</returns>
    Task<bool> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除用户。
    /// </summary>
    /// <param name="id">用户主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>；用户不存在返回 <c>false</c>。</returns>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 重置用户密码，并清空待处理的重置令牌（令牌一次性）。
    /// </summary>
    /// <param name="id">用户主键。</param>
    /// <param name="newPassword">新密码明文。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>；用户不存在返回 <c>false</c>。</returns>
    Task<bool> ResetPasswordAsync(int id, string newPassword, CancellationToken cancellationToken = default);

    /// <summary>
    /// 写入密码重置令牌（存哈希，不存明文）。
    /// </summary>
    /// <param name="id">用户主键。</param>
    /// <param name="tokenHash">令牌的 SHA-256 哈希。</param>
    /// <param name="expiresAt">过期时间（UTC）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>；用户不存在返回 <c>false</c>。</returns>
    Task<bool> SetResetTokenAsync(int id, string tokenHash, DateTime expiresAt, CancellationToken cancellationToken = default);
}
