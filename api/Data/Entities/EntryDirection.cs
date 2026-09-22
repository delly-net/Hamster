namespace Hamster.Api.Data.Entities;

/// <summary>
/// 交易明细的借贷方向。
/// </summary>
/// <remarks>
/// <para>
/// **本项目统一取「<see cref="Debit"/> = 余额增加、<see cref="Credit"/> = 余额减少」**，
/// 不按账户类型（资产/负债）翻转符号。传统会计里负债类账户是「贷增借减」，但本项目的账户
/// 只是记账的挂靠对象（见 <see cref="AccountType"/>），负债账户的期初金额直接以**负数**表达
/// （见 <see cref="Account.InitialBalance"/>）。若再叠加一层按类型翻转的分支，
/// 同一笔分录在不同账户上会得出相反的符号，汇总余额时就必须先查账户类型——
/// 一条换算规则变成两条，且两处必然漂移。
/// </para>
/// <para>
/// 因此「方向 → 账户余额」的换算**只有一处**（<c>TransactionService.SumSignedAmountsAsync</c>）：
/// <c>Debit</c> 取正、<c>Credit</c> 取负。负债账户期初为 −500 时自然落成
/// <c>Credit 500</c>，无需任何特例。
/// </para>
/// <para>
/// 显式赋值而非依赖声明顺序：该值直接落库，调整枚举顺序会把既有数据解释成另一个方向。
/// </para>
/// </remarks>
public enum EntryDirection
{
    /// <summary>借方：账户余额增加。</summary>
    Debit = 1,

    /// <summary>贷方：账户余额减少。</summary>
    Credit = 2,
}
