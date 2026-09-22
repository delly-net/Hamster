using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 账目明细查询服务：在当前账套内按时间区间与账户筛选交易明细，按发生时间分页输出。
/// </summary>
/// <remarks>
/// **可见性不在这里判定**：本服务复用 <see cref="IAccountService.ListByAccountSetAsync"/> 取回
/// 「当前用户可见的账户」，再以之主键集合过滤明细。刻意不另写一份可见性条件——
/// 两处判断必然漂移出「列表里看得见、明细却查不到」或更糟的「看不见却查得到」。
/// <para>
/// 账户是**软删除**，故取可见账户时一律 <c>includeInactive: true</c>：停用只是不再出现在账户列表，
/// 历史明细仍然挂在它上面，漏掉它会让过去的账凭空消失。
/// </para>
/// <para>
/// 本服务是**纯读取**：不写任何表，也不参与「明细方向 → 账户余额」的符号换算
/// （那个换算的唯一入口是 <see cref="ITransactionService.SumSignedAmountsAsync"/>）。
/// 明细行对外呈现的是「借贷方向 + 恒正金额」两列，不折算带符号金额。
/// </para>
/// </remarks>
public interface IEntryQueryService
{
    /// <summary>
    /// 分页查询交易明细。
    /// </summary>
    /// <param name="accountSetId">账套主键；只查该账套内的交易。</param>
    /// <param name="userId">当前用户主键。</param>
    /// <param name="isAdmin">是否为系统管理员；管理员可见该账套内的全部账户。</param>
    /// <param name="from">
    /// 起始时间（UTC，**含端点**）；<c>null</c> 表示不限下界。
    /// 与之比较的是交易的**业务发生时间** <see cref="Transaction.OccurredAt"/>，不是落库时间。
    /// </param>
    /// <param name="to">结束时间（UTC，**含端点**）；<c>null</c> 表示不限上界。</param>
    /// <param name="accountIds">
    /// 目标账户主键集合；<c>null</c> 或空集合表示不限账户（即全部可见账户）。
    /// **集合会与可见账户求交**：其中不可见的账户被静默剔除，不会因此报错——
    /// 否则这个参数就成了探测他人账户是否存在的探针（与 <see cref="IAccountService.FindVisibleAsync"/>
    /// 「不存在、不属于本账套、不可见三种情形同响应」的取舍一致）。
    /// </param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 一页明细及总数。明细按 <see cref="Transaction.OccurredAt"/> 升序、同一时刻按交易主键升序、
    /// 再按明细主键升序排列（第三级排序键保证分页结果稳定，不会出现「同一笔数据在两页里各出现一次」）。
    /// </returns>
    Task<EntryQueryPage> QueryAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        DateTime? from,
        DateTime? to,
        IReadOnlyCollection<int>? accountIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>明细行（已解析出账户名与对手方，供端点直接映射为 DTO）。</summary>
/// <param name="Id">明细主键。</param>
/// <param name="TransactionId">所属交易主键。</param>
/// <param name="OccurredAt">业务发生时间（UTC）。</param>
/// <param name="Summary">交易摘要。</param>
/// <param name="Remark">交易备注；无备注时为 <c>null</c>。</param>
/// <param name="Type">交易类型。</param>
/// <param name="AccountId">挂靠账户主键（必然是当前用户可见的账户）。</param>
/// <param name="AccountName">挂靠账户名称。</param>
/// <param name="Direction">借贷方向。</param>
/// <param name="Amount">金额，**恒为正**；方向由 <paramref name="Direction"/> 表达。</param>
/// <param name="CounterpartyKind">对手方账户的可见性档位。</param>
/// <param name="CounterpartyAccountId">对手方账户主键；仅 <see cref="CounterpartyKind.Account"/> 时有值。</param>
/// <param name="CounterpartyName">对手方账户名称；仅 <see cref="CounterpartyKind.Account"/> 时有值。</param>
public sealed record EntryQueryRow(
    int Id,
    int TransactionId,
    DateTime OccurredAt,
    string Summary,
    string? Remark,
    TransactionType Type,
    int AccountId,
    string AccountName,
    EntryDirection Direction,
    decimal Amount,
    CounterpartyKind CounterpartyKind,
    int? CounterpartyAccountId,
    string? CounterpartyName);

/// <summary>一页明细。</summary>
/// <param name="Items">本页明细。</param>
/// <param name="Total">满足筛选条件的明细总数（跨页）。</param>
/// <param name="Page">当前页码，从 1 开始。</param>
/// <param name="PageSize">每页条数。</param>
public sealed record EntryQueryPage(IReadOnlyList<EntryQueryRow> Items, int Total, int Page, int PageSize);

/// <summary>对手方账户相对当前用户的可见性档位。</summary>
/// <remarks>
/// 分档而非「给名称或给 null」，是为了让「不可见」这件事本身不携带任何可辨识信息：
/// 他人个人账户连主键都不外泄，前端只需按档位映射占位文案。
/// </remarks>
public enum CounterpartyKind
{
    /// <summary>
    /// 无对手方：同一交易里找不到方向相反的明细（单边明细）。
    /// 当前数据模型下每笔交易由借贷两条明细构成，**不应出现**本取值；给出它是为了如实表达
    /// 「没有对手方」，而不是把它混进「有对手方但不可见」里——后者是权限的结论，前者是数据的问题。
    /// </summary>
    None = 0,

    /// <summary>对手方对当前用户可见，附主键与名称。</summary>
    Account = 1,

    /// <summary>
    /// 对手方是**账本账户**（<see cref="AccountType.Ledger"/>）。
    /// 它由系统自动创建、对任何人不呈现，故不给主键与名称；当前数据下它只作为期初余额分录的对手方出现。
    /// </summary>
    Ledger = 2,

    /// <summary>对手方存在但对当前用户不可见（如他人的个人账户），不附任何可辨识信息。</summary>
    Hidden = 3,
}
