namespace Hamster.Api.Services;

/// <summary>
/// 支出结算：按「结算任务」里的日期逐日重算账套内**每一天的支出发生额**并落表。
/// </summary>
/// <remarks>
/// 与 <see cref="IIncomeSettlementService"/> 是同一套算法的两个实例（差异只有订阅码与交易类型），
/// 说明见该接口。**水位与本订阅各记一份**，故收入与支出的推进互不影响。
/// </remarks>
public interface IExpenseSettlementService : IDailyFlowSettlementService
{
    /// <summary>
    /// 本订阅的执行水位代码（<c>hamster_settlement_subscription_execution.subscription_code</c>）。
    /// </summary>
    /// <remarks>
    /// **与收入的必须是两个不同的值**：水位按「订阅 + 账套」记，两者共用一个码就会互相抢占。
    /// 其余取舍见 <see cref="IIncomeSettlementService.SUBSCRIPTION_CODE"/>。
    /// </remarks>
    public const string SUBSCRIPTION_CODE = "ExpenseSettlement";
}
