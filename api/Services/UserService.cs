using Hamster.Api.Data.Entities;
using Hamster.Api.Security;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的用户业务实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
public sealed class UserService(ISqlSugarClient db) : IUserService
{
    /// <inheritdoc />
    public async Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(username);
        var users = await db.Queryable<User>()
            .Where(user => user.Username.ToLower() == normalized)
            .Take(1)
            .ToListAsync(cancellationToken);

        return users.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<User?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var users = await db.Queryable<User>()
            .Where(user => user.Id == id)
            .Take(1)
            .ToListAsync(cancellationToken);

        return users.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<User> RegisterAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        // 注册用户一律非管理员且未激活，须由管理员激活后才能登录
        var user = new User
        {
            Username = username.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            IsAdmin = false,
            IsActive = false,
        };

        user.Id = await db.Insertable(user).ExecuteReturnIdentityAsync(cancellationToken);
        return user;
    }

    /// <inheritdoc />
    public async Task<User> CreateAdminAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var user = new User
        {
            Username = username.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            IsAdmin = true,
            IsActive = true,
        };

        user.Id = await db.Insertable(user).ExecuteReturnIdentityAsync(cancellationToken);
        return user;
    }

    /// <inheritdoc />
    public async Task<User?> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var user = await FindByUsernameAsync(username, cancellationToken);
        if (user is null)
        {
            return null;
        }

        return PasswordHasher.Verify(password, user.PasswordHash) ? user : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await db.Queryable<User>()
            .OrderBy(user => user.Id)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default)
    {
        var affected = await db.Updateable<User>()
            .SetColumns(user => user.IsActive == isActive)
            .Where(user => user.Id == id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var affected = await db.Deleteable<User>()
            .Where(user => user.Id == id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ResetPasswordAsync(int id, string newPassword, CancellationToken cancellationToken = default)
    {
        // 改密同时清空重置令牌：令牌一次性，用后即失效
        var affected = await db.Updateable<User>()
            .SetColumns(user => new User
            {
                PasswordHash = PasswordHasher.Hash(newPassword),
                ResetTokenHash = null,
                ResetTokenExpiresAt = null,
            })
            .Where(user => user.Id == id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> SetResetTokenAsync(int id, string tokenHash, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        var affected = await db.Updateable<User>()
            .SetColumns(user => new User
            {
                ResetTokenHash = tokenHash,
                ResetTokenExpiresAt = expiresAt,
            })
            .Where(user => user.Id == id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <summary>用户名归一化：去空白并转小写，用于不区分大小写的查询。</summary>
    /// <param name="username">原始用户名。</param>
    /// <returns>归一化后的用户名。</returns>
    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
