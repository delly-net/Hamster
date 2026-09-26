using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 标签业务服务：账套内标签字典的查询与维护。
/// </summary>
/// <remarks>
/// **与 <see cref="ICategoryService"/> 逐条同构**：标签按账套隔离，故本服务的方法**都带
/// <c>accountSetId</c> 参数**——这与 <see cref="ICurrencyService"/>（全局字典、方法一律不带账套维度）
/// 的方向刚好相反，勿为「统一风格」给币种补一个账套维度、或把账套维度从本服务里去掉。
/// <para>
/// 与分类的**唯一结构性差异是基数**：分类是交易头上的一列，标签落在 <see cref="TransactionTag"/>
/// 子表里。故本服务只管**字典**（<see cref="Tag"/> 表），不碰关联表——
/// 关联行的写入归 <c>TransactionService</c>（与交易头、两条明细同一事务），
/// 读取归 <c>EntryQueryService</c>。三者分工见 <see cref="TransactionTag"/> 的类头注释。
/// </para>
/// <para>
/// 删除采用软删除（<see cref="Tag.IsActive"/>），不提供物理删除：历史流水挂靠标签，
/// 物理删除会让既有明细的标签凭空消失。
/// </para>
/// <para>
/// **本服务不做可见性判定**：标签没有可见性维度——同一账套的所有成员看到的是同一份完整列表，
/// 没有他人私有的标签这种概念。故端点层无需像账户那样先过滤再查询；
/// 唯一的防护是「一切查询都限定在 <c>accountSetId</c> 内」，
/// 跨账套的标签在 <see cref="FindAsync"/> 下与「不存在」同响应。
/// </para>
/// </remarks>
public interface ITagService
{
    /// <summary>
    /// 列出某账套内的标签，按主键升序（即创建先后）。
    /// </summary>
    /// <param name="accountSetId">账套主键；只返回该账套的标签。</param>
    /// <param name="includeInactive">是否包含已停用的标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标签列表；供记账表单（只要启用）与标签管理页（含停用）。</returns>
    /// <remarks>
    /// 按主键升序而不是另设排序列：理由与 <see cref="ICategoryService.ListByAccountSetAsync"/> 相同——
    /// 标签全部由用户自己建立，建立先后即是最自然的次序。
    /// </remarks>
    Task<IReadOnlyList<Tag>> ListByAccountSetAsync(
        int accountSetId,
        bool includeInactive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键取**该账套内**的标签（含已停用）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="id">标签主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标签；不存在、或存在但不属于该账套时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 账套条件写在查询里而不是取回后再比对：调用方（端点）对两种情形给出同一个响应，
    /// 分开判只会多出一处需要同步维护的分支。
    /// <para>
    /// 与分类的同名方法相比，本方法还有一个新调用方：记账写入路径要按主键批量取回标签实体，
    /// 供「标签与交易必须同账套」这条不变量判定（见 <c>TransactionService.EnsureWriteInvariants</c>）。
    /// 批量取回那份写在 <c>TransactionService</c> 里（它已经持有 <c>ISqlSugarClient</c>），
    /// 不为它在本接口上再开一个「按一组主键查」的方法——那样会让同一个判据出现两份实现。
    /// </para>
    /// </remarks>
    Task<Tag?> FindAsync(int accountSetId, int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按名称取**该账套内**的标签（不区分大小写）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="name">标签名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>标签；不存在时返回 <c>null</c>。命中的可能是已停用的标签。</returns>
    /// <remarks>
    /// 供「记账时手工输入标签名」这条路径使用：命中即直接挂上，未命中才自动创建。
    /// <para>
    /// **命中已停用的标签时照常返回它**：停用是「不再出现在候选里」，不是「不可再被引用」。
    /// 用户在记账时把名字原样敲出来，说明他确实要用这个标签；此时另建一个同名的新标签，
    /// 只会让同一个名字在库里存在两行、把历史明细拆到两处。
    /// </para>
    /// </remarks>
    Task<Tag?> FindByNameAsync(int accountSetId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断标签名称在该账套内是否已被占用（不区分大小写）。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="name">待校验的名称。</param>
    /// <param name="excludeId">需排除的标签主键，用于「编辑时允许沿用自身名称」；新增时传 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已被占用返回 <c>true</c>。</returns>
    Task<bool> IsNameTakenAsync(
        int accountSetId,
        string name,
        int? excludeId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 在某账套内新建标签。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="name">标签名称（调用方需保证已 Trim 且非空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的标签实体。</returns>
    /// <remarks>
    /// 新建的标签**一律启用**：自动创建与手工新建共用本方法，两种来路都不该有「建出来就是停用的」
    /// 这种意外状态。
    /// </remarks>
    Task<Tag> CreateAsync(int accountSetId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改标签名称。
    /// </summary>
    /// <param name="tag">目标标签，必须是 <see cref="FindAsync"/> 取得的实体。</param>
    /// <param name="name">新名称（调用方需保证已 Trim 且非空）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>。</returns>
    /// <remarks>
    /// **只有名称**：所属账套不在参数中，它是标签的归属，中途改归属等于把它从一家的词汇表搬到另一家，
    /// 而挂着它的历史流水并不跟着搬家。让「不可改」成为编译期事实，未来新增调用点不可能误传一个账套进来。
    /// <para>
    /// 改名**不产生任何连带影响**：流水挂的是标签主键而非名称，改名后历史明细自动显示新名字——
    /// 这正是挂主键而非存名称字符串的意义。
    /// </para>
    /// </remarks>
    Task<bool> UpdateAsync(Tag tag, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// 启用或停用标签（软删除与恢复）。
    /// </summary>
    /// <param name="tag">目标标签，必须是 <see cref="FindAsync"/> 取得的实体。</param>
    /// <param name="isActive">目标状态：<c>false</c> 为停用，<c>true</c> 为启用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>。</returns>
    /// <remarks>
    /// 停用后不再出现在记账表单与明细页筛选区的标签候选中，但**挂着它的历史流水照常在明细页显示其名称**：
    /// 停用是「不再供新记账选择」，不是「历史上从未用过」。与账户、分类软删除同一口径。
    /// </remarks>
    Task<bool> SetActiveAsync(Tag tag, bool isActive, CancellationToken cancellationToken = default);
}
