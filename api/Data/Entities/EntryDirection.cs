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
/// 因此「方向 → 账户余额」的换算**只有一个定义**（<see cref="EntryDirectionExtensions.SignedAmount"/>）：
/// <c>Debit</c> 取正、<c>Credit</c> 取负。负债账户期初为 −500 时自然落成
/// <c>Credit 500</c>，无需任何特例。
/// </para>
/// <para>
/// 换算的**定义**在此处、而**调用**有两处（余额汇总与明细出参，见
/// <see cref="EntryDirectionExtensions.SignedAmount"/> 的说明）：两处都要「方向取符号」这个事实，
/// 让它们共用同一个定义，比让第二处照着第一处再写一遍更可靠。
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

/// <summary>
/// 借贷方向的符号换算。
/// </summary>
/// <remarks>
/// 「明细方向 → 带符号金额」的**唯一定义**：<c>Debit</c> 为正、<c>Credit</c> 为负，
/// 含义是「该条明细对它所挂靠账户的余额增减了多少」。
/// <para>
/// 两个调用点共用它，而非各自再写一次三元表达式：
/// <list type="bullet">
/// <item>账户余额汇总（<c>TransactionService.SumSignedAmountsAsync</c>）——对聚合后的金额取符号；</item>
/// <item>账目明细出参（<c>EntryEndpoint.EntryDto.From</c>）——对单条明细取符号，
/// 供界面按「收入 / 支出」分列呈现。</item>
/// </list>
/// 一旦两处各写一遍，符号口径就会各自漂移，而「同一笔明细在余额汇总与明细列表里符号相反」
/// 是那种「数字对不上却极难查」的错误。
/// </para>
/// <para>
/// 规则挂在枚举旁而非某个服务里：它是**方向的固有属性**（方向决定符号），与「谁来用」无关，
/// 这与 <c>AccountTypeExtensions.IsUserAssignable</c> 的组织方式一致。
/// </para>
/// </remarks>
public static class EntryDirectionExtensions
{
    /// <summary>
    /// 把恒正的金额按方向折成带符号金额。
    /// </summary>
    /// <param name="direction">借贷方向。</param>
    /// <param name="amount">
    /// 金额，**恒为正**（这是明细表的存储约定：符号从不入库，见 <see cref="EntryDirection"/>）。
    /// </param>
    /// <returns>借方为 <paramref name="amount"/> 本身，贷方为其相反数。</returns>
    public static decimal SignedAmount(this EntryDirection direction, decimal amount) =>
        direction == EntryDirection.Debit ? amount : -amount;
}
