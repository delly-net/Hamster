using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的标签业务实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <remarks>
/// 本服务只依赖 <see cref="ISqlSugarClient"/>，不依赖任何其它业务服务——
/// 它的职责是「账套内的标签字典」，与账户、交易都没有调用关系，
/// 故 <c>TransactionEndpoints</c> 可以放心注入它而不会碰到
/// <see cref="ITransactionService"/> 与 <see cref="IAccountService"/> 之间那条循环依赖红线。
/// <para>
/// **本服务不碰 <see cref="TransactionTag"/>**：关联行的写入要跟交易头、两条明细同处一个事务，
/// 只能在 <c>TransactionService</c> 里做；读取在 <c>EntryQueryService</c>。
/// 让字典维护与关联维护混在一个服务里，会诱使调用方在事务外挂标签。
/// </para>
/// </remarks>
public sealed class TagService(ISqlSugarClient db) : ITagService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Tag>> ListByAccountSetAsync(
        int accountSetId,
        bool includeInactive,
        CancellationToken cancellationToken = default) =>
        await db.Queryable<Tag>()
            .Where(tag => tag.AccountSetId == accountSetId)
            .WhereIF(!includeInactive, tag => tag.IsActive)
            .OrderBy(tag => tag.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Tag?> FindAsync(
        int accountSetId,
        int id,
        CancellationToken cancellationToken = default)
    {
        var matched = await db.Queryable<Tag>()
            .Where(tag => tag.AccountSetId == accountSetId && tag.Id == id)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<Tag?> FindByNameAsync(
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
        // 与 CategoryService.FindByNameAsync 同一写法（勿改成 C# 侧的 string.Equals，
        // 那会把整张表读进内存再比）。
        // 取主键最小的那一行：查重保证了正常情况下至多一条，此处取首条是为「库里万一有历史重复行」
        // 给出一个确定答案，而不是随查询计划漂移。
        var matched = await db.Queryable<Tag>()
            .Where(tag => tag.AccountSetId == accountSetId)
            .Where(tag => tag.Name.ToLower() == normalized)
            .OrderBy(tag => tag.Id)
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

        var matched = await db.Queryable<Tag>()
            .Where(tag => tag.AccountSetId == accountSetId)
            .Where(tag => tag.Name.ToLower() == normalized)
            .WhereIF(excludeId is not null, tag => tag.Id != excludeId)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count > 0;
    }

    /// <inheritdoc />
    public async Task<Tag> CreateAsync(
        int accountSetId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var tag = new Tag
        {
            AccountSetId = accountSetId,
            Name = name.Trim(),
            // 自动创建与手工新建共用本方法，两种来路都一律启用：
            // 不存「建出来就是停用的」这种意外状态
            IsActive = true,
        };

        tag.Id = await db.Insertable(tag).ExecuteReturnIdentityAsync(cancellationToken);
        return tag;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Tag tag,
        string name,
        CancellationToken cancellationToken = default)
    {
        // 只更新名称：account_set_id 是标签的归属，不在参数中（见 ITagService.UpdateAsync 的说明）
        var affected = await db.Updateable<Tag>()
            .SetColumns(target => new Tag { Name = name.Trim() })
            .Where(target => target.Id == tag.Id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(
        Tag tag,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var affected = await db.Updateable<Tag>()
            .SetColumns(target => new Tag { IsActive = isActive })
            .Where(target => target.Id == tag.Id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <summary>标签名称归一化：去空白并转小写，用于不区分大小写的查重与比对。</summary>
    /// <param name="name">原始名称。</param>
    /// <returns>归一化后的名称。</returns>
    private static string Normalize(string name) => name.Trim().ToLowerInvariant();
}
