using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 【示例服务】基于 SqlSugar 的账户业务实现，演示异步查询与写入的标准写法。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
public sealed class SampleAccountService(ISqlSugarClient db) : ISampleAccountService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SampleAccount>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await db.Queryable<SampleAccount>()
            .OrderBy(account => account.Id)
            .ToListAsync(cancellationToken);

        return accounts;
    }

    /// <inheritdoc />
    public async Task<SampleAccount> CreateAsync(string name, decimal balance, CancellationToken cancellationToken = default)
    {
        var account = new SampleAccount
        {
            Name = name,
            Balance = balance,
        };

        account.Id = await db.Insertable(account).ExecuteReturnIdentityAsync(cancellationToken);
        return account;
    }
}
