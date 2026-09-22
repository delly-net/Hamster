using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的账户业务实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
public sealed class AccountService(ISqlSugarClient db) : IAccountService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountWithOwner>> ListByAccountSetAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var accounts = await db.Queryable<Account>()
            .Where(account => account.AccountSetId == accountSetId)
            // 可见性条件在此处只写一次；单条查询（FindVisibleAsync）复用同一表达式，避免两处漂移
            .WhereIF(!isAdmin, account => account.Scope == AccountScope.Public || account.OwnerUserId == userId)
            .WhereIF(!includeInactive, account => account.IsActive)
            .OrderBy(account => account.Id)
            .ToListAsync(cancellationToken);

        var ownerNames = await LoadOwnerNamesAsync(accounts, cancellationToken);

        return accounts
            .Select(account => new AccountWithOwner(account, ResolveOwnerName(account, ownerNames)))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<Account?> FindVisibleAsync(
        int id,
        int accountSetId,
        int userId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        var matched = await db.Queryable<Account>()
            .Where(account => account.Id == id && account.AccountSetId == accountSetId)
            // 与列表同一可见性条件：杜绝「列表过滤了、单条没过」的越权缺口
            .WhereIF(!isAdmin, account => account.Scope == AccountScope.Public || account.OwnerUserId == userId)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<bool> IsNameTakenAsync(
        int accountSetId,
        AccountScope scope,
        int? ownerUserId,
        string name,
        int? excludeId,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(name);
        var matched = await db.Queryable<Account>()
            .Where(account => account.AccountSetId == accountSetId)
            .Where(account => account.Scope == scope)
            .Where(account => account.Name.ToLower() == normalized)
            // 公共账户之间互斥（归属人为 null），每个用户的个人账户各自互斥。
            // 刻意分成两个 WhereIF 而非 `account.OwnerUserId == ownerUserId`：后者在 ownerUserId
            // 为 null 时会拼出 `owner_user_id = NULL`，该表达式永不成立，等于把「公共账户重名」放行。
            .WhereIF(ownerUserId is null, account => account.OwnerUserId == null)
            .WhereIF(ownerUserId is not null, account => account.OwnerUserId == ownerUserId)
            .WhereIF(excludeId is not null, account => account.Id != excludeId)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count > 0;
    }

    /// <inheritdoc />
    public async Task<Account> CreateAsync(
        int accountSetId,
        string name,
        AccountScope scope,
        AccountType type,
        decimal initialBalance,
        int creatorUserId,
        CancellationToken cancellationToken = default)
    {
        var account = new Account
        {
            AccountSetId = accountSetId,
            Name = name.Trim(),
            Scope = scope,
            // 归属人由服务端按归属范围决定，不接受调用方指定：个人账户恒为创建者，公共账户恒为 null
            OwnerUserId = scope == AccountScope.Personal ? creatorUserId : null,
            Type = type,
            InitialBalance = NormalizeBalance(initialBalance),
            IsActive = true,
        };

        account.Id = await db.Insertable(account).ExecuteReturnIdentityAsync(cancellationToken);
        return account;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Account account,
        string name,
        AccountType type,
        decimal initialBalance,
        CancellationToken cancellationToken = default)
    {
        // 只更新可变的三个字段：account_set_id / scope / owner_user_id 一经创建不可修改
        var affected = await db.Updateable<Account>()
            .SetColumns(target => new Account
            {
                Name = name.Trim(),
                Type = type,
                InitialBalance = NormalizeBalance(initialBalance),
            })
            .Where(target => target.Id == account.Id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(
        Account account,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var affected = await db.Updateable<Account>()
            .SetColumns(target => new Account { IsActive = isActive })
            .Where(target => target.Id == account.Id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <summary>账户名称归一化：去空白并转小写，用于不区分大小写的查重。</summary>
    /// <param name="name">原始名称。</param>
    /// <returns>归一化后的名称。</returns>
    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    /// <summary>期初金额归一化：统一收敛到两位小数，避免浮点运算残留的尾数落库。</summary>
    /// <param name="balance">原始金额。</param>
    /// <returns>四舍五入到两位小数的金额。</returns>
    private static decimal NormalizeBalance(decimal balance) => decimal.Round(balance, 2);

    /// <summary>
    /// 批量取回个人账户归属人的用户名，供列表一次性展示「这是谁的个人账户」。
    /// </summary>
    /// <param name="accounts">本次列表结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>归属人主键到用户名的映射；结果中无个人账户时为空字典（不查用户表）。</returns>
    /// <remarks>
    /// 先收集去重后的归属人主键再一次性查询，而非逐行查用户表：后者是典型的 N+1。
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, string>> LoadOwnerNamesAsync(
        IReadOnlyList<Account> accounts,
        CancellationToken cancellationToken)
    {
        var ownerIds = accounts
            .Where(account => account.OwnerUserId is not null)
            .Select(account => account.OwnerUserId!.Value)
            .Distinct()
            .ToArray();

        if (ownerIds.Length == 0)
        {
            return new Dictionary<int, string>();
        }

        var owners = await db.Queryable<User>()
            .Where(user => ownerIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Username })
            .ToListAsync(cancellationToken);

        return owners.ToDictionary(row => row.Id, row => row.Username);
    }

    /// <summary>取账户的归属人用户名；公共账户返回 <c>null</c>，归属人已被删除时也返回 <c>null</c>。</summary>
    /// <param name="account">账户实体。</param>
    /// <param name="ownerNames">归属人主键到用户名的映射。</param>
    /// <returns>归属人用户名或 <c>null</c>。</returns>
    private static string? ResolveOwnerName(Account account, IReadOnlyDictionary<int, string> ownerNames)
    {
        if (account.OwnerUserId is not { } ownerId)
        {
            return null;
        }

        return ownerNames.TryGetValue(ownerId, out var username) ? username : null;
    }
}
