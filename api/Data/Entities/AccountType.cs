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
    /// 该类型是否可作为一笔转账的转出账户或转入账户。
    /// </summary>
    /// <param name="type">账户类型。</param>
    /// <returns>可作为转账端点返回 <c>true</c>。</returns>
    /// <remarks>
    /// 只有 <see cref="AccountType.Fund"/>（资金账户）与 <see cref="AccountType.Liability"/>（负债账户）
    /// 返回 <c>true</c>：这两类才是「钱本身」——现金、银行卡、电子钱包，以及信用卡、借款。
    /// 转账的语义就是把钱在两处「钱」之间挪动，两个端点都必须是钱。
    /// <para>
    /// <see cref="AccountType.Contact"/> 返回 <c>false</c>：往来账户是人情往来与应收应付，
    /// 它记录的是「谁欠谁」而不是「钱放在哪」，钱转进转出它并不改变钱的所在。
    /// <see cref="AccountType.Ledger"/> 返回 <c>false</c>：它是系统内部账户，对任何用户不呈现，
    /// 也不接受手工指定（见 <see cref="IsUserAssignable"/>）。
    /// </para>
    /// <para>
    /// 与 <see cref="IsUserAssignable"/> 是同一取舍：规则挂在枚举旁而非端点里，因为它是
    /// **类型的固有属性**（能不能作为一笔转账的端点），与「谁来校验」无关。端点的校验与错误文案
    /// 均由此派生，新增枚举取值时不会漏掉落校验，也不会出现「文案说可选、代码其实拒绝」的漂移。
    /// </para>
    /// </remarks>
    public static bool IsTransferAccount(this AccountType type) =>
        type is AccountType.Fund or AccountType.Liability;
}
