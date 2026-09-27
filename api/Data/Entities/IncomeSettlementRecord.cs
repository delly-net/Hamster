using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 收入结算记录：**某个用户**在**某本账套**的**某一天**、**某个币种**下发生的**收入**合计。
/// 由收入结算订阅按「结算任务」里的日期逐日重算并落表，供首页的当月收支图读取。
/// </summary>
/// <remarks>
/// **本表是快照，不是事实来源**：金额可以从交易与明细表随时重算出来（订阅正是这么做的），
/// 它的价值在于「把重算的结果按天存下来」，使首页读一次就能画出整月走势，
/// 而不必为每一根柱子跑一次跨全表的汇总。
/// <para>
/// **存的是「当日发生额」而不是「累计余额」**：收入是一个**流量**概念——
/// 「这一天挣了多少」才是用户看月度图时要回答的问题，而「从建账到这一天一共挣了多少」
/// 是一条单调上升的线、看不出任何单日波动（与 <see cref="TotalAssetSettlementRecord"/> 相反：
/// 那边存的是存量，故存的是「该日结束时的累计余额」）。一个月求和即当月总收入，
/// 这一步加法留给读侧，本表不存月度合计——同一事实存两处迟早对不上。
/// </para>
/// <para>
/// **按「用户」分行，是因为账户有归属范围**（见 <see cref="AccountScope"/>）：个人账户只属于其归属人，
/// 公共账户全账套共有。同一本账套、同一天的「我的收入」与「你的收入」并不相同——
/// 前者只含各自的个人账户，后者相同的那部分正是公共账户。故本表的自然键里必须带
/// <see cref="UserId"/>，且**在同一天为账套内的每个成员各落一行**（没有个人账户也照落，
/// 两个金额为 0，从而「每天每人一行」这条不变量成立，读侧不必补空缺）。
/// </para>
/// <para>
/// **个人与公共拆成两列，不合并成一个「收入合计」**：两者的归属不同（我的 / 全账套共有的），
/// 合成一个数字之后既说不清「这里头有多少是大家共有的」，也无法在将来按归属分开展示；
/// 而拆开存则任何组合都由读侧一次加法得到（同 <see cref="TotalAssetSettlementRecord"/>）。
/// </para>
/// <para>
/// **金额恒为非负**：收入交易的两条明细金额恒为正（见 <see cref="TransactionEntry.Amount"/>），
/// 而本表只累加「主账户明细」那一侧，故不存在负值——「这一天没有收入」就是 0，
/// 不需要用符号再表达一次方向（方向已由「这是收入表」这件事本身表达）。
/// </para>
/// <para>
/// **币种进唯一键、且一行只装一个币种**：跨币种求和需要汇率，而本项目没有汇率来源，
/// 把「100 美元」和「700 人民币」加成「800」是一个凭空捏造的数字。故每个币种各成一行，
/// 首页的收支图取**系统默认币种**那一组（见 <c>IncomeExpenseEndpoints</c>）。
/// </para>
/// </remarks>
[SugarTable("hamster_income_settlement_record")]
[SugarIndex(
    "uk_hamster_income_settlement_record_scope",
    nameof(AccountSetId),
    OrderByType.Asc,
    nameof(UserId),
    OrderByType.Asc,
    nameof(CurrencyCode),
    OrderByType.Asc,
    nameof(TransactionDate),
    OrderByType.Asc,
    true)]
public sealed class IncomeSettlementRecord
{
    /// <summary>
    /// 主键。
    /// 用 <see cref="int"/> 而非 <c>long</c>：Sqlite 的 AUTOINCREMENT 只允许加在 INTEGER PRIMARY KEY 上，
    /// 而 SqlSugar 会把 <c>long</c> 映射为 BIGINT 导致建表失败。
    /// </summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>所属账套主键。</summary>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>
    /// 这条记录呈现给哪个用户（**取值来自账套的成员列表**，见 <c>IAccountSetService.ListMemberIdsAsync</c>）。
    /// </summary>
    /// <remarks>
    /// 读出侧按「当前登录用户」过滤本列，故首页看到的是**自己的**口径：自己的个人账户 + 账套的公共账户。
    /// <para>
    /// **成员被移出账套后，历史记录不追改**；但重算某一天时会按**当时的成员口径**整体覆盖那一天
    /// （重算 = 先删该日全部行再插入），故成员变动后，被重算过的日子上不再有已移出成员的记录。
    /// 这是「快照按当前口径重算」的必然结果，接受它，而不是为历史成员保留一份永不更新的旧数。
    /// </para>
    /// </remarks>
    [SugarColumn(ColumnName = "user_id")]
    public int UserId { get; set; }

    /// <summary>
    /// 币种代码（ISO 4217，对应 <see cref="Currency.Code"/>）。
    /// 存代码而非币种主键，理由同 <see cref="Account.CurrencyCode"/>：代码才是对外的稳定标识。
    /// </summary>
    [SugarColumn(ColumnName = "currency_code", Length = 8)]
    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>
    /// 交易日期：本记录覆盖的那一天（**本地日期**，时刻部分恒为 00:00:00）。
    /// </summary>
    /// <remarks>
    /// 口径与 <see cref="SettlementTask.TransactionDate"/> 逐字相同——本表的日期直接来自
    /// 结算任务表的日期，两者是同一个词。读到后取 <c>Date</c> 即当天的日期，
    /// **不要再当作 UTC 去转本地时区**（<c>Kind</c> 为 <c>Unspecified</c>）。
    /// </remarks>
    [SugarColumn(ColumnName = "transaction_date")]
    public DateTime TransactionDate { get; set; }

    /// <summary>
    /// **个人**账户（归属人为 <see cref="UserId"/>）在该日发生的收入合计。
    /// </summary>
    /// <remarks>
    /// 「该日发生的」= 业务发生时间（<c>occurred_at</c>）落在这一天之内的收入交易的金额合计，
    /// 与「该账户当时的余额」无关。
    /// </remarks>
    [SugarColumn(ColumnName = "personal_income_total", DecimalDigits = 2)]
    public decimal PersonalIncomeTotal { get; set; }

    /// <summary>
    /// **公共**账户在该日发生的收入合计（同一账套的每个成员读到的是同一份数）。
    /// </summary>
    [SugarColumn(ColumnName = "public_income_total", DecimalDigits = 2)]
    public decimal PublicIncomeTotal { get; set; }

    /// <summary>创建时间（UTC）：本行首次落库的时刻。</summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 最后修改时间（UTC）：本行被重算覆盖的时刻。
    /// </summary>
    /// <remarks>
    /// 重算是「先删该日全部行再插入」，故正常重算会换出一条全新的行、本列等于 <see cref="CreatedAt"/>；
    /// 只有「订阅重复收到同一天的事件」时才有机会看到它被改写——而那种情况下订阅根本不会重算
    /// （水位已过，整天被跳过），所以本列在库里是判断「这一天是否被重算过」的可靠线索。
    /// </remarks>
    [SugarColumn(ColumnName = "updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
