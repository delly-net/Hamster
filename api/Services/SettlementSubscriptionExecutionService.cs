using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的结算订阅执行水位实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
public sealed class SettlementSubscriptionExecutionService(ISqlSugarClient db)
    : ISettlementSubscriptionExecutionService
{
    /// <inheritdoc />
    public async Task<DateTime?> FindLastExecutedDateAsync(
        string subscriptionCode,
        int accountSetId,
        CancellationToken cancellationToken = default)
    {
        var row = await db.Queryable<SettlementSubscriptionExecution>()
            .Where(execution => execution.SubscriptionCode == subscriptionCode &&
                                execution.AccountSetId == accountSetId)
            .FirstAsync(cancellationToken);

        return row?.LastExecutedDate;
    }

    /// <inheritdoc />
    public async Task<bool> AdvanceAsync(
        string subscriptionCode,
        int accountSetId,
        DateTime lastExecutedDate,
        CancellationToken cancellationToken = default)
    {
        // 日期只取到「天」：水位的粒度就是天（见 SettlementSubscriptionExecution.LastExecutedDate），
        // 调用方传进来的时刻部分一律丢掉，避免同一个水位因为多了几微秒而被判成「更晚」
        var targetDay = lastExecutedDate.Date;

        var existing = await db.Queryable<SettlementSubscriptionExecution>()
            .Where(execution => execution.SubscriptionCode == subscriptionCode &&
                                execution.AccountSetId == accountSetId)
            .FirstAsync(cancellationToken);

        if (existing is null)
        {
            var now = DateTime.UtcNow;
            await db.Insertable(new SettlementSubscriptionExecution
            {
                SubscriptionCode = subscriptionCode,
                AccountSetId = accountSetId,
                LastExecutedDate = targetDay,
                LastExecutedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            }).ExecuteCommandAsync(cancellationToken);

            return true;
        }

        // 不比已记日期更晚 → 空转，整行不写。
        // 这里的比较放在 C# 侧而不是写进 SQL：水位列是按「本地日 0 点」写入的，
        // 但 Sqlite 把它存成文本、格式取决于写入路径，交给 SQL 比大小就要为文本格式背书；
        // 而在 C# 里比的是真正的 DateTime（同 SettlementService.LoadWatermarksAsync 的取舍）。
        if (targetDay <= existing.LastExecutedDate)
        {
            return false;
        }

        var advancedAt = DateTime.UtcNow;
        await db.Updateable<SettlementSubscriptionExecution>()
            .SetColumns(execution => new SettlementSubscriptionExecution
            {
                LastExecutedDate = targetDay,
                LastExecutedAt = advancedAt,
                UpdatedAt = advancedAt,
            })
            .Where(execution => execution.Id == existing.Id)
            .ExecuteCommandAsync(cancellationToken);

        return true;
    }
}
