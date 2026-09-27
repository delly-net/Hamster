using SqlSugar;

namespace Hamster.Api.Data.Entities;

/// <summary>
/// 支出结算记录：**某个用户**在**某本账套**的**某一天**、**某个币种**下发生的**支出**合计。
/// 由支出结算订阅按「结算任务」里的日期逐日重算并落表，供首页的当月收支图读取。
/// </summary>
/// <remarks>
/// 与 <see cref="IncomeSettlementRecord"/> 是**同一个形状的两张表**（订阅、口径、落库时机逐字相同，
/// 只有「收入 / 支出」这一个词不同）。
/// <para>
/// **为什么是两个订阅、两张表，而不是一个订阅、一张带方向列的表**：任务要求即如此，
/// 且这样切分各有实际好处——① 订阅可独立重跑（收入口径出问题时不必连支出一起重算）；
/// ② 水位各记各的（见 <see cref="SettlementSubscriptionExecution"/>），互不阻塞；
/// ③ 读侧要「收入」时不必再带一个方向过滤条件，也就不存在「过滤条件写漏了、把支出当收入画上去」这种错。
/// 代价是同一份逐日汇总逻辑存在两条实例化路径，故后端用
/// <c>DailyFlowSettlementServiceBase</c> 之类共享基类承载算法、把差异收敛成几个钩子。
/// </para>
/// <para>
/// 其余口径（当日发生额而非累计、按用户分行、个人与公共两列、金额恒为非负、币种进唯一键）
/// 逐条与 <see cref="IncomeSettlementRecord"/> 相同，不再重复。
/// </para>
/// </remarks>
[SugarTable("hamster_expense_settlement_record")]
[SugarIndex(
    "uk_hamster_expense_settlement_record_scope",
    nameof(AccountSetId),
    OrderByType.Asc,
    nameof(UserId),
    OrderByType.Asc,
    nameof(CurrencyCode),
    OrderByType.Asc,
    nameof(TransactionDate),
    OrderByType.Asc,
    true)]
public sealed class ExpenseSettlementRecord
{
    /// <summary>主键（<c>int</c> 的理由见 <see cref="IncomeSettlementRecord.Id"/>）。</summary>
    [SugarColumn(ColumnName = "id", IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>所属账套主键。</summary>
    [SugarColumn(ColumnName = "account_set_id")]
    public int AccountSetId { get; set; }

    /// <summary>这条记录呈现给哪个用户（口径见 <see cref="IncomeSettlementRecord.UserId"/>）。</summary>
    [SugarColumn(ColumnName = "user_id")]
    public int UserId { get; set; }

    /// <summary>币种代码（ISO 4217，对应 <see cref="Currency.Code"/>）。</summary>
    [SugarColumn(ColumnName = "currency_code", Length = 8)]
    public string CurrencyCode { get; set; } = string.Empty;

    /// <summary>
    /// 交易日期：本记录覆盖的那一天（**本地日期**，时刻部分恒为 00:00:00）。
    /// 口径见 <see cref="IncomeSettlementRecord.TransactionDate"/>。
    /// </summary>
    [SugarColumn(ColumnName = "transaction_date")]
    public DateTime TransactionDate { get; set; }

    /// <summary>
    /// **个人**账户（归属人为 <see cref="UserId"/>）在该日发生的支出合计。
    /// </summary>
    /// <remarks>
    /// **恒为非负**：内部记账的支出是一条「钱离开主账户」的分录，其金额本身为正，
    /// 本表累加的是这笔金额的大小而不是一个带符号的收支净额——
    /// 「支出为负、收入为正、两者相加即净额」是**前端图上的呈现**，不是库里的口径；
    /// 若在本表里就把支出存成负数，则「这一天花了多少」这个直接问题反而要再做一次取反才能回答。
    /// </remarks>
    [SugarColumn(ColumnName = "personal_expense_total", DecimalDigits = 2)]
    public decimal PersonalExpenseTotal { get; set; }

    /// <summary>**公共**账户在该日发生的支出合计（同账套每个成员读到的是同一份数）。</summary>
    [SugarColumn(ColumnName = "public_expense_total", DecimalDigits = 2)]
    public decimal PublicExpenseTotal { get; set; }

    /// <summary>创建时间（UTC）：本行首次落库的时刻。</summary>
    [SugarColumn(ColumnName = "created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>最后修改时间（UTC）：本行被重算覆盖的时刻（语义见 <see cref="IncomeSettlementRecord.UpdatedAt"/>）。</summary>
    [SugarColumn(ColumnName = "updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
