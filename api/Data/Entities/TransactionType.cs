namespace Hamster.Api.Data.Entities;

/// <summary>
/// 交易类型：决定一笔交易在记账语义上因何而发生。
/// </summary>
/// <remarks>
/// 当前**只有期初余额一种**——刻意不预设尚未落地写入路径的收入/支出/转账等取值：
/// 枚举值是直接落库的数据契约，先占位再实现会让「库里有值、代码里没有产生它的路径」
/// 这种无从判断真伪的状态出现。新增交易类型时在此追加，并同步落库写入路径与文档。
/// <para>
/// 显式赋值而非依赖声明顺序：该值直接落库，调整枚举顺序会把既有数据解释成另一种类型。
/// </para>
/// </remarks>
public enum TransactionType
{
    /// <summary>
    /// 期初余额：账户建立时已有的金额，由账户创建（或升级回填）时自动生成。
    /// </summary>
    /// <remarks>
    /// 该类型交易的金额恒等于其目标账户的 <see cref="Account.InitialBalance"/>，
    /// 且每个账户至多一条（由 <c>TransactionService</c> 在写入前查重保证，不依赖数据库唯一索引）。
    /// </remarks>
    OpeningBalance = 1,
}
