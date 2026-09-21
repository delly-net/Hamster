using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的账套业务实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
public sealed class AccountSetService(ISqlSugarClient db) : IAccountSetService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountSetWithMemberCount>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var accountSets = await db.Queryable<AccountSet>()
            .OrderBy(accountSet => accountSet.Id)
            .ToListAsync(cancellationToken);

        if (accountSets.Count == 0)
        {
            return [];
        }

        // association 一次性按账套分组统计，避免逐行查询关联数造成的 N+1
        var counts = await db.Queryable<AccountSetMember>()
            .GroupBy(member => member.AccountSetId)
            .Select(member => new { AccountSetId = member.AccountSetId, Count = SqlFunc.AggregateCount(member.Id) })
            .ToListAsync(cancellationToken);

        var countByAccountSetId = counts.ToDictionary(row => row.AccountSetId, row => row.Count);
        return accountSets
            .Select(accountSet => new AccountSetWithMemberCount(
                accountSet,
                countByAccountSetId.GetValueOrDefault(accountSet.Id)))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountSet>> ListForUserAsync(
        int userId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        // 管理员无需显式关联即可访问全部账套——这是「管理员全可见」的唯一判定点
        if (isAdmin)
        {
            return await db.Queryable<AccountSet>()
                .OrderBy(accountSet => accountSet.Id)
                .ToListAsync(cancellationToken);
        }

        var accountSetIds = await db.Queryable<AccountSetMember>()
            .Where(member => member.UserId == userId)
            .Select(member => member.AccountSetId)
            .ToListAsync(cancellationToken);

        if (accountSetIds.Count == 0)
        {
            return [];
        }

        return await db.Queryable<AccountSet>()
            .Where(accountSet => accountSetIds.Contains(accountSet.Id))
            .OrderBy(accountSet => accountSet.Id)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AccountSet?> FindByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var accountSets = await db.Queryable<AccountSet>()
            .Where(accountSet => accountSet.Id == id)
            .Take(1)
            .ToListAsync(cancellationToken);

        return accountSets.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<bool> IsNameTakenAsync(string name, int? excludeId, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(name);
        var matched = await db.Queryable<AccountSet>()
            .Where(accountSet => accountSet.Name.ToLower() == normalized)
            .WhereIF(excludeId is not null, accountSet => accountSet.Id != excludeId)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count > 0;
    }

    /// <inheritdoc />
    public async Task<AccountSet> CreateAsync(
        string name,
        string? remark,
        CancellationToken cancellationToken = default)
    {
        var accountSet = new AccountSet
        {
            Name = name.Trim(),
            Remark = NormalizeRemark(remark),
        };

        accountSet.Id = await db.Insertable(accountSet).ExecuteReturnIdentityAsync(cancellationToken);
        return accountSet;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        int id,
        string name,
        string? remark,
        CancellationToken cancellationToken = default)
    {
        var affected = await db.Updateable<AccountSet>()
            .SetColumns(accountSet => new AccountSet
            {
                Name = name.Trim(),
                Remark = NormalizeRemark(remark),
            })
            .Where(accountSet => accountSet.Id == id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        // 关联行与账套在同一事务内删除：否则中途失败会留下指向已删账套的孤儿关联
        var deleted = false;
        await db.Ado.UseTranAsync(async () =>
        {
            await db.Deleteable<AccountSetMember>()
                .Where(member => member.AccountSetId == id)
                .ExecuteCommandAsync(cancellationToken);

            deleted = await db.Deleteable<AccountSet>()
                .Where(accountSet => accountSet.Id == id)
                .ExecuteCommandAsync(cancellationToken) > 0;
        });

        return deleted;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListMemberIdsAsync(int accountSetId, CancellationToken cancellationToken = default)
    {
        return await db.Queryable<AccountSetMember>()
            .Where(member => member.AccountSetId == accountSetId)
            .OrderBy(member => member.UserId)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReplaceMembersAsync(
        int accountSetId,
        IReadOnlyList<int> userIds,
        CancellationToken cancellationToken = default)
    {
        // 去重后再落库：即便调用方传入重复 Id，唯一索引也不会被触发
        var candidates = userIds.Distinct().ToArray();
        var validUserIds = candidates.Length == 0
            ? []
            : await db.Queryable<User>()
                .Where(user => candidates.Contains(user.Id))
                .Select(user => user.Id)
                .ToListAsync(cancellationToken);

        // 覆盖式保存：先清空该账套的关联，再写入新集合；两步同事务，避免留下「已清空」的中间态
        await db.Ado.UseTranAsync(async () =>
        {
            await db.Deleteable<AccountSetMember>()
                .Where(member => member.AccountSetId == accountSetId)
                .ExecuteCommandAsync(cancellationToken);

            if (validUserIds.Count > 0)
            {
                var rows = validUserIds
                    .Select(userId => new AccountSetMember { AccountSetId = accountSetId, UserId = userId })
                    .ToList();

                await db.Insertable(rows).ExecuteCommandAsync(cancellationToken);
            }
        });
    }

    /// <inheritdoc />
    public async Task<bool> IsAccessibleAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        CancellationToken cancellationToken = default)
    {
        if (isAdmin)
        {
            // 管理员对「存在的账套」一律可访问，此处仍需回查，避免已删除的账套 Id 被放行
            return await FindByIdAsync(accountSetId, cancellationToken) is not null;
        }

        var matched = await db.Queryable<AccountSetMember>()
            .Where(member => member.AccountSetId == accountSetId && member.UserId == userId)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count > 0;
    }

    /// <summary>账套名称归一化：去空白并转小写，用于不区分大小写的查重。</summary>
    /// <param name="name">原始名称。</param>
    /// <returns>归一化后的名称。</returns>
    private static string Normalize(string name) => name.Trim().ToLowerInvariant();

    /// <summary>备注归一化：去空白；空串一律存 <c>null</c>，避免库中出现「有值但为空」的两种等价形态。</summary>
    /// <param name="remark">原始备注。</param>
    /// <returns>归一化后的备注或 <c>null</c>。</returns>
    private static string? NormalizeRemark(string? remark)
    {
        var trimmed = remark?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
