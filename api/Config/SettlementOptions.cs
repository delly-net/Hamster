using System.Globalization;

namespace Hamster.Api.Config;

/// <summary>
/// 结算定时任务配置，对应 appsettings.json 的 <c>Settlement</c> 节点（环境变量优先）。
/// </summary>
/// <remarks>
/// 两个定时任务各自有「是否生效」开关与「触发时刻」两项：
/// <list type="bullet">
/// <item><b>开关</b>是任务描述里的明确要求（支持环境变量配置是否有效）；</item>
/// <item><b>时刻</b>做成可配置则有两个理由：部署时调整结算时点无需改代码；
/// 且固定写死的 <c>00:05</c> 无法在验证里等待，把时刻放开后隔离实例才能在一分钟内
/// 观察到真实的「触发 → 收集 → 落库」全过程。</item>
/// </list>
/// <para>
/// **默认值即任务要求的 00:05 与 01:00**，因此不配置任何环境变量时行为与需求逐字一致。
/// </para>
/// <para>
/// **刻意不走 <c>ConfigurationBinder</c> 绑定整个节**：时刻在本类里是 <see cref="TimeOnly"/>，
/// 而绑定器遇到空串（<c>"CollectTime": ""</c>，部署时注释掉取值又会留下这一行）
/// 会直接抛 <see cref="InvalidOperationException"/> 让进程起不来；而「取值无法识别」
/// 在本项目里一贯是「回落默认 + 告警」（同 <c>DatabaseOptions.ParseDbType</c>）。
/// 故本类逐项读取字面量再自行解析，配置节与环境变量两条路径共用同一份解析。
/// </para>
/// </remarks>
public sealed class SettlementOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Settlement";

    /// <summary>交易统计（结算收集）定时任务是否生效，默认开启。</summary>
    public bool CollectEnabled { get; set; } = true;

    /// <summary>结算执行定时任务是否生效，默认开启。</summary>
    public bool ExecuteEnabled { get; set; } = true;

    /// <summary>交易统计（结算收集）定时任务的触发时刻（本地时间），默认 <c>00:05</c>。</summary>
    public TimeOnly CollectTime { get; set; } = TimeOnly.ParseExact(ConfigConst.DEFAULT_SETTLEMENT_COLLECT_TIME, "HH\\:mm", CultureInfo.InvariantCulture);

    /// <summary>结算执行定时任务的触发时刻（本地时间），默认 <c>01:00</c>。</summary>
    public TimeOnly ExecuteTime { get; set; } = TimeOnly.ParseExact(ConfigConst.DEFAULT_SETTLEMENT_EXECUTE_TIME, "HH\\:mm", CultureInfo.InvariantCulture);

    /// <summary>
    /// 本实例的节点名；留空表示未声明。
    /// </summary>
    public string NodeName { get; set; } = string.Empty;

    /// <summary>
    /// 本实例是否应当在定时任务触发时真正执行。
    /// </summary>
    /// <remarks>
    /// 这是**分布式部署的互斥机制**（多实例同时运行时只有一个实例干活）：
    /// <list type="bullet">
    /// <item><b>留空</b> → 视为单实例部署，**照常执行**。这一支是刻意的：绝大多数部署只有
    /// 一个实例，要求它们必须配一个环境变量才肯工作，会把「默认可用」变成「默认不可用」。</item>
    /// <item><c>master</c>（不区分大小写）→ 主节点，执行。</item>
    /// <item>其余任意值（如 <c>worker</c>）→ 从节点，不执行。</item>
    /// </list>
    /// <para>
    /// **它不是唯一的安全网**：<c>hamster_settlement_task</c> 上
    /// <c>(account_set_id, transaction_date)</c> 的唯一索引让「两个主节点同时在跑」
    /// 退化成「一个成功、另一个在日志里报一条冲突」，而不是把同一笔账结算两遍。
    /// 但那是**兜底**，不承担互斥职责——真正的互斥仍然靠本属性，这一点在 README 里也已写明，
    /// 以免后来者以为「有唯一索引了，节点名随便配」。
    /// </para>
    /// </remarks>
    public bool IsMasterNode =>
        string.IsNullOrWhiteSpace(NodeName) ||
        string.Equals(NodeName.Trim(), ConfigConst.MASTER_NODE_NAME, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从配置与环境变量解析，环境变量优先。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <param name="logger">日志记录器，用于提示无法识别的取值。</param>
    /// <returns>结算任务配置实例。</returns>
    public static SettlementOptions From(IConfiguration configuration, ILogger logger)
    {
        var section = configuration.GetSection(SectionName);

        var options = new SettlementOptions
        {
            CollectEnabled = ReadBool(section, nameof(CollectEnabled), true, logger),
            ExecuteEnabled = ReadBool(section, nameof(ExecuteEnabled), true, logger),
            CollectTime = ReadTime(
                Environment.GetEnvironmentVariable(ConfigConst.SETTLEMENT_COLLECT_TIME_ENV),
                section[nameof(CollectTime)],
                ConfigConst.SETTLEMENT_COLLECT_TIME_ENV,
                ConfigConst.DEFAULT_SETTLEMENT_COLLECT_TIME,
                logger),
            ExecuteTime = ReadTime(
                Environment.GetEnvironmentVariable(ConfigConst.SETTLEMENT_EXECUTE_TIME_ENV),
                section[nameof(ExecuteTime)],
                ConfigConst.SETTLEMENT_EXECUTE_TIME_ENV,
                ConfigConst.DEFAULT_SETTLEMENT_EXECUTE_TIME,
                logger),
        };

        var envNode = Environment.GetEnvironmentVariable(ConfigConst.JOB_NODE_ENV);
        options.NodeName = !string.IsNullOrWhiteSpace(envNode)
            ? envNode.Trim()
            : section[nameof(NodeName)]?.Trim() ?? string.Empty;

        return options;
    }

    /// <summary>
    /// 读取布尔开关：环境变量优先于配置节，两者都取不到或都无法识别时用默认值。
    /// </summary>
    /// <remarks>
    /// 无法识别的取值（如 <c>HAMSTER_SETTLEMENT_COLLECT_ENABLED=yes</c>）**一律回落默认值并告警**，
    /// 不抛异常——一个笔误不该让整个服务起不来，但也不能静默：
    /// 用户会看到「我明明关了它，怎么还在跑」而无从解释。
    /// </remarks>
    private static bool ReadBool(
        IConfigurationSection section,
        string name,
        bool fallback,
        ILogger logger)
    {
        var envName = name switch
        {
            nameof(CollectEnabled) => ConfigConst.SETTLEMENT_COLLECT_ENABLED_ENV,
            nameof(ExecuteEnabled) => ConfigConst.SETTLEMENT_EXECUTE_ENABLED_ENV,
            _ => string.Empty,
        };

        var raw = Environment.GetEnvironmentVariable(envName);
        var source = envName;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = section[name];
            source = $"{SectionName}:{name}";
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (bool.TryParse(raw.Trim(), out var parsed))
        {
            return parsed;
        }

        logger.LogWarning(
            "无法识别的开关取值 {Value}（来自 {Source}），已回落到默认值 {Fallback}；正确写法为 true 或 false",
            raw,
            source,
            fallback);
        return fallback;
    }

    /// <summary>
    /// 读取 <c>HH:mm</c> 形式的时刻：环境变量优先于配置节，两者都取不到或都无法识别时用默认值。
    /// </summary>
    /// <param name="envValue">环境变量原始取值（可为空）。</param>
    /// <param name="configuredValue">配置节原始取值（可为空）。</param>
    /// <param name="source">取值来源标签，仅用于告警文案。</param>
    /// <param name="fallback">默认时刻字面量。</param>
    /// <param name="logger">日志记录器。</param>
    /// <returns>解析出的时刻；无有效取值时为默认值。</returns>
    private static TimeOnly ReadTime(
        string? envValue,
        string? configuredValue,
        string source,
        string fallback,
        ILogger logger)
    {
        var raw = !string.IsNullOrWhiteSpace(envValue) ? envValue : configuredValue;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TimeOnly.ParseExact(fallback, "HH\\:mm", CultureInfo.InvariantCulture);
        }

        raw = raw.Trim();

        // 限定 HH:mm 一种写法，而不是放任 TimeOnly.Parse 去猜：Parse 会把 "5"、"0:5" 之类
        // 的写法一并接受，部署脚本里一个笔误就会被静默解释成一个并非本意的时刻。
        if (TimeOnly.TryParseExact(raw, "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        logger.LogWarning(
            "无法识别的结算任务触发时刻 {Value}（来自 {Source}），已回落到 {Fallback}；正确写法为 HH:mm，例如 00:05",
            raw,
            source,
            fallback);
        return TimeOnly.ParseExact(fallback, "HH\\:mm", CultureInfo.InvariantCulture);
    }
}
