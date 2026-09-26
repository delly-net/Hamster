using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 结算订阅执行表：某个**结算订阅**在某个**账套**上「已执行到的最后一天」。
/// 一个订阅 + 一本账套一行，供订阅在执行前判断「哪些日期还没处理过」、执行后推进水位。
/// </summary>
/// <remarks>
/// 本表是订阅的**幂等依据**，不是账务数据：它只记录「我处理到哪一天了」，
/// 删掉它只会让订阅下次执行时把历史重算一遍（重算本身幂等，只是白做功），不会丢账。
/// <para>
/// **水位为什么是「日期」而不是「时刻」**：订阅要处理的最小单位是<see cref="SettlementTask"/>
/// ——它是「某账套的某一天」的结算批次，日期即其粒度（见 <see cref="SettlementTask.TransactionDate"/>）。
/// 记成时刻则每次都要把「处理到 15:06」再翻译回「处理到哪一天」，
/// 而这两个答案之间差着一次时区换算，换算写错的表现是**静默地漏掉一整天**。
/// </para>
/// <para>
/// **水位为什么必须按账套分行**：结算任务按账套建立，而账套是**随时新增**的——
/// 新建一本账套时它的历史结算任务会一次性全部生成（见 <c>SettlementService.CollectAsync</c>
/// 的水位为空即不限初始时间的口径）。若水位是全局一条，那本新账套的历史日期会被
/// 「全局水位已经走到今天」直接跳过，**永远不会被订阅处理**。按账套分行后，
/// 「没见过的账套」自然表现为「没有行」= 从头处理，与结算收集侧的口径一致。
/// </para>
/// <para>
/// 一个订阅可以有多个实现（见 <c>IEventHandler{TEvent}</c>），故唯一索引的复合键是
/// <c>(subscription_code, account_set_id)</c>：<see cref="SubscriptionCode"/> 区分订阅，
/// <see cref="AccountSetId"/> 区分账套，两者缺一都会让不同订阅或不同账套互相顶掉水位。
/// </para>
/// </remarks>
[SugarTable("hamster_settlement_subscription_execution")]
[SugarIndex(
    "uk_hamster_settlement_subscription_execution_scope",
    nameof(SubscriptionCode),
    OrderByType.Asc,
    nameof(AccountSetId),
    OrderByType.Asc,
    true)]
[SugarIndex(
    "idx_hamster_settlement_subscription_execution_account_set",
    nameof(AccountSetId),
    OrderByType.Asc)]
public sealed class SettlementSubscriptionExecution
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>
    /// 订阅代码：订阅的稳定标识，由订阅实现自己声明（如 <c>TotalAssetSettlement</c>）。
    /// </summary>
    /// <remarks>
    /// 用**代码**而不是订阅者类型名或注册序号：类型名会随重构改名（一改名水位就全部作废、
    /// 历史被重算一遍），注册序号更是随注册顺序浮动。代码是订约双方唯一的约定点，
    /// 改它等同于「换一个订阅」，须连同历史水位的处置一起考虑。
    /// <para>
    /// 长度 64 足够容纳「谁 + 做什么」这种自解释命名，索引左前缀也吃得住。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "subscription_code", Length = 64)]
    public string SubscriptionCode { get; set; } = string.Empty;

    /// <summary>所属账套主键（水位的作用域，见类头注释）。</summary>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>
    /// 最后执行日期：该订阅在这本账套上**已经处理完的最后一天**（**本地日期**，时刻部分恒为 00:00:00）。
    /// </summary>
    /// <remarks>
    /// 口径与 <see cref="SettlementTask.TransactionDate"/> 逐字相同（含「读到后 <c>Kind</c> 为
    /// <c>Unspecified</c>、不要再当 UTC 转时区」那条），因为两者直接比较——
    /// 「结算任务里比它晚的那些日期」就是本订阅待处理的日期集合。
    /// <para>
    /// **语义是「已完成」而不是「已开始」**：订阅把某一天的结果落库之后才推进到这一天。
    /// 顺序反过来的话，中途失败的订阅会留下一个「水位说做过了、结果并不存在」的洞，
    /// 而这个洞没有任何东西会去补——比重复执行严重得多。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "last_executed_date")]
    public DateTime LastExecutedDate { get; set; }

    /// <summary>
    /// 最后执行时刻（UTC）：水位被推进的那一瞬间，**只用于观测**。
    /// </summary>
    /// <remarks>
    /// 判定「要不要处理」的是 <see cref="LastExecutedDate"/>，本列不参与任何计算——
    /// 带上它是因为「水位走到哪天」与「什么时候走的」在排查时是两个问题，
    /// 而后者只有落库才追溯得到（日志会滚掉）。
    /// <para>
    /// **本列不随空转刷新**：没有新日期可处理时整行一个字节都不写，故本列的时间戳
    /// 就是「最后一次真正干了活」的时刻，读它不会被空转刷成「刚刚」。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "last_executed_at")]
    public DateTime LastExecutedAt { get; set; }

    /// <summary>创建时间（UTC）：本行建立（即该订阅首次在这本账套上干活）的时刻。</summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 最后修改时间（UTC）：水位被推进的时刻，口径同 <see cref="LastExecutedAt"/>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="LastExecutedAt"/> 语义重合，但两者刻意都留：前者是全表统一的时间戳列，
    /// 供「按修改时间做增量同步」这类通用手段使用；后者是本表的业务字段，语义独立于时间戳惯例。
    /// 真要合并，先想清楚将来按哪一列同步——按惯例列同步会连带本表全部的字段变更。
    /// </remarks>
    [SugarColumn(ColumnName = "updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
