using Hamster.Api.Services;

namespace Hamster.Api.Data;

/// <summary>
/// 期初余额回填：为**缺少期初分录**的既有账户补写期初交易。
/// </summary>
/// <remarks>
/// 交易表与明细表是随复式记账落地才引入的，但账户表在此前就已存在，其中的
/// <c>initial_balance</c> 是账户建立当时写下的期初金额。若不回填，这些账户就永远没有期初分录——
/// 而余额的派生口径是「全部明细的有符号汇总」（期初金额由期初分录承载），
/// 于是它们的余额会显示成 0，与期初金额对不上。
/// <para>
/// 必须在 <see cref="DatabaseInitializer.InitializeDatabase"/>（建表）之后执行，
/// 否则两张新表尚不存在。回填本身幂等（见 <see cref="ITransactionService.BackfillOpeningBalancesAsync"/>），
/// 每次启动都调用一次是安全的。
/// </para>
/// </remarks>
public static class OpeningBalanceBackfill
{
    /// <summary>
    /// 为既有账户补写期初分录。
    /// </summary>
    /// <param name="app">Web 应用实例。</param>
    /// <remarks>
    /// 与 <see cref="DatabaseInitializer"/> / <see cref="AdminSeeder"/> 一致：失败仅记录告警，不阻断应用启动。
    /// 回填是补救动作，让它挡住启动会让一次数据异常升级成服务不可用。
    /// </remarks>
    public static void BackfillOpeningBalances(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hamster.Api.Data");

        try
        {
            var transactions = app.Services.GetRequiredService<ITransactionService>();

            // 播种发生在启动阶段、无请求上下文，此处同步等待是安全的（同 AdminSeeder）
            var written = transactions.BackfillOpeningBalancesAsync().GetAwaiter().GetResult();

            if (written > 0)
            {
                logger.LogInformation("已为 {Count} 个既有无期初分录的账户补写期初余额交易", written);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "期初余额回填失败，应用继续启动；既有账户的期初分录仍会缺位，可检查数据库连接后重启重试（回填幂等）");
        }
    }
}
