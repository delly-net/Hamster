using Hamster.Api.Config;

namespace Hamster.Api.Jobs;

/// <summary>
/// 每日定时任务的公共骨架：到点执行一次 <see cref="RunOnceAsync"/>。
/// </summary>
/// <remarks>
/// 两个结算任务（<see cref="SettlementCollectionJob"/>、<see cref="SettlementExecutionJob"/>）
/// 的差异只有「几点跑」「跑什么」两项，其余（开关、主节点判定、休眠与重算、异常兜底、
/// 「一天至多跑一次」）逐字相同，故收在本类里——
/// 各写一遍的话，「异常不能让循环退出」这类细节迟早只有一份写对。
/// <para>
/// **本类不引入任何调度框架**（Quartz / Hangfire 等）：需求是「每天某个时刻跑一次」，
/// 一个 <see cref="BackgroundService"/> 加一次 <c>Task.Delay</c> 就够了，
/// 引入一套调度框架会连带把它的存储、集群协调、控制台一并带进来，
/// 而本项目的节点互斥已经由配置决定（见 <see cref="SettlementOptions.IsMasterNode"/>）。
/// </para>
/// <para>
/// **休眠切成小片**（见 <see cref="MaxSleepSlice"/>）：一次睡满 24 小时看着更省事，
/// 但机器休眠唤醒、系统时间被校正、容器被暂停之后，那个长眠的剩余时间与墙上时钟就对不上了，
/// 任务是「到点没跑」还是「跑早了」全凭运气。每分钟醒一次重算的代价可以忽略。
/// </para>
/// </remarks>
public abstract class ScheduledJobBase : BackgroundService
{
    /// <summary>单次休眠的上限：超过它就分片重算，见类头注释。</summary>
    private static readonly TimeSpan MaxSleepSlice = TimeSpan.FromMinutes(1);

    private readonly ILogger _logger;

    /// <summary>
    /// 构造函数。
    /// </summary>
    /// <param name="logger">日志记录器（由子类传入，使日志分类是子类而非本基类）。</param>
    protected ScheduledJobBase(ILogger logger) => _logger = logger;

    /// <summary>任务名称，仅用于日志。</summary>
    protected abstract string JobName { get; }

    /// <summary>决定本任务开关的配置项名（<c>Settlement</c> 节下的键名），仅用于日志。</summary>
    protected abstract string SettingName { get; }

    /// <summary>本任务是否已启用（由配置决定）。</summary>
    protected abstract bool IsEnabled { get; }

    /// <summary>每日触发时刻（本地时间）。</summary>
    protected abstract TimeOnly ScheduledAt { get; }

    /// <summary>
    /// 本实例是否应当执行定时任务（分布式部署的主节点判定）。
    /// </summary>
    protected abstract bool ShouldRun { get; }

    /// <summary>
    /// 到点后执行的实际工作。
    /// </summary>
    /// <param name="stoppingToken">停机令牌。</param>
    /// <returns>任务。</returns>
    /// <remarks>
    /// 本方法抛出的异常由基类接住并记日志，**不影响下一次触发**：
    /// 一天的收集失败不应让这个任务从此沉默。
    /// </remarks>
    protected abstract Task RunOnceAsync(CancellationToken stoppingToken);

    /// <inheritdoc />
    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled)
        {
            _logger.LogInformation(
                "定时任务「{JobName}」未启用，不再参与调度（置配置 {Section}:{Setting}=true 或对应环境变量可开启）",
                JobName,
                SettlementOptions.SectionName,
                SettingName);
            return;
        }

        if (!ShouldRun)
        {
            _logger.LogInformation(
                "本实例不是定时任务的主节点（{Env}={Node}），定时任务「{JobName}」不执行",
                ConfigConst.JOB_NODE_ENV,
                Environment.GetEnvironmentVariable(ConfigConst.JOB_NODE_ENV),
                JobName);
            return;
        }

        _logger.LogInformation(
            "定时任务「{JobName}」已启动，每天 {Time}（服务器本地时间 {Zone}）执行一次",
            JobName,
            ScheduledAt.ToString("HH\\:mm"),
            TimeZoneInfo.Local.Id);

        // 「下一次该在什么时候跑」是循环里唯一的**状态**：它由本次触发时刻往后推，
        // 因此「同一天被触发两次」在结构上就不可能发生，无需再拿一个「今天跑过了吗」的标志去判
        // （那种判法在跨零点时是错的：新的一天刚过 0 点、距触发时刻还有几小时，
        //  日期已经变了而时刻未到，只看日期会当场多跑一次）。
        var next = NextOccurrence(DateTime.Now);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now;
            var wait = next - now;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait > MaxSleepSlice ? MaxSleepSlice : wait, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 停机时正在跑：干净退出，不把它记成一次失败
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "定时任务「{JobName}」执行失败，将在下一个触发日重试", JobName);
            }

            // 从**当前时刻**重新推算下一次，而不是在旧值上加一天：
            // 机器休眠数日后醒来时旧值会落在很久以前，加一天仍然在过去，于是连着补跑好几次；
            // 而漏掉的那几天本来就会被收集窗口自动收进「水位到今天」这一整段里（见 ISettlementService.CollectAsync），
            // 补跑没有意义。如此 `next` 恒在未来，一天至多跑一次也就成立。
            next = NextOccurrence(DateTime.Now);
        }
    }

    /// <summary>
    /// 求严格晚于 <paramref name="after"/> 的下一个触发时刻。
    /// </summary>
    /// <param name="after">起始时刻。</param>
    /// <returns>下一个触发时刻（本地时间）。</returns>
    private DateTime NextOccurrence(DateTime after)
    {
        var candidate = after.Date.Add(ScheduledAt.ToTimeSpan());

        // `>` 而不是 `>=`：恰好等于触发时刻时应当推到明天，
        // 否则一次触发结束得足够快（在同一毫秒内返回）就会立刻再跑一次。
        return candidate > after ? candidate : candidate.AddDays(1);
    }
}
