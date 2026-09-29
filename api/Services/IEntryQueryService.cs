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
/// **明细行只落在钱账户上**：可见账户集取回后，行的过滤依据是
/// <see cref="AccountTypeExtensions.IsMoneyAccount"/>（资金/负债）——本页回答的是「钱动在哪个账户」，
/// 而往来账户记的是「谁欠谁」而不是「钱放在哪」，它上面的明细是另一本账，不在本页呈现
/// （其余额与来往由账户管理页承担）。账本账户同理：它是系统内部账户，从不作为明细行出现。
/// </para>
/// <para>
/// 但**账户名**来自**全部**可见账户（含往来账户）：往来账户照常作为**对手方**出现在钱账户那些行上
/// （「支出 现金 → 老王」的对手方就是它），故 <see cref="CounterpartyKind.Account"/> 档的
/// <see cref="EntryQueryRow.CounterpartyName"/> 照旧给出它的名称。
/// 行被排除的是「往来账户作为记账主体」，不是「往来账户这个信息」。
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
    /// 目标账户主键集合；<c>null</c> 或空集合表示不限账户（即全部**钱账户**）。
    /// **集合会与钱账户集求交**：其中不可见的账户、以及往来账户都被静默剔除，
    /// 不会因此报错——否则这个参数就成了探测他人账户是否存在的探针
    /// （与 <see cref="IAccountService.FindVisibleAsync"/>「不存在、不属于本账套、不可见三种情形同响应」
    /// 的取舍一致）；而往来账户本就不作为明细行出现，为它报错只会让调用方以为「传错了参数」。
    /// </param>
    /// <param name="tagIds">
    /// 标签主键集合；<c>null</c> 或空集合表示不限标签。
    /// **匹配语义是「任一命中」**（交易挂着的标签中有任意一个在集合内即入选），
    /// 不是要求全部命中——多选标签的常规意图是「这几类我都想看看」，而不是「同时具备这几个标签的账」。
    /// <para>
    /// 集合同样**静默求交**：不属于本账套的标签主键不会报错，只是匹配不到任何交易。
    /// 理由与 <paramref name="accountIds"/> 逐字相同：标签主键按账套分配，
    /// 对「传了别人的标签主键」报错、对「传了不存在的标签主键」报错、而对存在的沉默，
    /// 三者组合起来就是一个能探出「某主键是否属于别人」的探针。
    /// </para>
    /// <para>
    /// 与 <paramref name="accountIds"/> **同时给出时是「且」的关系**：
    /// 两个筛选维度各自收窄，是筛选区的常规语义。
    /// </para>
    /// </param>
    /// <param name="types">
    /// 交易类型集合；<c>null</c> 或空集合表示**不限类型**（期初余额在其中，与其它类型一视同仁）。
    /// <para>
    /// 条件写在**交易头**的 <see cref="Transaction.Type"/> 上（与 <paramref name="from"/> / <paramref name="to"/>
    /// 同一张表），不是明细上的字段——明细没有自己的类型列。
    /// </para>
    /// <para>
    /// 与 <paramref name="accountIds"/> / <paramref name="tagIds"/> 同口径：省略即不限，
    /// 给出时与其它维度**同时成立**（「且」）。
    /// </para>
    /// <para>
    /// 记账页「最近 N 次同类型交易」正是用 <c>types=&lt;本页类型&gt;</c> + 倒序取回：
    /// 客户端本地按类型筛同一页数据是错的——用户昨天记的 100 笔支出会把「最新 20 条」占满，
    /// 今天记的 2 笔收入一条也筛不出来。过滤必须落在 SQL 里。
    /// </para>
    /// </param>
    /// <param name="page">页码，从 1 开始。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <param name="descending">
    /// 是否**倒序**（默认 <c>false</c>，即升序）。
    /// <para>
    /// 为真时三级排序键**逐级反向**，得到的是升序结果的**严格逆序**。只把第一级反向
    /// （同为某时刻的明细仍按主键升序）会让同一时刻的多条明细在正反两种方向下先后不一致，
    /// 翻页时同一行可能在两页里各出现一次——而「最后一级排序键让同一时刻的多条明细也有确定次序」
    /// 正是本查询分页稳定的依据。
    /// </para>
    /// <para>
    /// 首页「最近交易」面板用它取最新的若干条（<c>descending: true</c> + 第 1 页 + 想要的条数）。
    /// 它只改**方向**，行过滤、总数与对手方/分类/标签的解析与升序完全相同。
    /// </para>
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 一页明细及总数，**只含钱账户上的明细**（往来账户与账本账户上的行不在其中）。
    /// 明细按 <see cref="Transaction.OccurredAt"/> 升序、同一时刻按交易主键升序、
    /// 再按明细主键升序排列（第三级排序键保证分页结果稳定，不会出现「同一笔数据在两页里各出现一次」）；
    /// <paramref name="descending"/> 为真时上述三级键**逐级反向**（见该参数说明）。
    /// <para>
    /// 被排除的行只影响**呈现**，不影响库内数据：那笔交易的借贷两条明细照旧在库里配平，
    /// 账户余额（<see cref="ITransactionService.SumSignedAmountsAsync"/>）也照旧把它们计入。
    /// </para>
    /// </returns>
    Task<EntryQueryPage> QueryAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        DateTime? from,
        DateTime? to,
        IReadOnlyCollection<int>? accountIds,
        IReadOnlyCollection<int>? tagIds,
        IReadOnlyCollection<TransactionType>? types,
        int page,
        int pageSize,
        bool descending = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按交易类型聚合当前账套内的**历史摘要**：去重后按最近使用时间倒序给出。
    /// </summary>
    /// <param name="accountSetId">账套主键；只聚合该账套内的交易。</param>
    /// <param name="userId">当前用户主键。</param>
    /// <param name="isAdmin">是否为系统管理员；管理员可见该账套内的全部账户。</param>
    /// <param name="types">
    /// 交易类型集合；<c>null</c> 或空集合表示**不限类型**。
    /// 记账页与改账弹窗传的是**本页那一种类型**（收入页只列收入的历史摘要），
    /// 与「最近 N 次同类型交易」同一口径：摘要的可复用性本就随类型走
    /// （「工资」是收入摘要、「早餐」是支出摘要），跨类型混排只会让候选变长而不变准。
    /// </param>
    /// <param name="limit">最多返回多少条候选；由调用方保证落在合法区间内（端点层已校验）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>去重后的历史摘要，最近的在前；无匹配时为空列表。</returns>
    /// <remarks>
    /// **为什么要一个独立方法而不是拿 <see cref="QueryAsync"/> 的结果去重**：后者的行粒度是
    /// **明细**且必须分页（<c>pageSize</c> 上限 200），拿它去重既把整行明细的载荷搬进内存，
    /// 又只能覆盖最近的那一页——「上个月记过的摘要」会凭空不在候选里。
    /// 聚合要的是 <c>GROUP BY</c>，那是查询形状上的差别，不是同一件事的两个参数集。
    /// <para>
    /// **口径与明细页一致**：只聚合**钱账户**（资金/负债）上有明细的交易，可见性同样复用
    /// <see cref="IAccountService.ListByAccountSetAsync"/>——否则候选里会出现用户在明细页
    /// 根本查不到的账（往来账户上的那条明细），而用户对着候选无从分辨。
    /// </para>
    /// <para>
    /// <see cref="EntrySummaryOption.UsageCount"/> 数的是**笔数**不是明细行数：
    /// 一笔交易恒有借贷两条明细，数行数会让每一次记账都被记成 2 次。
    /// 判据不是前端要显示的数字好不好看，而是「候选里这一条被用过几次」这个问题的答案只有一个。
    /// </para>
    /// <para>
    /// **不做任何形状归一**：不折叠大小写、不去空白后合并同义项，「早餐」与「早餐 」就是两条候选。
    /// 摘要是用户自己敲下的自由文本，不是字典项——把两条不同的写法合并成一条，
    /// 等于替用户改了账（提交上去的会是其中一条的写法，而另一条从此选不出来）。
    /// </para>
    /// <para>
    /// 本方法同样**不写任何表**：它是纯读取，摘要没有停用/启用这一说，也没有任何汇总列。
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<EntrySummaryOption>> ListSummariesAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        IReadOnlyCollection<TransactionType>? types,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>历史摘要的一条候选。</summary>
/// <param name="Summary">摘要原文（用户当初敲下的写法，原样给出）。</param>
/// <param name="LastUsedAt">
/// 最近一次使用时间（UTC）：<see cref="Transaction.OccurredAt"/> 的最大值，
/// **业务发生时间**而不是落库时间——补记往日支出时两者会差很远，而用户心里「上次用这条摘要」的时点
/// 说的正是那笔账发生的时间。
/// </param>
/// <param name="UsageCount">
/// 用了多少次（**笔数**，不是明细行数）：一笔交易由借贷两条明细构成，
/// 数行数会让每一次记账都被计成 2 次。
/// </param>
public sealed record EntrySummaryOption(string Summary, DateTime LastUsedAt, int UsageCount);

/// <summary>明细行（已解析出账户名与对手方，供端点直接映射为 DTO）。</summary>
/// <param name="Id">明细主键。</param>
/// <param name="TransactionId">所属交易主键。</param>
/// <param name="OccurredAt">业务发生时间（UTC）。</param>
/// <param name="Summary">交易摘要。</param>
/// <param name="Remark">交易备注；无备注时为 <c>null</c>。</param>
/// <param name="Type">交易类型。</param>
/// <param name="AccountId">挂靠账户主键（必然是当前用户可见的**钱账户**）。</param>
/// <param name="AccountName">挂靠账户名称。</param>
/// <param name="Direction">借贷方向。</param>
/// <param name="Amount">金额，**恒为正**；方向由 <paramref name="Direction"/> 表达。</param>
/// <param name="CounterpartyKind">对手方账户的可见性档位。</param>
/// <param name="CounterpartyAccountId">对手方账户主键；仅 <see cref="CounterpartyKind.Account"/> 时有值。</param>
/// <param name="CounterpartyName">对手方账户名称；仅 <see cref="CounterpartyKind.Account"/> 时有值。</param>
/// <param name="CategoryId">交易分类主键；**未分类**时为 <c>null</c>。</param>
/// <param name="CategoryName">
/// 交易分类名称；**未分类**时为 <c>null</c>。与 <paramref name="CategoryId"/> 同生同灭。
/// </param>
/// <param name="IsPrimary">
/// 这条明细是否挂在**主账户**上（主账户 = 用户记账时选定的那个账户：收入账户 / 支出账户 / 转出账户）。
/// </param>
/// <param name="Tags">
/// 这笔交易挂着的标签，**没有标签时为空列表**。同一笔交易的两条明细会得到同一份标签——
/// 与 <paramref name="CategoryId"/> 同理：标签挂在**交易**上。
/// </param>
/// <remarks>
/// 分类挂在**交易**上而非明细上（见 <see cref="Transaction.CategoryId"/>），
/// 故同一笔交易的两条明细会得到同一个分类——这是刻意的：一笔转账只应有一个分类。
/// <para>
/// 分类**没有可见性档位**（不像对手方那样分 Account / Ledger / Hidden 三档）：
/// 分类表没有可见性维度，账套内所有成员看到的是同一份完整字典，
/// 故这里直接给出主键与名称，不需要「可见 / 不可见」这层区分。
/// </para>
/// <para>
/// <see cref="IsPrimary"/> 的判据是「<see cref="Direction"/> 等于该交易类型的
/// <see cref="TransactionTypeExtensions.PrimaryDirection"/>」，由查询侧在内存里算得——
/// 界面据它把「被点的那一行」还原成「主账户 + 对手方」两个端点，从而**不必自己按方向再推一遍**。
/// 判错的后果是编辑写到错误的账户上（数据损坏），故该判据只定义一处、不向前端镜像
/// （同 #43 把 <c>signedAmount</c> 算好下发而不让前端再算一次符号）。
/// </para>
/// <para>
/// 一笔用户交易里**恰有一行**为 <c>true</c>（两条明细方向恒相反）；
/// <see cref="TransactionType.OpeningBalance"/> 的行恒为 <c>false</c>——期初没有「主账户」这一概念
/// （它的方向由期初金额的符号决定），而期初本就不支持编辑，故该字段对它无意义。
/// </para>
/// <para>
/// 标签**没有可见性档位**（与分类同理、与对手方相反）：标签表没有可见性维度，
/// 且此处只给「这笔账当时标了什么」——**已停用的标签照常给出名称**，
/// 停用是「不再供新记账选择」，不是「历史上从未用过」。
/// </para>
/// </remarks>
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
    string? CounterpartyName,
    int? CategoryId,
    string? CategoryName,
    bool IsPrimary,
    IReadOnlyList<EntryTag> Tags);

/// <summary>某笔交易挂着的一个标签（服务层表示）。</summary>
/// <param name="Id">标签主键。</param>
/// <param name="Name">标签名称（可能是已停用标签的名称）。</param>
/// <remarks>
/// 与 <see cref="Category"/> 那一路不同，此处刻意**不返回标签实体**：交易挂着的标签在界面上只用于显示，
/// 账套归属、启用状态、创建时间在这里都是噪音，而实体类型会把它们一并带出去。
/// <para>
/// 与端点层的 <c>TagRefDto</c> 形状相同却各自定义：服务层不引用端点层的类型
/// （依赖方向是端点 → 服务，反过来会让服务层无法独立演进）。
/// </para>
/// </remarks>
public sealed record EntryTag(int Id, string Name);

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
