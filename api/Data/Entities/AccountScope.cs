namespace Hamster.Api.Data.Entities;

/// <summary>
/// 账户的归属范围：决定该账户在账套内「谁能看见、谁能用」。
/// </summary>
/// <remarks>
/// 可见性规则（判定收敛在 <c>AccountService</c> 一处）：
/// <list type="bullet">
///   <item><see cref="Public"/>：账套内全部成员可见可用，用于家庭共用的账户。</item>
///   <item><see cref="Personal"/>：仅归属人（<see cref="Account.OwnerUserId"/>）可见可用，他人不可见。</item>
/// </list>
/// 系统管理员是唯一的例外：对任意存在的账套均可访问，且可见并可改账套内的全部账户（含他人个人账户）。
/// <para>
/// 显式赋值而非依赖声明顺序：该值直接落库，调整枚举顺序会把既有数据解释成另一种范围。
/// </para>
/// </remarks>
public enum AccountScope
{
    /// <summary>个人账户：仅归属人可见可用。</summary>
    Personal = 1,

    /// <summary>公共账户：账套内全部成员可见可用。</summary>
    Public = 2,
}
