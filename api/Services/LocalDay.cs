namespace Hamster.Api.Services;

/// <summary>
/// 「本地日」与 UTC 时刻之间的换算，以及日界比较在 SQL 侧的统一预筛口径。
/// </summary>
/// <remarks>
/// 本类只有两件事，但两件都是**口径**而不是工具函数，故必须只此一处：
/// <list type="number">
/// <item>
/// **本地日的 0 点 ↔ UTC 时刻**：结算的收集窗口、总资产结算的「该日结束时的余额」都用它。
/// 两处若各写一份，时区口径就会各自漂移——一处改成固定偏移、另一处没改，
/// 表现是「同一天在结算页与首页分到了不同的日子」。
/// </item>
/// <item>
/// **日界比较在 SQL 侧需要放宽的秒数**（<see cref="PrefilterMargin"/>）：这是对
/// 「Sqlite 把时间存成文本」这一事实的补偿，凡是拿一个日界时刻去和 <c>occurred_at</c> 比大小的
/// 地方都吃这一条。写成两份的话，一处放宽一处不放宽，两个页面的边界表现就会不同。
/// </item>
/// </list>
/// <para>
/// 本类**不持有状态**：时区由调用方解析一次后自行缓存（见 <see cref="ResolveTimeZone"/>），
/// 因为调用点通常在一次执行里要反复换算，每次进本类都查一次系统时区库是白费。
/// </para>
/// </remarks>
internal static class LocalDay
{
    /// <summary>
    /// 日界比较在 SQL 侧预筛时向两侧放宽的秒数。
    /// </summary>
    /// <remarks>
    /// **为什么需要它**：Sqlite 把 <c>DateTime</c> 存成**文本**，而历史行的格式并不统一——
    /// 有 <c>2026-09-23 15:06:00.5630041</c>（带小数部分）也有 <c>2026-09-23 15:06:00</c>
    /// （无小数部分，用户记账时的时间戳精确到分钟即如此）。两者按字符串比较时，
    /// 无小数部分的那个排在**同一个整秒的前面**，于是「恰好落在日界上」的一笔账与边界比较时
    /// 可能被判到边界之外，凭空漏掉。
    /// <para>
    /// 故 SQL 只做**预筛**：把边界向两侧各放宽一秒取回候选行，再由 C# 用真正的
    /// <see cref="DateTime"/> 比较做**精确**判定。多取回的行至多几笔、代价可忽略，
    /// 换来的是边界判定不依赖数据库的文本格式。
    /// </para>
    /// <para>
    /// **放宽的方向由调用方决定，本常量只给秒数**：结算收集是「取窗口内的行」，
    /// 故两侧都放宽后逐行精确过滤；总资产结算是「累计到某时刻为止」，预筛只放宽上界、
    /// 再把多取回的 [界, 界+1s) 那几行精确地减掉（见
    /// <c>TransactionService.SumSignedAmountsAsync</c>）。
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan PrefilterMargin = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 解析服务器本地时区，失败时回落 UTC 并告警。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <returns>本地时区；无法解析时为 UTC。</returns>
    /// <remarks>
    /// <see cref="TimeZoneInfo.Local"/> 在本进程里理论上不会抛异常，此处仍兜一层：
    /// 结算的日期分组一旦没有时区可用就无从谈起，宁可退化成「按 UTC 日分组」也不能让
    /// 定时任务的宿主构造失败——那会让**整个应用起不来**，代价远大于结算口径不精确。
    /// <para>
    /// 调用方应在构造时解析一次并缓存：<see cref="TimeZoneInfo.Local"/> 每次访问都可能
    /// 重新查一次系统时区库，而一次收集里要反复换算（每个交易、每一天都要换）。
    /// </para>
    /// <para>
    /// **它在本项目里的特殊性**：<c>Hamster.Api.csproj</c> 开着 <c>InvariantGlobalization</c>，
    /// 此处刻意不去改成固定偏移（如 +08:00）——「交易日期的分组口径」应当跟随部署所在地，
    /// 写死偏移会让一台部署在其它时区的实例把用户的账分到错误的日期上。
    /// 启动日志里会打印解析结果（见 <c>Program.LogStartupInfo</c>），运维可据此确认
    /// 「本地时区」在这台机器上到底是什么。
    /// </para>
    /// </remarks>
    internal static TimeZoneInfo ResolveTimeZone(ILogger logger)
    {
        try
        {
            return TimeZoneInfo.Local;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "无法解析服务器本地时区，交易日期的分组口径已回落为 UTC 日");
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// 把**本地日**的 0 点换算成 UTC 时刻。
    /// </summary>
    /// <param name="localDay">本地日期（时刻部分被忽略）。</param>
    /// <param name="timeZone">本地时区（由 <see cref="ResolveTimeZone"/> 解析并缓存）。</param>
    /// <returns>该本地日 0 点对应的 UTC 时刻。</returns>
    /// <remarks>
    /// 用 <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> 而不是 <c>ConvertTimeToUtc</c>：
    /// 后者在「夏令时向前跳」的那一天遇到不存在的本地时刻（如 2 点整跳到 3 点时的 2:30）
    /// 会抛 <see cref="ArgumentException"/>，一个每天都要跑的定时任务不该因为某年某一天而整体失败。
    /// <c>GetUtcOffset</c> 对不存在的时刻给的是跳变前的偏移，对重复的时刻给的是标准时间偏移，
    /// 两个取值都是确定的、不会抛异常。中国不使用夏令时，本条在本地是无差别的保险。
    /// </remarks>
    internal static DateTime StartToUtc(DateTime localDay, TimeZoneInfo timeZone) =>
        DateTime.SpecifyKind(
            DateTime.SpecifyKind(localDay.Date, DateTimeKind.Unspecified) - timeZone.GetUtcOffset(localDay.Date),
            DateTimeKind.Utc);

    /// <summary>
    /// 把一个 UTC 时刻换算成**本地日期**（时刻部分为 00:00:00）。
    /// </summary>
    /// <param name="utc">UTC 时刻。</param>
    /// <param name="timeZone">本地时区（由 <see cref="ResolveTimeZone"/> 解析并缓存）。</param>
    /// <returns>本地日期。</returns>
    /// <remarks>
    /// 先 <c>SpecifyKind</c> 再换算：从库里读回的时间 <c>Kind</c> 是 <c>Unspecified</c>
    /// （两种数据库都不保存 Kind），而 <see cref="TimeZoneInfo.ConvertTimeFromUtc"/> 只在
    /// <c>Kind</c> 不是 <c>Local</c> 时才把它当作 UTC 处理。显式声明一次，
    /// 让「这一列存的就是 UTC」成为代码里的事实而不是隐含前提。
    /// </remarks>
    internal static DateTime DateOf(DateTime utc, TimeZoneInfo timeZone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone).Date;
}
