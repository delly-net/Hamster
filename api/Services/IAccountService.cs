using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 账户业务服务：按账套列出账户、判定可见性，以及账户的增改与停用/启用。
/// </summary>
/// <remarks>
/// **可见性判定统一收敛在本服务内**（实现见 <c>AccountService</c>），端点无需重复该分支。
/// 本任务中「可见集合 = 可改集合」：
/// <list type="bullet">
///   <item>系统管理员：账套内全部账户（含他人个人账户）。</item>
///   <item>普通用户：账套内全部公共账户 + 自己创建的个人账户。</item>
/// </list>
/// 因此无需再区分「只读」态——两套判定并行只会漂移出「能看见却改不了」，
/// 甚至更糟的「看不见却能改」。
/// </remarks>
public interface IAccountService
{
    /// <summary>
    /// 列出某账套内当前用户可见的账户，按主键升序，并附带个人账户的归属人用户名。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="userId">当前用户主键。</param>
    /// <param name="isAdmin">是否为系统管理员；管理员可见该账套内全部账户。</param>
    /// <param name="includeInactive">是否包含已停用的账户；<c>false</c> 时只返回启用的（默认视图）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>可见的账户及归属人用户名列表。</returns>
    Task<IReadOnlyList<AccountWithOwner>> ListByAccountSetAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        bool includeInactive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在指定账套内按主键查询**当前用户可见**的账户。
    /// </summary>
    /// <param name="id">账户主键。</param>
    /// <param name="accountSetId">账套主键；账户与账套不匹配时视为不可见。</param>
    /// <param name="userId">当前用户主键。</param>
    /// <param name="isAdmin">是否为系统管理员。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 可见时返回账户实体；账户不存在、不属于该账套或对当前用户不可见时一律返回 <c>null</c>
    /// （三种情形同响应，避免被用于探测他人账户是否存在）。
    /// </returns>
    Task<Account?> FindVisibleAsync(
        int id,
        int accountSetId,
        int userId,
        bool isAdmin,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断同一账套、同一归属范围内是否已存在同名账户（不区分大小写）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="scope">归属范围。</param>
    /// <param name="ownerUserId">归属人主键；公共账户传 <c>null</c>。</param>
    /// <param name="name">待校验的名称。</param>
    /// <param name="excludeId">需排除的账户主键，用于「改名时允许沿用自身名称」；新增时传 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已被占用返回 <c>true</c>。</returns>
    Task<bool> IsNameTakenAsync(
        int accountSetId,
        AccountScope scope,
        int? ownerUserId,
        string name,
        int? excludeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 新建账户。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="name">账户名称（调用方需保证已 Trim 且在该归属范围内未被占用）。</param>
    /// <param name="scope">归属范围。</param>
    /// <param name="type">账户类型。</param>
    /// <param name="initialBalance">期初金额。</param>
    /// <param name="creatorUserId">
    /// 创建者主键。个人账户的归属人一律取该值，**忽略调用方传入的任何归属人**，
    /// 否则可伪造出「归属他人的个人账户」。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的账户实体。</returns>
    /// <remarks>
    /// **期初金额会同时落成一笔期初交易**：账户与「借（或贷）目标账户、贷（或借）账本账户」的
    /// 两条明细在同一事务内写入（见 <see cref="ITransactionService.RecordOpeningBalanceAsync"/>），
    /// 账本账户不存在时按需自动创建。期初金额为 0 时只建账户、不写分录。
    /// </remarks>
    Task<Account> CreateAsync(
        int accountSetId,
        string name,
        AccountScope scope,
        AccountType type,
        decimal initialBalance,
        int creatorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改账户的名称与类型。
    /// </summary>
    /// <param name="account">
    /// 目标账户，**必须是 <see cref="FindVisibleAsync"/> 取得的实体**：
    /// 归属范围、归属人与所属账套一经创建不可修改，故不在此方法的参数中。
    /// </param>
    /// <param name="name">新名称（调用方需保证已 Trim 且未被占用）。</param>
    /// <param name="type">新类型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>；账户已被删除返回 <c>false</c>。</returns>
    /// <remarks>
    /// **期初金额不在此方法的参数中，因为它已不可修改**：期初余额一经创建即是一笔落库的期初交易
    /// （借贷两条明细），改它就必须同步改写那笔交易，而期初是既成事实而非可随意改写的设置项。
    /// 需要调整余额时应记一笔「余额调整交易」，而不是回头改期初——后者会让已经发生的账变得不可信。
    /// </remarks>
    Task<bool> UpdateAsync(
        Account account,
        string name,
        AccountType type,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 启用或停用账户（软删除与恢复）。
    /// </summary>
    /// <param name="account">目标账户，必须是 <see cref="FindVisibleAsync"/> 取得的实体。</param>
    /// <param name="isActive">目标状态：<c>false</c> 为停用，<c>true</c> 为启用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>；账户已被删除返回 <c>false</c>。</returns>
    Task<bool> SetActiveAsync(Account account, bool isActive, CancellationToken cancellationToken = default);
}

/// <summary>账户及其归属人用户名。</summary>
/// <param name="Account">账户实体。</param>
/// <param name="OwnerUsername">归属人用户名；公共账户为 <c>null</c>。</param>
public sealed record AccountWithOwner(Account Account, string? OwnerUsername);
