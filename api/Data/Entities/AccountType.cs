namespace Hamster.Api.Data.Entities;

/// <summary>
/// 账户类型：决定该账户在记账语义上代表什么。
/// </summary>
/// <remarks>
/// 四类取值由业务固定，不由用户扩展。显式赋值而非依赖声明顺序：该值直接落库，
/// 调整枚举顺序会把既有数据解释成另一种类型。
/// </remarks>
public enum AccountType
{
    /// <summary>账本账户：记账用的汇总性账户，本身不代表具体的钱。</summary>
    Ledger = 1,

    /// <summary>资金账户：实实在在的钱（现金、银行卡、电子钱包等）。</summary>
    Fund = 2,

    /// <summary>负债账户：欠别人的钱（信用卡、借款等），期初金额允许为负。</summary>
    Liability = 3,

    /// <summary>往来账户：人情往来与应收应付（借出、借入、待收报销等）。</summary>
    Contact = 4,
}
