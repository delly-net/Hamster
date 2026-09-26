using Hamster.Api.Config;
using Hamster.Api.Events;
using Hamster.Api.Services;

namespace Hamster.Api.Jobs;

/// <summary>
/// 结算执行定时任务：每天 <c>01:00</c>（可配置）把尚未执行的结算任务逐个派发结算事件。
/// </summary>
/// <remarks>
/// 任务描述：「在每天的 1 点触发，触发结算事件（建立统一的事件订阅机制，
/// 后续可自行添加结算事件订阅进行功能扩展）」。
/// <para>
/// **本任务自己不认识任何订阅者**：它只调 <see cref="IEventBus.PublishAsync{TEvent}"/>，
/// 谁来接、接几个，由「实现了 <see cref="IEventHandler{TEvent}"/> 的类」决定。
/// 新增一个订阅者只需新增一个类文件，不改本类、不改 <c>Program.cs</c>
/// （注册由 <c>EventBusRegistration.AddHamsterEventHandlers</c> 反射完成）。
/// </para>
/// <para>
/// **「已执行」只在全部订阅者都成功后才落定**：有订阅者失败时该结算任务保持未执行，
/// 下一个执行日会被再次派发。代价是**订阅者必须幂等**——
/// 同一个结算事件可能被投递两次（成功的那几个订阅者会收到第二遍）。
/// 这是刻意选的：宁可让订阅者多做一次无副作用的判断，也不要因为一个订阅者的临时故障
/// 就把这次结算永久标记为「已完成」——后者会让那个订阅者**永远收不到**这一天。
/// </para>
/// </remarks>
/// <param name="options">结算任务配置。</param>
/// <param name="settlements">结算业务服务。</param>
/// <param name="eventBus">事件总线。</param>
/// <param name="logger">日志记录器。</param>
public sealed class SettlementExecutionJob(
    SettlementOptions options,
    ISettlementService settlements,
    IEventBus eventBus,
    ILogger<SettlementExecutionJob> logger) : ScheduledJobBase(logger)
{
    /// <inheritdoc />
    protected override string JobName => nameof(SettlementExecutionJob);

    /// <inheritdoc />
    protected override string SettingName => nameof(SettlementOptions.ExecuteEnabled);

    /// <inheritdoc />
    protected override bool IsEnabled => options.ExecuteEnabled;

    /// <inheritdoc />
    protected override TimeOnly ScheduledAt => options.ExecuteTime;

    /// <inheritdoc />
    protected override bool ShouldRun => options.IsMasterNode;

    /// <inheritdoc />
    protected override async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var pending = await settlements.FindPendingExecutionsAsync(stoppingToken);
        if (pending.Count == 0)
        {
            logger.LogInformation("结算执行：没有待执行的结算任务");
            return;
        }

        var executed = 0;
        var failed = 0;

        foreach (var task in pending)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // 条数取不到（结算任务刚被删）时按 0 报：事件仍然照发，
            // 因为「这个结算任务被触发了」本身是要订阅者知道的事实，
            // 载荷里的条数只是便利信息，不该因为它取不到就吞掉整个事件。
            var counts = await settlements.CountSnapshotsAsync(task.Id, stoppingToken)
                         ?? new SettlementSnapshotCounts(0, 0);

            var settledAt = DateTime.UtcNow;
            var @event = new SettlementTriggeredEvent(
                task.Id,
                task.AccountSetId,
                task.TransactionDate,
                settledAt,
                counts.TransactionCount,
                counts.EntryCount);

            var result = await eventBus.PublishAsync(@event, stoppingToken);

            if (!result.IsSuccessful)
            {
                // 不标记已执行：下一个执行日重试（订阅者需幂等，见类头注释）
                failed++;
                logger.LogWarning(
                    "结算事件派发有订阅者失败：结算任务 {TaskId}（账套 {AccountSetId}，交易日期 {Date}），" +
                    "{FailureCount}/{HandlerCount} 个订阅者失败，本次不标记已执行，将在下一个执行日重试",
                    task.Id,
                    task.AccountSetId,
                    task.TransactionDate.ToString("yyyy-MM-dd"),
                    result.FailureCount,
                    result.HandlerCount);
                continue;
            }

            var marked = await settlements.MarkExecutedAsync(task, settledAt, stoppingToken);
            if (marked)
            {
                executed++;
                logger.LogInformation(
                    "已结算：结算任务 {TaskId}（账套 {AccountSetId}，交易日期 {Date}，{TransactionCount} 笔交易 / {EntryCount} 条明细），" +
                    "结算事件已派发给 {HandlerCount} 个订阅者",
                    task.Id,
                    task.AccountSetId,
                    task.TransactionDate.ToString("yyyy-MM-dd"),
                    counts.TransactionCount,
                    counts.EntryCount,
                    result.HandlerCount);
            }
            else
            {
                // 条件更新没改到行 = 另一个实例（或另一次触发）已经标记过它。
                // 不是错误，但值得记一条：它说明本实例的这次派发是**重复**的。
                logger.LogInformation(
                    "结算任务 {TaskId} 已被其它实例标记为已执行，本实例的这次派发未重复记账",
                    task.Id);
            }
        }

        logger.LogInformation(
            "结算执行完成：待执行 {PendingCount} 个，成功 {ExecutedCount} 个，因订阅者失败待重试 {FailedCount} 个",
            pending.Count,
            executed,
            failed);
    }
}
