using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的分类业务实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <remarks>
/// 本服务只依赖 <see cref="ISqlSugarClient"/>，不依赖任何其它业务服务——
/// 它的职责是「账套内的分类字典」，与账户、交易都没有调用关系，
/// 故 <c>TransactionEndpoints</c> 可以放心注入它而不会碰到
/// <see cref="ITransactionService"/> 与 <see cref="IAccountService"/> 之间那条循环依赖红线。
/// </remarks>
public sealed class CategoryService(ISqlSugarClient db) : ICategoryService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Category>> ListByAccountSetAsync(
        int accountSetId,
        bool includeInactive,
        CancellationToken cancellationToken = default) =>
        await db.Queryable<Category>()
            .Where(category => category.AccountSetId == accountSetId)
            .WhereIF(!includeInactive, category => category.IsActive)
            .OrderBy(category => category.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Category?> FindAsync(
        int accountSetId,
        int id,
        CancellationToken cancellationToken = default)
    {
        var matched = await db.Queryable<Category>()
            .Where(category => category.AccountSetId == accountSetId && category.Id == id)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<Category?> FindByNameAsync(
        int accountSetId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0)
        {
            return null;
        }

        // 不区分大小写地比对：`ToLower()` 可被 SqlSugar 翻译成 SQL 的 lower()，
        // 与 CurrencyService.IsCodeTakenAsync 同一写法（勿改成 C# 侧的 string.Equals，
        // 那会把整张表读进内存再比）。
        // 取主键最小的那一行：查重保证了正常情况下至多一条，此处取首条是为「库里万一有历史重复行」
        // 给出一个确定答案，而不是随查询计划漂移。
        var matched = await db.Queryable<Category>()
            .Where(category => category.AccountSetId == accountSetId)
            .Where(category => category.Name.ToLower() == normalized)
            .OrderBy(category => category.Id)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<bool> IsNameTakenAsync(
        int accountSetId,
        string name,
        int? excludeId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0)
        {
            return false;
        }

        var matched = await db.Queryable<Category>()
            .Where(category => category.AccountSetId == accountSetId)
            .Where(category => category.Name.ToLower() == normalized)
            .WhereIF(excludeId is not null, category => category.Id != excludeId)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count > 0;
    }

    /// <inheritdoc />
    public async Task<Category> CreateAsync(
        int accountSetId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var category = new Category
        {
            AccountSetId = accountSetId,
            Name = name.Trim(),
            // 自动创建与手工新建共用本方法，两种来路都一律启用：
            // 不存「建出来就是停用的」这种意外状态
            IsActive = true,
        };

        category.Id = await db.Insertable(category).ExecuteReturnIdentityAsync(cancellationToken);
        return category;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Category category,
        string name,
        CancellationToken cancellationToken = default)
    {
        // 只更新名称：account_set_id 是分类的归属，不在参数中（见 ICategoryService.UpdateAsync 的说明）
        var affected = await db.Updateable<Category>()
            .SetColumns(target => new Category { Name = name.Trim() })
            .Where(target => target.Id == category.Id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(
        Category category,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var affected = await db.Updateable<Category>()
            .SetColumns(target => new Category { IsActive = isActive })
            .Where(target => target.Id == category.Id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <summary>分类名称归一化：去空白并转小写，用于不区分大小写的查重与比对。</summary>
    /// <param name="name">原始名称。</param>
    /// <returns>归一化后的名称。</returns>
    private static string Normalize(string name) => name.Trim().ToLowerInvariant();
}
