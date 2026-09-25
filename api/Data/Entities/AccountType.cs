namespace Hamster.Api.Data.Entities;

/// <summary>
/// 账户类型：决定该账户在记账语义上代表什么。
/// </summary>
/// <remarks>
/// 四类取值由业务固定，不由用户扩展。显式赋值而非依赖声明顺序：该值直接落库，
/// 调整枚举顺序会把既有数据解释成另一种类型。
/// <para>
/// 四类取值**并非都可由用户指定**：<see cref="AccountType.Ledger"/> 是系统内部账户类型，
/// 是否可用用户指定见 <see cref="AccountTypeExtensions.IsUserAssignable"/>。
/// </para>
/// </remarks>
public enum AccountType
{
    /// <summary>
    /// 账本账户：记账用的汇总性账户，本身不代表具体的钱。
    /// </summary>
    /// <remarks>
    /// **不接受用户手工指定或变更**，也**不在账户列表中呈现**——它只作为复式记账的对手方存在：
    /// 期初余额、收入与支出都以它配平，由系统按账套按需自动创建
    /// （见 <c>TransactionService.EnsureLedgerAccountAsync</c>）。
    /// 作为对手方，它照常计入余额汇总与复式配平，只是不出现在界面上。
    /// </remarks>
    Ledger = 1,

    /// <summary>资金账户：实实在在的钱（现金、银行卡、电子钱包等）。</summary>
    Fund = 2,

    /// <summary>负债账户：欠别人的钱（信用卡、借款等），期初金额允许为负。</summary>
    Liability = 3,

    /// <summary>往来账户：人情往来与应收应付（借出、借入、待收报销等）。</summary>
    /// <remarks>
    /// 它记录的是「谁欠谁」而不是「钱放在哪」，故不属于钱账户
    /// （见 <see cref="AccountTypeExtensions.IsMoneyAccount"/>）：不能作为转账的端点，
    /// 其明细也不在「账目明细」页呈现。它照常出现在账户管理页，并作为**对手方**
    /// 出现在钱账户那些明细行的对手方列上——「支出 现金 → 老王」的那笔往来正记在它上面。
    /// </remarks>
    Contact = 4,
}

/// <summary>
/// 账户类型的可用性判定。
/// </summary>
/// <remarks>
/// 「哪些类型可由用户手工建立或变更」只在这里判定一次：端点的参数校验与错误文案都从这里派生，
/// 新增枚举取值时不会漏掉校验，也不会出现「文案说可选、代码其实拒绝」这类两处漂移。
/// <para>
/// 之所以把规则挂在枚举旁而不是写进端点：它是**类型的固有属性**（能不能被用户指定），
/// 与「谁来校验」无关。
/// </para>
/// </remarks>
public static class AccountTypeExtensions
{
    /// <summary>
    /// 该类型是否可由用户手工指定（新建账户时选择、修改账户时变更）。
    /// </summary>
    /// <param name="type">账户类型。</param>
    /// <returns>可由用户指定返回 <c>true</c>。</returns>
    /// <remarks>
    /// <see cref="AccountType.Ledger"/> 返回 <c>false</c>：账本账户由系统在首次需要对手方时按需创建，
    /// 它承担的是复式配平的对手方角色，每个账套恒只有一个，放开给用户手工建立会破坏该不变量。
    /// </remarks>
    public static bool IsUserAssignable(this AccountType type) => type != AccountType.Ledger;

    /// <summary>
    /// 该类型是否是「**钱本身**」——钱实际放在哪里。
    /// </summary>
    /// <param name="type">账户类型。</param>
    /// <returns>是钱账户返回 <c>true</c>。</returns>
    /// <remarks>
    /// 只有 <see cref="AccountType.Fund"/>（资金账户）与 <see cref="AccountType.Liability"/>（负债账户）
    /// 返回 <c>true</c>——现金、银行卡、电子钱包，以及信用卡、借款。
    /// <para>
    /// <see cref="AccountType.Contact"/> 返回 <c>false</c>：往来账户是人情往来与应收应付，
    /// 它记录的是「谁欠谁」而不是「钱放在哪」。
    /// <see cref="AccountType.Ledger"/> 返回 <c>false</c>：它是系统内部账户，对任何用户不呈现，
    /// 也不接受手工指定（见 <see cref="IsUserAssignable"/>）。
    /// </para>
    /// <para>
    /// 本谓词是**共用定义**，当前有两个消费点，且两处的依据是同一句话（往来账户不是钱）：
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// **转账的端点**（<see cref="IsTransferAccount"/>）：把钱在两处「钱」之间挪动，两端都必须是钱。
    /// </description></item>
    /// <item><description>
    /// **账目明细的呈现**（<c>EntryQueryService</c>）：明细页回答的是「钱动在哪个账户」，
    /// 往来账户上的明细是「谁欠谁」的另一本账，不在该页呈现；它只作为**对手方**出现在
    /// 钱账户那些行的对手方列上（如「支出 现金 → 老王」）。
    /// </description></item>
    /// </list>
    /// <para>
    /// 两条规则是同一条集合，故只在此处写一遍：写成两份清单必然漂移——新增账户类型时改了一处、
    /// 另一处静默地继续把它当钱看。
    /// </para>
    /// <para>
    /// **注意**：与 <see cref="IsUserAssignable"/> 一样，扩展方法**不能被 SqlSugar 翻译**成 SQL。
    /// 本谓词只能作用在**已在内存里**的账户列表上（<c>EntryQueryService</c> 正是如此），
    /// 不得写进查询表达式树——SQL 侧需要该条件时只能写枚举字面量，理由见
    /// <c>AccountService.ListByAccountSetAsync</c>。
    /// </para>
    /// </remarks>
    public static bool IsMoneyAccount(this AccountType type) =>
        type is AccountType.Fund or AccountType.Liability;

    /// <summary>
    /// 该类型是否可作为一笔转账的转出账户或转入账户。
    /// </summary>
    /// <param name="type">账户类型。</param>
    /// <returns>可作为转账端点返回 <c>true</c>。</returns>
    /// <remarks>
    /// 转账的语义就是把钱在两处「钱」之间挪动，两个端点都必须是钱，故本方法**就是**
    /// <see cref="IsMoneyAccount"/>——它只是该集合在「转账端点」这个语境下的名字。
    /// <para>
    /// 保留这个名字而不让端点直接调 <see cref="IsMoneyAccount"/>：端点的错误文案与校验读起来
    /// 是「转账的两端只能是…」，名字须与它说的那件事一致（同 <see cref="IsUserAssignable"/>
    /// 「规则挂在枚举旁而非端点里」的取舍）。
    /// </para>
    /// </remarks>
    public static bool IsTransferAccount(this AccountType type) => type.IsMoneyAccount();
}
