using Hamster.Api.Services;

namespace Hamster.Api.Events;

/// <summary>
/// 支出结算订阅：收到结算事件后，把该账套尚未处理过的日期逐日重算支出并落表。
/// </summary>
/// <param name="expenses">支出结算业务服务（订阅的真正实现）。</param>
/// <param name="logger">日志记录器。</param>
/// <remarks>
/// 与 <see cref="IncomeSettlementSubscription"/> 逐行同构，只有服务与水位的名字不同。
/// 接线、幂等与异常处理三条口径见该类的注释。
/// </remarks>
public sealed class ExpenseSettlementSubscription(
    IExpenseSettlementService expenses,
    ILogger<ExpenseSettlementSubscription> logger) : IEventHandler<SettlementTriggeredEvent>
{
    /// <inheritdoc />
    public async Task HandleAsync(
        SettlementTriggeredEvent @event,
        CancellationToken cancellationToken = default)
    {
        // **载荷里的日期（@event.TransactionDate）刻意不使用**（理由同 IncomeSettlementSubscription）
        var result = await expenses.RunAsync(@event.AccountSetId, cancellationToken);

        if (result.DayCount == 0)
        {
            logger.LogDebug(
                "支出结算订阅：账套 {AccountSetId} 没有待处理的日期（事件载荷日期 {Date}，结算任务 {TaskId}）",
                @event.AccountSetId,
                @event.TransactionDate.ToString("yyyy-MM-dd"),
                @event.SettlementTaskId);
            return;
        }

        logger.LogInformation(
            "支出结算订阅：账套 {AccountSetId} 重算 {DayCount} 天并落库 {RecordCount} 条记录" +
            "（水位推进到 {Watermark}，触发事件为结算任务 {TaskId} 的 {Date}）",
            @event.AccountSetId,
            result.DayCount,
            result.RecordCount,
            result.LastExecutedDate?.ToString("yyyy-MM-dd") ?? "（无）",
            @event.SettlementTaskId,
            @event.TransactionDate.ToString("yyyy-MM-dd"));
    }
}
