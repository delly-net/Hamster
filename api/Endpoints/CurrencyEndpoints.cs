using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 币种查询端点（任意已登录用户）：读取全局币种字典。
/// </summary>
/// <remarks>
/// **只有读取，没有写入**：维护币种需系统管理员身份，落在
/// <see cref="AdminCurrencyEndpoints"/>。读写分开而非在写端点上加 `RequireAdmin` 分支，
/// 是因为两者的受众与鉴权口径完全不同——记账表单与账户新建表单面向所有登录用户。
/// <para>
/// 本端点**不挂在账套下**：币种是全系统共用的字典，与 <see cref="AccountEndpoints"/> 不同，
/// 它既不读 <c>X-Account-Set-Id</c> 请求头，也不做账套校验。未选择账套的用户同样需要币种字典
/// 才能渲染表单，此时把它们挡在门外毫无道理。
/// </para>
/// </remarks>
public sealed class CurrencyEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.CURRENCY_GROUP)
            .WithTags("币种")
            .RequireAuthorization();

        group.MapGet("", async (
                ICurrencyService currencies,
                CancellationToken cancellationToken) =>
            {
                var active = await currencies.ListActiveAsync(cancellationToken);
                var fallbackDefault = await currencies.GetDefaultAsync(cancellationToken);

                return Results.Ok(active
                    .Select(currency => CurrencyDto.From(
                        currency,
                        // 以 GetDefaultAsync 的结果为准确认默认身份，而不是直接读各行自带的 isDefault：
                        // 默认币种若恰好被停用（见 GetDefaultAsync 的回退逻辑），此时真正生效的兜底值
                        // 是第一个启用币种，而前端拿到的 isDefault 必须与后端实际行为一致。
                        currency.Id == fallbackDefault?.Id))
                    .ToArray());
            })
            .WithName("ListCurrencies")
            .WithSummary("币种列表")
            .WithDescription(
                "返回全部**启用**的币种，按排序值与主键升序，供记账表单与账户新建表单的币种候选。" +
                "isDefault 标记系统默认币种（新建账户与交易币种的初值），**全表至多一个为 true**；" +
                "默认币种被停用时，回退到排序最靠前的启用币种，此时回退项会被标为 isDefault。" +
                "已停用的币种不返回——停用即不再接受新的绑定，但既有账户与流水照常可用。" +
                "维护币种（新增/改名/设为默认/停用）为管理员权限，见 /api/admin/currencies。");

        group.MapGet("/default", async (
                ICurrencyService currencies,
                CancellationToken cancellationToken) =>
            {
                var fallback = await currencies.GetDefaultAsync(cancellationToken);
                return fallback is null
                    ? Results.NotFound(new { message = "尚未配置任何币种" })
                    : Results.Ok(CurrencyDto.From(fallback, true));
            })
            .WithName("GetDefaultCurrency")
            .WithSummary("默认币种")
            .WithDescription(
                "返回系统默认币种，供表单初值使用。默认币种被停用或从未设置时回退到排序最靠前的启用币种；" +
                "一个币种都没有时返回 404。");
    }
}

/// <summary>币种（对外暴露）。</summary>
/// <param name="Id">币种主键。</param>
/// <param name="Code">ISO 4217 三字母代码，恒为大写（如 <c>CNY</c>）。</param>
/// <param name="Name">币种中文名（如「人民币」）。</param>
/// <param name="Symbol">币种符号（如 <c>¥</c>）；无符号时为 <c>null</c>。</param>
/// <param name="IsDefault">是否为系统默认币种。</param>
/// <param name="IsActive">是否启用；停用即软删除。</param>
/// <param name="SortOrder">呈现顺序，越小越靠前。</param>
/// <remarks>
/// **代码与名称都由后端下发，前端不维护第二份对照表**：币种可由管理员新增与改名，
/// 前端硬编码一份「CNY → 人民币」只会在管理员改名后与之漂移。
/// </remarks>
public sealed record CurrencyDto(
    int Id,
    string Code,
    string Name,
    string? Symbol,
    bool IsDefault,
    bool IsActive,
    int SortOrder)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="currency">币种实体。</param>
    /// <param name="isDefault">
    /// 是否为**实际生效**的默认币种。由调用方判定而非直接取 <see cref="Currency.IsDefault"/>：
    /// 默认币种被停用时会回退到排序最靠前的启用币种，此时生效的是回退项。
    /// </param>
    /// <returns>币种 DTO。</returns>
    public static CurrencyDto From(Currency currency, bool isDefault) => new(
        currency.Id,
        currency.Code,
        currency.Name,
        currency.Symbol,
        isDefault,
        currency.IsActive,
        currency.SortOrder);
}
