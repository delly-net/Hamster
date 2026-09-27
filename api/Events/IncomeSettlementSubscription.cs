using Hamster.Api.Services;

namespace Hamster.Api.Events;

/// <summary>
/// 收入结算订阅：收到结算事件后，把该账套尚未处理过的日期逐日重算收入并落表。
/// </summary>
/// <param name="incomes">收入结算业务服务（订阅的真正实现）。</param>
/// <param name="logger">日志记录器。</param>
/// <remarks>
/// **本类只做接线**：把「结算事件发生了」翻译成「把账套的收入补齐到今天」，其余全在
/// <see cref="IDailyFlowSettlementService.RunAsync"/> 里（同 <c>TotalAssetSettlementSubscription</c>）。
/// <para>
/// **本类是订阅机制的第三个使用者**，新增它**不需要改 <c>Program.cs</c> 的订阅注册**：
/// 注册由 <c>EventBusRegistration.AddHamsterEventHandlers</c> 反射完成
/// （但业务服务的依赖注入注册仍要手写，见 <c>Program.cs</c>——反射只认订阅者，不认服务）。
/// </para>
/// <para>
/// **与总资产订阅并行执行、互不阻塞**：同一个结算事件会依次派发给全部订阅者，
/// 本订阅只推进自己的水位（<see cref="IIncomeSettlementService.SUBSCRIPTION_CODE"/>）。
/// 故本订阅失败**不会**让总资产的水位倒退，反之亦然——代价是「收入算好了、总资产没算」这种
/// 短暂的不一致是可能的，而它们各自都会在下一次执行时自愈。
/// </para>
/// <para>
/// **幂等靠「无事可做」而不是靠去重表**、**不平抑异常**：两条理由逐字同
/// <c>TotalAssetSettlementSubscription</c>，不再重复。
/// </para>
/// </remarks>
public sealed class IncomeSettlementSubscription(
    IIncomeSettlementService incomes,
    ILogger<IncomeSettlementSubscription> logger) : IEventHandler<SettlementTriggeredEvent>
{
    /// <inheritdoc />
    public async Task HandleAsync(
        SettlementTriggeredEvent @event,
        CancellationToken cancellationToken = default)
    {
        // **载荷里的日期（@event.TransactionDate）刻意不使用**：待处理区间由水位与结算任务表
        // 一起决定，单看载荷这一天会让「事件漏送」或「账套新加」留下的缺口永远补不上
        // （同 TotalAssetSettlementSubscription）。
        var result = await incomes.RunAsync(@event.AccountSetId, cancellationToken);

        if (result.DayCount == 0)
        {
            logger.LogDebug(
                "收入结算订阅：账套 {AccountSetId} 没有待处理的日期（事件载荷日期 {Date}，结算任务 {TaskId}）",
                @event.AccountSetId,
                @event.TransactionDate.ToString("yyyy-MM-dd"),
                @event.SettlementTaskId);
            return;
        }

        logger.LogInformation(
            "收入结算订阅：账套 {AccountSetId} 重算 {DayCount} 天并落库 {RecordCount} 条记录" +
            "（水位推进到 {Watermark}，触发事件为结算任务 {TaskId} 的 {Date}）",
            @event.AccountSetId,
            result.DayCount,
            result.RecordCount,
            result.LastExecutedDate?.ToString("yyyy-MM-dd") ?? "（无）",
            @event.SettlementTaskId,
            @event.TransactionDate.ToString("yyyy-MM-dd"));
    }
}
