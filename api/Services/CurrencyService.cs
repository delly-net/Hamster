using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的币种业务实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
public sealed class CurrencyService(ISqlSugarClient db) : ICurrencyService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Currency>> ListActiveAsync(CancellationToken cancellationToken = default) =>
        await db.Queryable<Currency>()
            .Where(currency => currency.IsActive)
            .OrderBy(currency => currency.SortOrder)
            .OrderBy(currency => currency.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Currency>> ListAllAsync(CancellationToken cancellationToken = default) =>
        await db.Queryable<Currency>()
            .OrderBy(currency => currency.SortOrder)
            .OrderBy(currency => currency.Id)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Currency?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        var matched = await db.Queryable<Currency>()
            .Where(currency => currency.IsDefault && currency.IsActive)
            .OrderBy(currency => currency.Id)
            .Take(1)
            .ToListAsync(cancellationToken);

        if (matched.Count > 0)
        {
            return matched[0];
        }

        // 回退：默认币种未被设置（或被停用）时取第一个启用币种。
        // 直接复用 ListActiveAsync 的排序，不另写一份「第一个启用币种」的查询。
        var active = await ListActiveAsync(cancellationToken);
        return active.Count > 0 ? active[0] : null;
    }

    /// <inheritdoc />
    public async Task<bool> IsCodeTakenAsync(
        string code,
        int? excludeId = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code);
        if (normalized.Length == 0)
        {
            return false;
        }

        var matched = await db.Queryable<Currency>()
            .Where(currency => currency.Code.ToLower() == normalized)
            .WhereIF(excludeId is not null, currency => currency.Id != excludeId)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsActiveAsync(string? code, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(code ?? string.Empty);
        if (normalized.Length == 0)
        {
            return false;
        }

        var matched = await db.Queryable<Currency>()
            .Where(currency => currency.IsActive && currency.Code.ToLower() == normalized)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.Count > 0;
    }

    /// <inheritdoc />
    public async Task<Currency?> FindAsync(int id, CancellationToken cancellationToken = default)
    {
        var matched = await db.Queryable<Currency>()
            .Where(currency => currency.Id == id)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<Currency> CreateAsync(
        string code,
        string name,
        string? symbol,
        int sortOrder,
        CancellationToken cancellationToken = default)
    {
        var currency = new Currency
        {
            // 代码统一大写入库，查重则不区分大小写——两者合起来即「大小写不敏感且存法唯一」
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Symbol = NormalizeSymbol(symbol),
            IsActive = true,
            // 默认币种只能由 SetDefaultAsync 显式指定（见 ICurrencyService 的说明）
            IsDefault = false,
            SortOrder = sortOrder,
        };

        currency.Id = await db.Insertable(currency).ExecuteReturnIdentityAsync(cancellationToken);
        return currency;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(
        Currency currency,
        string name,
        string? symbol,
        int sortOrder,
        CancellationToken cancellationToken = default)
    {
        // 只更新可变的列：code 是币种身份，不在参数中（见 ICurrencyService.UpdateAsync 的说明）
        var affected = await db.Updateable<Currency>()
            .SetColumns(target => new Currency
            {
                Name = name.Trim(),
                Symbol = NormalizeSymbol(symbol),
                SortOrder = sortOrder,
            })
            .Where(target => target.Id == currency.Id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(
        Currency currency,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var affected = await db.Updateable<Currency>()
            .SetColumns(target => new Currency { IsActive = isActive })
            .Where(target => target.Id == currency.Id)
            .ExecuteCommandAsync(cancellationToken);

        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<bool> SetDefaultAsync(Currency currency, CancellationToken cancellationToken = default)
    {
        // 先清后置，两步同事务：任何中间态下「全表至多一个默认」都成立。
        // 反过来的顺序（先置本行再清其余行）会在中间态出现两个默认币种，
        // 此刻若有并发读，GetDefaultAsync 就可能取到另一个。
        await db.Ado.UseTranAsync(async () =>
        {
            await db.Updateable<Currency>()
                .SetColumns(target => target.IsDefault == false)
                .Where(target => target.IsDefault && target.Id != currency.Id)
                .ExecuteCommandAsync(cancellationToken);

            await db.Updateable<Currency>()
                .SetColumns(target => target.IsDefault == true)
                .Where(target => target.Id == currency.Id)
                .ExecuteCommandAsync(cancellationToken);
        });

        return true;
    }

    /// <summary>币种代码归一化：去空白并转小写，用于不区分大小写的查重与比对。</summary>
    /// <param name="code">原始代码。</param>
    /// <returns>归一化后的代码。</returns>
    private static string Normalize(string code) => code.Trim().ToLowerInvariant();

    /// <summary>符号归一化：去空白，空串收敛为 <c>null</c>。</summary>
    /// <param name="symbol">原始符号。</param>
    /// <returns>符号或 <c>null</c>。</returns>
    /// <remarks>
    /// 空串与 <c>null</c> 都表示「无符号」，统一收敛到 <c>null</c>：
    /// 两种写法并存会让「有无符号」的判定变成两次比较，且展示时仍需过滤空串。
    /// </remarks>
    private static string? NormalizeSymbol(string? symbol)
    {
        var trimmed = symbol?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
