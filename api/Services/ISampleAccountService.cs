using Hamster.Api.Data.Entities;

namespace Hamster.Api.Services;

/// <summary>
/// 【示例服务】账户业务服务约定，演示业务层与数据层的分层方式。
/// </summary>
public interface ISampleAccountService
{
    /// <summary>查询全部账户。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>账户列表。</returns>
    Task<IReadOnlyList<SampleAccount>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>创建账户。</summary>
    /// <param name="name">账户名称。</param>
    /// <param name="balance">初始余额。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的账户（含自增主键）。</returns>
    Task<SampleAccount> CreateAsync(string name, decimal balance, CancellationToken cancellationToken = default);
}
