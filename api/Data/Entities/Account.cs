using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 账户：记账业务中资金与债务的挂靠对象，后续的流水都会落到某个账户上。
/// </summary>
/// <remarks>
/// 每个账户归属且仅归属一个账套（<see cref="AccountSetId"/>），账户列表按「当前账套」过滤——
/// 切换账套即切换账户集合。归属范围见 <see cref="Scope"/>，类型见 <see cref="Type"/>。
/// <para>
/// 删除采用**软删除**：以 <see cref="IsActive"/> 的停用/启用取代物理删除。
/// 账户是流水的挂靠对象，物理删除会让历史流水指向一个不存在的账户。
/// </para>
/// <para>
/// **刻意不存余额列**：账户只有<see cref="InitialBalance"/>（期初金额）一列。
/// 余额是「全部交易明细的有符号汇总」的派生值（对外 DTO 上提供，见 <c>AccountDto.From</c>）。
/// 期初金额在创建时即落成一笔期初交易（见 <c>TransactionService.RecordOpeningBalanceAsync</c>），
/// 本身就在这份汇总里，故派生时**不再叠加**本列——叠加会把期初金额重复计一次。
/// 若同时存一份余额列，则又多出一条需要同步的路径，迟早出现「余额列忘了同步」的静默错账——
/// 单一真相比省一次计算重要得多。
/// </para>
/// </remarks>
[SugarTable("hamster_account")]
[SugarIndex("idx_hamster_account_account_set", nameof(AccountSetId), OrderByType.Asc)]
public sealed class Account
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>
    /// 所属账套主键，创建后不可修改。
    /// 索引非唯一：一个账套下自然有多个账户。
    /// </summary>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>
    /// 账户名称。
    /// 唯一性**不做数据库约束**，改由服务层按「账套 + 归属范围」判定：个人账户的可见范围本就
    /// 只到归属人，两个用户各有一个「我的钱包」是合法且常见的，加全局唯一索引会把正常用法判成冲突。
    /// </summary>
    [SugarColumn(ColumnName = "name", Length = 64)]
    public string Name { get; set; } = string.Empty;

    /// <summary>归属范围（个人 / 公共），创建后不可修改。</summary>
    /// <remarks>
    /// 刻意不允许中途变更：个人 → 公共等于把私有数据一次性公开给全账套，反之则会让他人
    /// 正在使用的账户突然消失。需要变更归属时，应停用后重新创建。
    /// </remarks>
    [SugarColumn(ColumnName = "scope")]
    public AccountScope Scope { get; set; }

    /// <summary>
    /// 归属人用户主键：个人账户必填且等于创建者；公共账户恒为 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 用 <c>null</c>（而非 <c>0</c> 之类的哨兵值）表达「无归属人」：哨兵值会在
    /// 「按归属人过滤」的查询里变成需要额外排除的特例。创建时由服务端以当前登录者强制写入，
    /// 不接受调用方指定，否则可伪造出「归属他人的个人账户」。
    /// </remarks>
    [SugarColumn(ColumnName = "owner_user_id", IsNullable = true)]
    public int? OwnerUserId { get; set; }

    /// <summary>账户类型（账本 / 资金 / 负债 / 往来）。</summary>
    [SugarColumn(ColumnName = "type")]
    public AccountType Type { get; set; }

    /// <summary>
    /// 期初金额，即该账户建立时已有的金额。
    /// 单位「元」，两位小数；负债账户允许为负。建议业务侧以「分」为单位存储，避免浮点误差。
    /// </summary>
    [SugarColumn(ColumnName = "initial_balance", DecimalDigits = 2)]
    public decimal InitialBalance { get; set; }

    /// <summary>
    /// 是否启用。停用即软删除：停用后默认不出现在账户列表中，但历史数据与流水挂靠关系保留。
    /// 列名与语义沿用 <see cref="User.IsActive"/>，全站保持一致。
    /// </summary>
    [SugarColumn(ColumnName = "is_active")]
    public bool IsActive { get; set; }

    /// <summary>
    /// 是否为系统自动创建的内置账户。
    /// </summary>
    /// <remarks>
    /// 当前唯一的用途是标识**账本账户**：每笔交易都需要一个复式对手方账户
    /// （期初余额、收入、支出皆然，如「目标账户 +金额，账本账户 −金额」），
    /// 该账户按账套自动创建、每账套至多一个。
    /// <para>
    /// 之所以用一个持久化的标记位而不是「按名称找」或「按类型找」：
    /// 名称与类型都是**用户可改**的属性（<c>UpdateAsync</c> 允许改名与改类型），
    /// 拿它们当身份依据，一次改名就会让系统认不出既有账本账户、再建一个出来。
    /// 标记位随行持久化，改名、改类型、停用都不影响识别。
    /// </para>
    /// <para>
    /// 系统账户**不出现在账户列表中**：它的类型是 <see cref="AccountType.Ledger"/>，
    /// 而账本账户类型对任何人不呈现（管理员同样看不到，见 <c>AccountService</c> 的可见性过滤）。
    /// </para>
    /// <para>
    /// 隐藏依据是**类型**而不是本标记：本标记只用来识别「哪一个是系统建的那个」，
    /// 用于按需创建时复用同一条而不重复建。两者若混用，一次改类型就会让系统认不出既有账本账户。
    /// </para>
    /// <para>
    /// 但数据本身**照常计入余额与参与复式配平**：它仍是期初分录的对手方，
    /// 藏起来的只是界面呈现，不是账务参与。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "is_system")]
    public bool IsSystem { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// 用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>：Sqlite 以文本存储时间且不保留偏移量，
    /// DateTimeOffset 读回时会被按本地时区重新解释，导致时刻偏移。
    /// </summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
