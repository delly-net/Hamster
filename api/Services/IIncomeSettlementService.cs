namespace Hamster.Api.Services;

/// <summary>
/// 收入结算：按「结算任务」里的日期逐日重算账套内**每一天的收入发生额**并落表。
/// </summary>
/// <remarks>
/// 本服务是「收入结算订阅」的**业务实现**，订阅的接线在 <c>IncomeSettlementSubscription</c>
/// （它只做一件事：收到结算事件就调 <see cref="IDailyFlowSettlementService.RunAsync"/>）。
/// 分开的理由与事件总线本身一致：订阅者要做到「新增一个类文件即生效」，
/// 而业务逻辑要能脱离事件机制被直接调用与验证。
/// <para>
/// **本服务自己不认识任何事件**：它接受的是「把某本账套的收入补齐到今天」，由调用方决定何时触发。
/// </para>
/// <para>
/// 算法与水位语义见 <see cref="IDailyFlowSettlementService"/> 与 <c>DailyFlowSettlementServiceBase</c>；
/// 本接口存在的意义只有两条：**钉住订阅码**，以及让「收入」与「支出」在依赖注入里是两个可分辨的类型。
/// </para>
/// </remarks>
public interface IIncomeSettlementService : IDailyFlowSettlementService
{
    /// <summary>
    /// 本订阅的执行水位代码（<c>hamster_settlement_subscription_execution.subscription_code</c>）。
    /// </summary>
    /// <remarks>
    /// 常量放在接口上而不是订阅者类里：读写的两边（本服务与
    /// <see cref="ISettlementSubscriptionExecutionService"/>）都按它认人，
    /// 而订阅者只是「谁在什么时候调」的那一层，把约定挂在它身上会让协议定义散落在调用方。
    /// <para>
    /// **与支出必须是两个不同的值**：水位按「订阅 + 账套」记，两者共用一个码就会互相抢占，
    /// 表现为「只跑了一个订阅、另一个却显示已完成」（同 <c>ITotalAssetSettlementService</c> 的取舍）。
    /// </para>
    /// <para>
    /// 改这个值等同于**换一个订阅**：既有水位全部作废、下次执行会从头重算（结果仍幂等，
    /// 只是白做功）。真要改，先想清楚这是不是本意。
    /// </para>
    /// </remarks>
    public const string SUBSCRIPTION_CODE = "IncomeSettlement";
}
