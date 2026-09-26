using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 个人账套配置：某个用户在某本账套里的**个人界面设置**，目前只记「账目明细」页的筛选条件。
/// </summary>
/// <remarks>
/// **唯一性是「账套 + 用户」两个维度**：同一本账套里的两个成员各有各的筛选习惯，
/// 「个人账套配置」的重点在**个人**——只按账套唯一会让 A 改一次筛选就把 B 的页面也改掉；
/// 只按用户唯一则会让「在账套甲筛『现金』、切到账套乙」时沿用甲里的账户主键，而那些主键在乙里并不存在。
/// <para>
/// **为什么不往 <see cref="AccountSetMember"/> 上加两列**：那张表记的是「谁属于哪本账套」这一**归属关系**，
/// 成员被移出账套时它会被删掉，而「我曾经怎么筛明细」并不是归属关系的一部分，
/// 不该跟着一次成员变更一起消失。两件事的生存期不同，故分作两张表。
/// </para>
/// <para>
/// **为什么不用浏览器本地存储**：要求就是「保存到（个人）账套配置表中」——换一台机器、换一个浏览器，
/// 筛选条件应当照旧；本地存储只在一台机器的一个浏览器里成立。
/// </para>
/// <para>
/// <see cref="EntryAccountIds"/> / <see cref="EntryTagIds"/> **按列分而不是按页开表**：
/// 本表是「个人账套配置」，账目明细页只是它的**第一个**消费者。将来别的页面若要记住自己的筛选条件，
/// 往同一行加一组 <c>settlement_*</c> 列即可，不必再为「用户 + 账套 + 页面」开一张新表。
/// 页面多了之后若列数难以维持，正确的演进方向是拆出按页面键的子表，
/// 而不是让现在这版提前长成一个通用键值表（那会让列的语义、类型校验、默认值全部消失）。
/// </para>
/// </remarks>
[SugarTable("hamster_account_set_preference")]
[SugarIndex(
    "idx_hamster_account_set_preference_owner",
    nameof(AccountSetId),
    OrderByType.Asc,
    nameof(UserId),
    OrderByType.Asc,
    true)]
public sealed class AccountSetPreference
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败（同 <see cref="Tag.Id"/>）。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>所属账套主键。</summary>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>配置归属人主键。</summary>
    /// <remarks>
    /// 与 <see cref="AccountSetId"/> 一起构成唯一索引：一行即「某人在某本账套里的那份配置」。
    /// 配置随人走、不随账套共享——用户被移出账套时本行**刻意保留**（见类头注释）。
    /// </remarks>
    [SugarColumn(ColumnName = "user_id")]
    public int UserId { get; set; }

    /// <summary>
    /// 账目明细页选中的账户主键，**逗号分隔**；空串表示「没有保存过账户条件」。
    /// </summary>
    /// <remarks>
    /// 存文本而非 JSON：Sqlite 与 PostgreSQL 都有文本列、两种库的写法完全一致，
    /// 且库里肉眼可读（排查「这个用户到底存了什么」时不必先解析一层）。
    /// <para>
    /// 列长 2000 足够容纳数百个主键。真要突破这个量级，正确的做法是把主键挪进子表，
    /// 而不是把本列继续加长——一个装不下的字符串列会在写入时静默截断或直接报错。
    /// </para>
    /// <para>
    /// **写入前一律按当前账套收敛**（见 <c>AccountSetPreferenceService</c>）：存进来的只会是
    /// 「本账套内确实存在的那些」，故本列不含跨账套主键、也不含已消失的主键。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "entry_account_ids", Length = 2000)]
    public string EntryAccountIds { get; set; } = string.Empty;

    /// <summary>
    /// 账目明细页选中的标签主键，**逗号分隔**；空串表示「不限标签」。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="EntryAccountIds"/> 逐条同理。标签**含已停用的**：
    /// 筛选区本就把停用标签列进候选（它查的正是历史账，见 <c>EntryQueryView</c>），
    /// 若在这里把停用标签剔掉，用户上次按它筛的视图下次进入就会静默变样。
    /// </remarks>
    [SugarColumn(ColumnName = "entry_tag_ids", Length = 2000)]
    public string EntryTagIds { get; set; } = string.Empty;

    /// <summary>创建时间（UTC）。</summary>
    /// <remarks>
    /// 用 <see cref="DateTime"/> 而非 <c>DateTimeOffset</c>：Sqlite 以文本存储时间且不保留偏移量，
    /// DateTimeOffset 读回时会被按本地时区重新解释，导致时刻偏移（同 <see cref="Tag.CreatedAt"/>）。
    /// </remarks>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 最后修改时间（UTC）。
    /// </summary>
    /// <remarks>
    /// **本列非空是安全的**：本表是随本任务全新建出来的表，库里不存在「先有行、后有列」的历史数据，
    /// 故这里不适用「加在既有表上的列一律标 <c>IsNullable = true</c>」那条规则
    /// （PostgreSQL 拒绝在非空表上新增非空列，那条规则是为此而设，见 <c>DatabaseInitializer</c>）。
    /// 勿因「和 <see cref="Transaction.UpdatedAt"/> 长得像」就照抄它的可空标注——
    /// 那一列是加到既有交易表上的，两者处境不同。
    /// </remarks>
    [SugarColumn(ColumnName = "updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
