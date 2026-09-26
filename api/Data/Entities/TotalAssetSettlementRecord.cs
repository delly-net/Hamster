using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 总资产结算记录：**某个用户**在**某本账套**的**某一天**、**某个币种**下看到的资产与负债合计。
/// 由总资产结算订阅按「结算任务」里的日期逐日重算并落表，供首页的当月走势图读取。
/// </summary>
/// <remarks>
/// **本表是快照，不是事实来源**：金额可以从交易与明细表随时重算出来（订阅正是这么做的），
/// 它的价值在于「把重算的结果按天存下来」，使首页读一次就能画出整月走势，
/// 而不必为每一根柱子跑一次跨全表的汇总。
/// <para>
/// **按「用户」分行，是因为账户有归属范围**（见 <see cref="AccountScope"/>）：个人账户只属于其归属人，
/// 公共账户全账套共有。同一本账套、同一天的「我的总资产」与「你的总资产」并不相同——
/// 前者只含各自的个人账户，后者相同的那部分正是公共账户。故本表的自然键里必须带
/// <see cref="UserId"/>，且**在同一天为账套内的每个成员各落一行**（没有个人账户也照落，
/// 四个金额为 0，从而「每天每人一行」这条不变量成立，读侧不必补空缺）。
/// </para>
/// <para>
/// **资产 / 负债 / 个人 / 公共拆成四列，不合并成一列「总资产」**：四者的口径互不相干
/// （资产是钱在哪，负债是欠谁），合成一个数字之后既算不出净资产、也说不清「我到底欠了多少」，
/// 而拆开存则任何组合都由读侧一次加法得到。多出来的三列不占什么空间，
/// 换的是「将来想按公共/个人分开展示时不必回头改表」。
/// </para>
/// <para>
/// **负债以带符号金额存储**（负数表示欠款），与 <c>AccountTypeExtensions.IsMoneyAccount</c>
/// 计出来的账户余额同号：负债账户的贷方明细使其余额为负。于是
/// 「净资产 = 资产合计 + 负债合计」——**加号而不是减号**，因为符号已经含在负债的取值里了。
/// 存储侧不再取绝对值：取绝对值等于在落库时抹掉一次方向信息，读侧要还原就得再知道
/// 「这一列是被取过绝对值的」，那是一条只在注释里存在的约定。
/// </para>
/// <para>
/// **刻意不存「净资产」列**：它是资产合计与负债合计的和，存下来就是同一事实的第二处表达，
/// 而两处表达迟早对不上（改了口径只改一处）。读侧一次加法即得，代价可以忽略
/// （同 <see cref="Account"/>「不存余额列」的取舍）。
/// </para>
/// <para>
/// **币种进唯一键、且一行只装一个币种**：跨币种求和需要汇率，而本项目没有汇率来源，
/// 把「100 美元」和「700 人民币」加成「800」是一个凭空捏造的数字。故每个币种各成一行，
/// 首页的走势图取**系统默认币种**那一组（见 <c>TotalAssetEndpoints</c>）。
/// </para>
/// </remarks>
[SugarTable("hamster_total_asset_settlement_record")]
[SugarIndex(
    "uk_hamster_total_asset_settlement_record_scope",
    nameof(AccountSetId),
    OrderByType.Asc,
    nameof(UserId),
    OrderByType.Asc,
    nameof(CurrencyCode),
    OrderByType.Asc,
    nameof(TransactionDate),
    OrderByType.Asc,
    true)]
public sealed class TotalAssetSettlementRecord
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
    /// **个人**账户（归属人为 <see cref="UserId"/>）中资产账户（资金账户）在该日结束时的合计。
    /// </summary>
    /// <remarks>
    /// 「该日结束时的合计」是**从建账以来的累计余额**，不是当日增减——走势图要画的是
    /// 「这天我手里有多少钱」，而不是「这天多出来多少钱」。
    /// </remarks>
    [SugarColumn(ColumnName = "personal_asset_total", DecimalDigits = 2)]
    public decimal PersonalAssetTotal { get; set; }

    /// <summary>
    /// **个人**账户中负债账户在该日结束时的合计，**带符号**（欠款为负）。
    /// </summary>
    [SugarColumn(ColumnName = "personal_liability_total", DecimalDigits = 2)]
    public decimal PersonalLiabilityTotal { get; set; }

    /// <summary>
    /// **公共**账户中资产账户在该日结束时的合计（同一账套的每个成员读到的是同一份数）。
    /// </summary>
    [SugarColumn(ColumnName = "public_asset_total", DecimalDigits = 2)]
    public decimal PublicAssetTotal { get; set; }

    /// <summary>
    /// **公共**账户中负债账户在该日结束时的合计，**带符号**（欠款为负）。
    /// </summary>
    [SugarColumn(ColumnName = "public_liability_total", DecimalDigits = 2)]
    public decimal PublicLiabilityTotal { get; set; }

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
