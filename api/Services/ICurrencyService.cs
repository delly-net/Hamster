using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 币种业务服务：全系统共用的币种字典的查询与维护。
/// </summary>
/// <remarks>
/// 币种**不归属账套**：它是一份全局字典，各账套的账户都指向同一份数据。
/// 因此本服务的方法**都不带 <c>accountSetId</c> 参数**——这与 <see cref="IAccountService"/> 的方向
/// 刚好相反，勿为「统一风格」给它补一个账套维度。
/// <para>
/// 删除采用软删除（<see cref="Currency.IsActive"/>），不提供物理删除：账户会绑定币种，
/// 物理删除会让既有账户指向不存在的币种、历史金额失去计价单位。
/// </para>
/// </remarks>
public interface ICurrencyService
{
    /// <summary>
    /// 列出**启用**的币种，按排序值与主键升序。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启用币种列表；供记账表单与账户新建表单的币种候选。</returns>
    Task<IReadOnlyList<Currency>> ListActiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出全部币种（含已停用），按排序值与主键升序。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部币种列表；供管理页。</returns>
    Task<IReadOnlyList<Currency>> ListAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 取系统默认币种。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 默认币种；**未被设置时回退到第一个启用币种**（按排序值升序）。
    /// 回退而非返回 <c>null</c>：默认币种是新建账户与历史回填的兜底取值，调用方总需要一个可用值；
    /// 把它为空的处理推给每个调用点，只会得到若干份互不一致的兜底逻辑。
    /// </returns>
    Task<Currency?> GetDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断币种代码是否已被占用（不区分大小写）。
    /// </summary>
    /// <param name="code">待校验的代码。</param>
    /// <param name="excludeId">需排除的币种主键，用于「编辑时允许沿用自身代码」；新增时传 <c>null</c>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>已被占用返回 <c>true</c>。</returns>
    Task<bool> IsCodeTakenAsync(string code, int? excludeId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 判断某代码是否为**存在的启用**币种。
    /// </summary>
    /// <param name="code">币种代码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在且启用返回 <c>true</c>。</returns>
    /// <remarks>
    /// 供账户新建与记账两个入口校验传入的币种：**停用币种不接受新绑定**，
    /// 否则「停用」就挡不住新数据继续引用它，停用形同虚设。
    /// </remarks>
    Task<bool> ExistsActiveAsync(string? code, CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键取币种（含已停用）。
    /// </summary>
    /// <param name="id">币种主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>币种；不存在时返回 <c>null</c>。</returns>
    Task<Currency?> FindAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// 新建币种。
    /// </summary>
    /// <param name="code">币种代码（调用方需保证已 Trim 且未被占用）。</param>
    /// <param name="name">币种中文名（调用方需保证已 Trim 且非空）。</param>
    /// <param name="symbol">币种符号；无符号时传 <c>null</c>。</param>
    /// <param name="sortOrder">呈现顺序。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的币种实体。</returns>
    /// <remarks>
    /// 新建的币种**一律启用、且不是默认币种**：默认币种由 <see cref="SetDefaultAsync"/> 显式指定，
    /// 若新建时偷偷认领默认身份，默认币种就会随最后一次新建而漂移。
    /// </remarks>
    Task<Currency> CreateAsync(
        string code,
        string name,
        string? symbol,
        int sortOrder,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 修改币种的名称、符号与排序。
    /// </summary>
    /// <param name="currency">目标币种，必须是 <see cref="FindAsync"/> 取得的实体。</param>
    /// <param name="name">新名称（调用方需保证已 Trim 且非空）。</param>
    /// <param name="symbol">新符号；无符号时传 <c>null</c>。</param>
    /// <param name="sortOrder">新排序值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>。</returns>
    /// <remarks>
    /// **代码不在参数中**：它是币种的身份，账户按代码绑定币种，中途改代码等于让所有已绑定的账户
    /// 指向另一个币种。让「不可改」成为编译期事实，未来新增调用点不可能误传一个代码进来。
    /// </remarks>
    Task<bool> UpdateAsync(
        Currency currency,
        string name,
        string? symbol,
        int sortOrder,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 启用或停用币种（软删除与恢复）。
    /// </summary>
    /// <param name="currency">目标币种，必须是 <see cref="FindAsync"/> 取得的实体。</param>
    /// <param name="isActive">目标状态：<c>false</c> 为停用，<c>true</c> 为启用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0 返回 <c>true</c>。</returns>
    /// <remarks>
    /// **默认币种不可停用**：默认币种是新建账户与历史回填的兜底取值，停用它会让这两条路径
    /// 同时失去可用值。调用方（端点）在调用前拦下并给出 400，本方法不重复校验——与
    /// <see cref="IAccountService"/> 「前置条件由端点层拦下」的取舍一致。
    /// </remarks>
    Task<bool> SetActiveAsync(Currency currency, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>
    /// 把指定币种设为系统默认币种。
    /// </summary>
    /// <param name="currency">目标币种，必须是 <see cref="FindAsync"/> 取得的实体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>受影响行数大于 0（即确有变更或本身已是默认）返回 <c>true</c>。</returns>
    /// <remarks>
    /// 在一个事务内**先清掉其余行的默认标记、再置本行**：这样「全表至多一个默认」这一不变量
    /// 在任何中间态下都成立，不会出现短暂的零个或两个默认币种。
    /// 停用币种不得设为默认，由调用方拦下。
    /// </remarks>
    Task<bool> SetDefaultAsync(Currency currency, CancellationToken cancellationToken = default);
}
