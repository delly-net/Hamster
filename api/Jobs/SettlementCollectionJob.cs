using Hamster.Api.Config;
using Hamster.Api.Services;

namespace Hamster.Api.Jobs;

/// <summary>
/// 交易统计定时任务：每天 <c>00:05</c>（可配置）把上一个执行日至今的交易按「账套 + 交易日期」
/// 分组，建立结算任务并冗余存储其全部交易与明细。
/// </summary>
/// <remarks>
/// 任务描述：「在每天的 0 点 5 分触发，判断并收集上一个执行日期 0 点 0 分（第一次执行不限初始时间）
/// 到当天 0 点之前（不含 0 点）之间所有的交易信息，并按照交易日期分组，建立结算任务
/// （结算任务具有交易日期与创建时间字段），结算任务下冗余存储所属日期的交易信息及所有明细」。
/// <para>
/// **本任务只做收集，不派发任何事件**：收集与结算是两件事，各自有各自的时间点
/// （0 点 5 分 / 1 点），合在一起会让「0 点后新记的账没被收集到」这类问题与
/// 「订阅者失败」搅在一起、无从分辨。派发由 <see cref="SettlementExecutionJob"/> 负责。
/// </para>
/// <para>
/// **窗口与幂等全部在 <see cref="ISettlementService.CollectAsync"/> 里**，
/// 本类只负责「到点了就调它一次」——把判据写在服务里，隔离实例才能不经定时器直接触发同一条逻辑
/// （见该方法的注释）。
/// </para>
/// </remarks>
/// <param name="options">结算任务配置。</param>
/// <param name="settlements">结算业务服务。</param>
/// <param name="logger">日志记录器。</param>
public sealed class SettlementCollectionJob(
    SettlementOptions options,
    ISettlementService settlements,
    ILogger<SettlementCollectionJob> logger) : ScheduledJobBase(logger)
{
    /// <inheritdoc />
    protected override string JobName => nameof(SettlementCollectionJob);

    /// <inheritdoc />
    protected override string SettingName => nameof(SettlementOptions.CollectEnabled);

    /// <inheritdoc />
    protected override bool IsEnabled => options.CollectEnabled;

    /// <inheritdoc />
    protected override TimeOnly ScheduledAt => options.CollectTime;

    /// <inheritdoc />
    protected override bool ShouldRun => options.IsMasterNode;

    /// <inheritdoc />
    protected override async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var result = await settlements.CollectAsync(stoppingToken);

        if (result.IsEmpty)
        {
            logger.LogInformation(
                "交易统计：窗口内没有待收集的交易（已收集到水位处，或这几天本就没有账），未新建结算任务");
            return;
        }

        logger.LogInformation(
            "交易统计：新建 {TaskCount} 个结算任务（{Tasks}），冗余存储 {TransactionCount} 笔交易、{EntryCount} 条明细",
            result.CreatedTasks.Count,
            string.Join(
                "、",
                result.CreatedTasks.Select(task =>
                    $"账套 {task.AccountSetId} {task.TransactionDate:yyyy-MM-dd}")),
            result.TransactionCount,
            result.EntryCount);
    }
}
