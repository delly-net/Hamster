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
    /// </summary>
    /// <param name="username">用户名（调用方需保证已 Trim 且未被占用）。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的用户实体。</returns>
    Task<User> RegisterAsync(string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 校验登录凭据。
    /// </summary>
    /// <param name="username">用户名。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>校验通过返回用户，否则返回 <c>null</c>。</returns>
    Task<User?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
}
