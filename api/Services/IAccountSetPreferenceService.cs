namespace Hamster.Api.Services;

/// <summary>
/// 个人账套配置服务：读写「某个用户在某本账套里」的个人界面设置。
/// </summary>
/// <remarks>
/// **本接口只有「账目明细页筛选条件」一组方法，且方法名里带 <c>EntryFilter</c>**：
/// 表是按列扩展的（见 <c>AccountSetPreference</c> 的类头注释），将来别的页面要记自己的设置时，
/// 在同一个服务上再加一组 <c>XxxFilter</c> 方法即可——本接口刻意不叫
/// 「GetPreference / SetPreference」这种不表明内容的通用名，
/// 那会把「这里存了什么」这件事从签名里抹掉，调用方只能靠翻实现才知道。
/// <para>
/// **一切方法都带 <c>accountSetId</c> + <c>userId</c> 两个维度**：配置按「账套 + 用户」唯一，
/// 少任何一个都会读到别人的配置（同 <see cref="ITagService"/> 一律带账套维度的取舍）。
/// </para>
/// </remarks>
public interface IAccountSetPreferenceService
{
    /// <summary>
    /// 读取账目明细页筛选条件。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="userId">配置归属人主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 该用户在该账套里保存的账户与标签主键；**没有保存过时返回两个空集合**。
    /// </returns>
    /// <remarks>
    /// **「从未保存过」与「保存了一份空条件」在这里不作区分**（都是一行零长度的主键串、
    /// 以及「查无此行」时返回的两个空集合）：两者的处置完全相同——
    /// 调用方一律回落到默认视图（账户全选、标签不限），故为一个不会产生任何行为差异的差别
    /// 增加一个「是否保存过」的标志位，只会多出一个必须被正确传递和维护的字段。
    /// <para>
    /// **返回值不按当前候选收敛**：本方法如实返回库里存着的那一份，收敛只发生在写入侧
    /// （见 <see cref="SaveEntryFilterAsync"/>）。读的时候再收一次，会让「存进去 10 个、读回来 8 个」
    /// 这种状态无从解释——要收敛的地方是「写进去的本来就都是有效的」。
    /// </para>
    /// </remarks>
    Task<EntryFilterPreference> GetEntryFilterAsync(
        int accountSetId,
        int userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存账目明细页筛选条件（不存在则新建，存在则原地改写）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="userId">配置归属人主键。</param>
    /// <param name="isAdmin">
    /// 是否为系统管理员；账户可见性判定要用它（管理员的可见账户更多，见 <see cref="IAccountService"/>）。
    /// </param>
    /// <param name="accountIds">选中的账户主键；<c>null</c> 或空集合即存为空。</param>
    /// <param name="tagIds">选中的标签主键；<c>null</c> 或空集合即存为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <remarks>
    /// **写入前一律按当前账套收敛**：不属于本账套的主键、以及对本用户不可见的账户被**静默丢弃**，
    /// 而不是报错。理由与账目明细查询的账户/标签参数逐字相同（见 <see cref="IEntryQueryService.QueryAsync"/>）：
    /// 对「传了别人的主键」报错、对「传了不存在的主键」报错、而对存在的主键沉默，
    /// 三者组合起来就是一个能探出「某主键是否属于别人」的探针。
    /// <para>
    /// 收敛用的判据与页面候选**同源**（账户走 <see cref="IAccountService.ListByAccountSetAsync"/>、
    /// 标签走 <see cref="ITagService.ListByAccountSetAsync"/>），保证「存进去的一定在候选里」——
    /// 若另写一份条件，两边迟早漂移出「存进去的账户恢复时勾不出来」这种幽灵状态。
    /// </para>
    /// <para>
    /// 顺序沿用**入参首次出现的顺序**（同交易标签关联行的口径）：当前消费方只把结果当集合用，
    /// 但如实保序不需要额外代价，且将来若有「按上次的顺序呈现」的需求，这里已经是对的。
    /// </para>
    /// </remarks>
    Task SaveEntryFilterAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        IReadOnlyCollection<int>? accountIds,
        IReadOnlyCollection<int>? tagIds,
        CancellationToken cancellationToken = default);
}

/// <summary>账目明细页的筛选条件（服务层表示）。</summary>
/// <param name="AccountIds">选中的账户主键；空表示没有保存过。</param>
/// <param name="TagIds">选中的标签主键；空表示不限标签。</param>
/// <remarks>
/// 与端点层的 DTO 形状相同却**各自定义**：服务层不引用端点层的类型
/// （依赖方向是端点 → 服务，反过来会让服务层无法独立演进，同 <see cref="EntryTag"/> 的取舍）。
/// </remarks>
public sealed record EntryFilterPreference(IReadOnlyList<int> AccountIds, IReadOnlyList<int> TagIds);
