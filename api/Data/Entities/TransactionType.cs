namespace Hamster.Api.Data.Entities;

/// <summary>
/// 交易类型：决定一笔交易在记账语义上因何而发生。
/// </summary>
/// <remarks>
/// **每个取值都必须有真实的落库写入路径**：枚举值是直接落库的数据契约，先占位再实现会让
/// 「库里有值、代码里没有产生它的路径」这种无从判断真伪的状态出现。新增交易类型时在此追加，
/// 并同步落库写入路径与文档。
/// <para>
/// 三种取值按「谁能产生它」分成两档，判定见 <see cref="TransactionTypeExtensions.IsUserRecordable"/>：
/// <see cref="OpeningBalance"/> 由系统在账户创建时自动生成；<see cref="Income"/> 与
/// <see cref="Expense"/> 由用户经记账端点手工写入。
/// </para>
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

    /// <summary>收入：钱进入目标账户（目标账户记借方、系统账本账户记贷方）。</summary>
    Income = 2,

    /// <summary>支出：钱离开目标账户（目标账户记贷方、系统账本账户记借方）。</summary>
    Expense = 3,
}

/// <summary>
/// 交易类型的可用性判定。
/// </summary>
/// <remarks>
/// 「哪些交易类型可由用户手工记账」只在这里判定一次：端点的参数校验与错误文案都从这里派生，
/// 新增枚举取值时不会漏掉校验，也不会出现「文案说可记、代码其实拒绝」这类两处漂移。
/// <para>
/// 之所以把规则挂在枚举旁而不是写进端点：它是**类型的固有属性**（能不能由用户产生），
/// 与「谁来校验」无关。这与 <see cref="AccountTypeExtensions.IsUserAssignable"/> 是同一取舍。
/// </para>
/// </remarks>
public static class TransactionTypeExtensions
{
    /// <summary>
    /// 该类型是否可由用户手工记账（经记账端点写入）。
    /// </summary>
    /// <param name="type">交易类型。</param>
    /// <returns>可由用户记账返回 <c>true</c>。</returns>
    /// <remarks>
    /// <see cref="TransactionType.OpeningBalance"/> 返回 <c>false</c>：期初余额由系统在账户创建时
    /// 自动生成，若能再手工记一笔「期初」，两个不变量会同时失效——
    /// 「期初交易金额恒等于账户的期初金额」与「每账户至多一条期初分录」
    /// （后者是 <c>TransactionService.BackfillOpeningBalancesAsync</c> 的幂等判据）。
    /// </remarks>
    public static bool IsUserRecordable(this TransactionType type) =>
        type is TransactionType.Income or TransactionType.Expense;
}
