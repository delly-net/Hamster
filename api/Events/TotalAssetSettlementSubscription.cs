using Hamster.Api.Services;

namespace Hamster.Api.Events;

/// <summary>
/// 总资产结算订阅：收到结算事件后，把该账套尚未处理过的日期逐日重算并落表。
/// </summary>
/// <param name="totalAssets">总资产结算业务服务（订阅的真正实现）。</param>
/// <param name="logger">日志记录器。</param>
/// <remarks>
/// **本类只做接线**：把「结算事件发生了」翻译成「把账套补齐到今天」，其余全在
/// <see cref="ITotalAssetSettlementService.RunAsync"/> 里。之所以不把逻辑写进来，
/// 是因为业务要能脱离事件机制被直接调用与验证（见 <see cref="ITotalAssetSettlementService"/>）。
/// <para>
/// **本类是订阅机制的第二个使用者**（第一个是 <c>SettlementTriggeredEvent</c> 的既有订阅方），
/// 新增它**不需要改 <c>Program.cs</c>**：注册由
/// <c>EventBusRegistration.AddHamsterEventHandlers</c> 反射完成，
/// 「写一个实现类就自动生效」正是那套机制存在的理由。
/// </para>
/// <para>
/// **幂等靠「无事可做」而不是靠去重表**：同一天的事件被投递两次时，第二次进来水位已经走过，
/// <see cref="ITotalAssetSettlementService.RunAsync"/> 直接返回 0 天、一个字节都不写
/// ——这正是 <see cref="IEventHandler{TEvent}"/> 要求的幂等，而且它同时是**可观测的**：
/// 水库那一行的 <c>updated_at</c> 不会被重复投递刷新。
/// </para>
/// <para>
/// **不平抑异常**：本方法的异常由事件总线接住并计为一次派发失败，
/// 结算执行任务据此**不标记**该结算任务已执行、留待下一个执行日重试。
/// 在这里 try/catch 吞掉异常，等于把「这一天没算成功」伪装成成功，
/// 而水位也只走到失败日的前一天，下次重投会把那一天重新算一遍（覆盖写保证结果一致）。
/// </para>
/// </remarks>
public sealed class TotalAssetSettlementSubscription(
    ITotalAssetSettlementService totalAssets,
    ILogger<TotalAssetSettlementSubscription> logger) : IEventHandler<SettlementTriggeredEvent>
{
    /// <inheritdoc />
    public async Task HandleAsync(
        SettlementTriggeredEvent @event,
        CancellationToken cancellationToken = default)
    {
        // **载荷里的日期（@event.TransactionDate）刻意不使用**：事件是「某一天结算好了」，
        // 而本订阅要干的是「把这本账套尚未处理的日期全部补齐」——待处理区间由水位与结算任务表
        // 一起决定（见 ITotalAssetSettlementService.RunAsync），单看载荷这一天，
        // 一旦某次事件没送到（或账套是新加的、历史日期被积压），那个缺口就再也补不上了。
        // 于是本方法对载荷的唯一依赖是「哪本账套」，这也让它天然幂等。
        var result = await totalAssets.RunAsync(@event.AccountSetId, cancellationToken);

        if (result.DayCount == 0)
        {
            logger.LogDebug(
                "总资产结算订阅：账套 {AccountSetId} 没有待处理的日期（事件载荷日期 {Date}，结算任务 {TaskId}）",
                @event.AccountSetId,
                @event.TransactionDate.ToString("yyyy-MM-dd"),
                @event.SettlementTaskId);
            return;
        }

        logger.LogInformation(
            "总资产结算订阅：账套 {AccountSetId} 重算 {DayCount} 天并落库 {RecordCount} 条记录" +
            "（水位推进到 {Watermark}，触发事件为结算任务 {TaskId} 的 {Date}）",
            @event.AccountSetId,
            result.DayCount,
            result.RecordCount,
            result.LastExecutedDate?.ToString("yyyy-MM-dd") ?? "（无）",
            @event.SettlementTaskId,
            @event.TransactionDate.ToString("yyyy-MM-dd"));
    }
}
