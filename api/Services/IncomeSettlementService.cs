using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 收入结算实现：把共享骨架（<see cref="DailyFlowSettlementServiceBase"/>）实例化为「收入」这一套。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <param name="transactions">交易业务服务：当日收入发生额的**唯一**来源。</param>
/// <param name="accountSets">账套服务：取账套成员列表。</param>
/// <param name="executions">订阅执行水位。</param>
/// <param name="settlements">结算服务：取「最早的未执行结算任务日期」，用于给下界让出被补收的那一天。</param>
/// <param name="logger">日志记录器。</param>
/// <remarks>
/// 本类**只写与「收入」这张表有关的那几笔查询**（读覆盖度、删某日、插某日、读某月），
/// 逐日重算的顺序、水位推进、事务与覆盖写的口径全在基类里——那几处是共享的、也是最容易改错的。
/// </remarks>
public sealed class IncomeSettlementService(
    ISqlSugarClient db,
    ITransactionService transactions,
    IAccountSetService accountSets,
    ISettlementSubscriptionExecutionService executions,
    ISettlementService settlements,
    ILogger<IncomeSettlementService> logger)
    : DailyFlowSettlementServiceBase(db, transactions, accountSets, executions, settlements, logger), IIncomeSettlementService
{
    /// <inheritdoc />
    protected override string SubscriptionCode => IIncomeSettlementService.SUBSCRIPTION_CODE;

    /// <inheritdoc />
    protected override string FlowLabel => "收入";

    /// <inheritdoc />
    protected override TransactionType FlowType => TransactionType.Income;

    /// <inheritdoc />
    protected override async Task<IReadOnlyDictionary<int, DateTime>> LoadLastRecordedDayByUserAsync(
        int accountSetId,
        CancellationToken cancellationToken)
    {
        var stored = await Db.Queryable<IncomeSettlementRecord>()
            .Where(record => record.AccountSetId == accountSetId)
            .Select(record => new { record.UserId, record.TransactionDate })
            .ToListAsync(cancellationToken);

        // 取每个成员的最后一天，比较在 C# 侧（同 SettlementService.LoadWatermarksAsync 的取舍：
        // 日期在 Sqlite 里是文本，聚合的次序交给数据库不划算）
        var lastDayByUser = new Dictionary<int, DateTime>();
        foreach (var row in stored)
        {
            if (!lastDayByUser.TryGetValue(row.UserId, out var current) || row.TransactionDate > current)
            {
                lastDayByUser[row.UserId] = row.TransactionDate;
            }
        }

        return lastDayByUser;
    }

    /// <inheritdoc />
    protected override async Task DeleteDayAsync(
        int accountSetId,
        DateTime dayStart,
        DateTime dayEnd,
        CancellationToken cancellationToken) =>
        await Db.Deleteable<IncomeSettlementRecord>()
            .Where(record => record.AccountSetId == accountSetId &&
                             record.TransactionDate >= dayStart &&
                             record.TransactionDate < dayEnd)
            .ExecuteCommandAsync(cancellationToken);

    /// <inheritdoc />
    protected override async Task InsertDayAsync(
        int accountSetId,
        DateTime localDay,
        IReadOnlyList<DailyFlowRow> rows,
        CancellationToken cancellationToken)
    {
        // 同一次落库共用同一个时刻：逐行各取一次 UtcNow 会让同一秒里的记录带上微秒级差异，
        // 「这批记录是同一批重算写出来的」这件事在库里就看不出来了（同 TotalAssetSettlementService）。
        var recordedAt = DateTime.UtcNow;

        var records = rows.Select(row => new IncomeSettlementRecord
        {
            AccountSetId = accountSetId,
            UserId = row.UserId,
            CurrencyCode = row.CurrencyCode,
            TransactionDate = localDay,
            PersonalIncomeTotal = row.PersonalTotal,
            PublicIncomeTotal = row.PublicTotal,
            CreatedAt = recordedAt,
            UpdatedAt = recordedAt,
        }).ToList();

        await Db.Insertable(records).ExecuteCommandAsync(cancellationToken);
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyList<DailyFlowPoint>> ListDailyAsync(
        int accountSetId,
        int userId,
        string currencyCode,
        DateTime monthStart,
        CancellationToken cancellationToken = default)
    {
        var start = monthStart.Date;
        var end = start.AddMonths(1);

        // 区间直接比较、不做 ±1s 预筛：本表的时间列由本表自己写入、恒为本地日 0 点，
        // 与这里的两个参数逐字同格式（日期列没有「用户手工输入的时间戳」那种精度差异，
        // 见 LocalDay.PrefilterMargin 所针对的情形）。
        var records = await Db.Queryable<IncomeSettlementRecord>()
            .Where(record => record.AccountSetId == accountSetId &&
                             record.UserId == userId &&
                             record.CurrencyCode == currencyCode)
            .Where(record => record.TransactionDate >= start && record.TransactionDate < end)
            .OrderBy(record => record.TransactionDate)
            .ToListAsync(cancellationToken);

        return
        [
            .. records.Select(record => new DailyFlowPoint(
                record.TransactionDate,
                record.PersonalIncomeTotal,
                record.PublicIncomeTotal)),
        ];
    }
}
