using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 账套及其与用户关联关系的业务服务。
/// </summary>
/// <remarks>
/// 是否可见的判定统一收敛在服务层：管理员返回全部账套，普通用户返回被关联的账套。
/// 各端点无需重复该分支，避免出现「某个端点漏判、普通用户看光全部账套」的缺口。
/// </remarks>
public interface IAccountSetService
{
    /// <summary>
    /// 列出全部账套，按主键升序，并附带各自的关联用户数。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>账套及关联用户数列表。</returns>
    Task<IReadOnlyList<AccountSetWithMemberCount>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出指定用户可访问的账套，按主键升序。
    /// </summary>
    /// <param name="userId">用户主键。</param>
    /// <param name="isAdmin">是否为系统管理员；管理员无需关联即可访问全部账套。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可访问的账套列表。</returns>
    Task<IReadOnlyList<AccountSet>> ListForUserAsync(int userId, bool isAdmin, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键查询账套。
    /// </summary>
    /// <param name="id">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的账套；不存在时返回 <c>null</c>。</returns>
    Task<AccountSet?> FindByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断账套名称是否已被占用（不区分大小写）。
    /// </summary>
    /// <param name="name">待校验的名称（调用方需保证已 Trim）。</param>
    /// <param name="excludeId">需排除的账套主键，用于「改名时允许沿用自身名称」；新增时传 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已被占用返回 <c>true</c>。</returns>
    Task<bool> IsNameTakenAsync(string name, int? excludeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 新建账套。
    /// </summary>
    /// <param name="name">账套名称（调用方需保证已 Trim 且未被占用）。</param>
    /// <param name="remark">备注，可为 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的账套实体。</returns>
    Task<AccountSet> CreateAsync(string name, string? remark, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新账套名称与备注。
    /// </summary>
    /// <param name="id">账套主键。</param>
    /// <param name="name">新名称（调用方需保证已 Trim 且未被占用）。</param>
    /// <param name="remark">新备注，可为 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>；账套不存在返回 <c>false</c>。</returns>
    Task<bool> UpdateAsync(int id, string name, string? remark, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除账套，并同时清理其全部关联行。
    /// </summary>
    /// <param name="id">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>账套存在并被删除返回 <c>true</c>；不存在返回 <c>false</c>。</returns>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出某账套已关联的用户主键。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已关联的用户主键列表，按用户主键升序。</returns>
    Task<IReadOnlyList<int>> ListMemberIdsAsync(int accountSetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 覆盖式设置某账套的关联用户：整体替换为给定集合。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="userIds">目标用户主键集合；其中不存在的用户会被忽略。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task ReplaceMembersAsync(int accountSetId, IReadOnlyList<int> userIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断用户能否访问指定账套。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="userId">用户主键。</param>
    /// <param name="isAdmin">是否为系统管理员；管理员对存在的账套一律可访问。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可访问返回 <c>true</c>；账套不存在或未关联返回 <c>false</c>。</returns>
    Task<bool> IsAccessibleAsync(int accountSetId, int userId, bool isAdmin, CancellationToken cancellationToken = default);
}

/// <summary>账套及其关联用户数。</summary>
/// <param name="AccountSet">账套实体。</param>
/// <param name="MemberCount">该账套已关联的用户数。</param>
public sealed record AccountSetWithMemberCount(AccountSet AccountSet, int MemberCount);
