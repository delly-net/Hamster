using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 分类业务服务：账套内分类字典的查询与维护。
/// </summary>
/// <remarks>
/// 分类**按账套隔离**，故本服务的方法**都带 <c>accountSetId</c> 参数**——这与
/// <see cref="ICurrencyService"/>（全局字典、方法一律不带账套维度）的方向**刚好相反**，
/// 勿为「统一风格」给币种补一个账套维度、或把账套维度从本服务里去掉。
/// <para>
/// 删除采用软删除（<see cref="Category.IsActive"/>），不提供物理删除：历史流水挂靠分类，
/// 物理删除会让既有明细的分类凭空消失。
/// </para>
/// <para>
/// **本服务不做可见性判定**：分类没有可见性维度——同一账套的所有成员看到的是同一份完整列表，
/// 没有他人私有的分类这种概念。故端点层无需像账户那样先过滤再查询；
/// 唯一的防护是「一切查询都限定在 <c>accountSetId</c> 内」，
/// 跨账套的分类在 <see cref="FindAsync"/> 下与「不存在」同响应。
/// </para>
/// </remarks>
public interface ICategoryService
{
    /// <summary>
    /// 列出某账套内的分类，按主键升序（即创建先后）。
    /// </summary>
    /// <param name="accountSetId">账套主键；只返回该账套的分类。</param>
    /// <param name="includeInactive">是否包含已停用的分类。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分类列表；供记账表单（只要启用）与分类管理页（含停用）。</returns>
    /// <remarks>
    /// 按主键升序而不是另设排序列：分类全部由用户自己建立，建立先后即是最自然的次序，
    /// 而「常用在前」这类公认次序对分类并不存在（币种有，故有 <see cref="Currency.SortOrder"/>）。
    /// </remarks>
    Task<IReadOnlyList<Category>> ListByAccountSetAsync(
        int accountSetId,
        bool includeInactive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键取**该账套内**的分类（含已停用）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="id">分类主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分类；不存在、或存在但不属于该账套时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 账套条件写在查询里而不是取回后再比对：调用方（端点）对两种情形给出同一个响应，
    /// 分开判只会多出一处需要同步维护的分支。
    /// </remarks>
    Task<Category?> FindAsync(int accountSetId, int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按名称取**该账套内**的分类（不区分大小写）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="name">分类名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分类；不存在时返回 <c>null</c>。命中的可能是已停用的分类。</returns>
    /// <remarks>
    /// 供「记账时手工输入分类名」这条路径使用：命中即直接挂上，未命中才自动创建。
    /// <para>
    /// **命中已停用的分类时照常返回它**：停用是「不再出现在候选里」，不是「不可再被引用」。
    /// 用户在记账时把名字原样敲出来，说明他确实要用这个分类；此时另建一个同名的新分类，
    /// 只会让同一个名字在库里存在两行、把历史明细拆到两处。
    /// </para>
    /// </remarks>
    Task<Category?> FindByNameAsync(int accountSetId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断分类名称在该账套内是否已被占用（不区分大小写）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="name">待校验的名称。</param>
    /// <param name="excludeId">需排除的分类主键，用于「编辑时允许沿用自身名称」；新增时传 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已被占用返回 <c>true</c>。</returns>
    Task<bool> IsNameTakenAsync(
        int accountSetId,
        string name,
        int? excludeId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在某账套内新建分类。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="name">分类名称（调用方需保证已 Trim 且非空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的分类实体。</returns>
    /// <remarks>
    /// 新建的分类**一律启用**：自动创建与手工新建共用本方法，两种来路都不该有「建出来就是停用的」
    /// 这种意外状态。
    /// </remarks>
    Task<Category> CreateAsync(int accountSetId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改分类名称。
    /// </summary>
    /// <param name="category">目标分类，必须是 <see cref="FindAsync"/> 取得的实体。</param>
    /// <param name="name">新名称（调用方需保证已 Trim 且非空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>。</returns>
    /// <remarks>
    /// **只有名称**：所属账套不在参数中，它是分类的归属，中途改归属等于把它从一家的字典搬到另一家，
    /// 而挂在它上面的历史流水并不跟着搬家。让「不可改」成为编译期事实，未来新增调用点不可能误传一个账套进来。
    /// <para>
    /// 改名**不产生任何连带影响**：流水挂的是分类主键而非名称，改名后历史明细自动显示新名字——
    /// 这正是挂主键而非存名称字符串的意义。
    /// </para>
    /// </remarks>
    Task<bool> UpdateAsync(Category category, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 启用或停用分类（软删除与恢复）。
    /// </summary>
    /// <param name="category">目标分类，必须是 <see cref="FindAsync"/> 取得的实体。</param>
    /// <param name="isActive">目标状态：<c>false</c> 为停用，<c>true</c> 为启用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>。</returns>
    /// <remarks>
    /// 停用后不再出现在记账表单的分类候选中，但**已挂该分类的历史流水照常显示其名称**：
    /// 停用是「不再供新记账选择」，不是「历史上从未用过」。与账户软删除同一口径。
    /// </remarks>
    Task<bool> SetActiveAsync(Category category, bool isActive, CancellationToken cancellationToken = default);
}
