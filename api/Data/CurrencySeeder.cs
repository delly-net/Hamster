using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Data;

/// <summary>
/// 常见币种播种：首次启动（币种表为空）时写入一份常用币种清单，并把人民币置为默认币种。
/// </summary>
/// <remarks>
/// 播种是**开箱可用**的前提：币种是账户的必填属性，一份空字典会让「新建账户」这一步直接走不通，
/// 逼用户先去管理页手工补几个常见币种——而那正是所有人都需要的那几个。
/// <para>
/// 与 <see cref="AdminSeeder"/> 同一约定：**只在表为空时执行**，已有数据一律原样保留。
/// 否则管理员删掉或停用掉的币种会在下次启动时被重新塞回来，管理页的改动形同虚设。
/// </para>
/// </remarks>
public static class CurrencySeeder
{
    /// <summary>
    /// 要播种的常见币种：代码、中文名、符号。
    /// </summary>
    /// <remarks>
    /// 列表顺序即界面呈现顺序（见 <see cref="Currency.SortOrder"/>），故把最常用的四种放最前。
    /// 前 8 位放得下三位字母代码：列表按**代码**而非名称排序时不会因为中文而次序混乱，
    /// 也让「代码 ≤ 8 位」这一列长约束（见 <see cref="Currency.Code"/>）自然成立。
    /// </remarks>
    private static readonly (string Code, string Name, string Symbol)[] COMMON_CURRENCIES =
    [
        ("CNY", "人民币", "¥"),
        ("USD", "美元", "$"),
        ("EUR", "欧元", "€"),
        ("JPY", "日元", "¥"),
        ("HKD", "港币", "HK$"),
        ("GBP", "英镑", "£"),
        ("AUD", "澳元", "A$"),
        ("CAD", "加元", "C$"),
        ("SGD", "新加坡元", "S$"),
        ("KRW", "韩元", "₩"),
        ("TWD", "新台币", "NT$"),
        ("THB", "泰铢", "฿"),
    ];

    /// <summary>
    /// 首个被播种的币种的代码，即系统默认币种。
    /// </summary>
    /// <remarks>
    /// 取 <see cref="COMMON_CURRENCIES"/> 的第一项而非把「CNY」再写一遍：
    /// 两处各写一遍时，调整清单顺序会让默认币种悄悄变成另一个，而两处看起来都「没错」。
    /// </remarks>
    public static string DefaultCode => COMMON_CURRENCIES[0].Code;

    /// <summary>
    /// 按需播种常见币种。
    /// </summary>
    /// <param name="db">SqlSugar 客户端。</param>
    /// <param name="logger">日志记录器。</param>
    /// <returns>本次实际写入的币种数量；表非空时为 0。</returns>
    /// <remarks>
    /// **必须在账户币种回填之前调用**（见 <see cref="DatabaseInitializer"/>）：
    /// 回填要把历史账户的币种填成默认币种，而默认币种正是本方法播种出来的。
    /// </remarks>
    public static int SeedIfEmpty(ISqlSugarClient db, ILogger logger)
    {
        // 只判「有没有数据」而不判「有没有默认币种」：管理员把默认币种停用或删改都属于管理页的
        // 正当操作，播种不该替他把改动纠正回来；表为空才是真正的「尚未初始化」。
        if (db.Queryable<Currency>().Any())
        {
            return 0;
        }

        var rows = COMMON_CURRENCIES
            .Select((item, index) => new Currency
            {
                Code = item.Code,
                Name = item.Name,
                Symbol = item.Symbol,
                IsActive = true,
                // 第一个即默认币种，其余一律不是——「全表至多一个为 true」的初始态由此成立
                IsDefault = index == 0,
                // 清单顺序即呈现顺序，故直接取下标
                SortOrder = index + 1,
            })
            .ToList();

        var written = db.Insertable(rows).ExecuteCommand();

        logger.LogInformation(
            "已播种 {Count} 个常见币种，默认币种为 {Code}",
            written,
            DefaultCode);

        return written;
    }
}
