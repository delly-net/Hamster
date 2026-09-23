using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 管理员币种管理端点：币种列表（含停用）、新建、改名/改符号/改排序、设为默认，以及停用/启用。
/// </summary>
/// <remarks>
/// 管理员身份校验统一走 <see cref="AdminGuard"/>（以数据库为准，不信任令牌声明）。
/// <para>
/// **币种不挂在账套下**：它是全系统共用的字典，故本组端点既不读 <c>X-Account-Set-Id</c> 请求头，
/// 也不复用 <c>ResolveContextAsync</c> 那套「操作者 + 当前账套」的解析——这里只需要操作者。
/// </para>
/// <para>
/// 删除为**软删除**：以 <c>/deactivate</c> 与 <c>/activate</c> 取代 DELETE。
/// 账户会绑定币种，物理删除会让既有账户指向不存在的币种、历史金额失去计价单位。
/// </para>
/// </remarks>
public sealed class AdminCurrencyEndpoints : IEndpoint
{
    /// <summary>币种代码最大长度，与 <c>Currency.Code</c> 的列长一致。</summary>
    private const int CODE_MAX_LENGTH = 8;

    /// <summary>币种名称最大长度，与 <c>Currency.Name</c> 的列长一致。</summary>
    private const int NAME_MAX_LENGTH = 32;

    /// <summary>币种符号最大长度，与 <c>Currency.Symbol</c> 的列长一致。</summary>
    private const int SYMBOL_MAX_LENGTH = 8;

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.ADMIN_CURRENCIES_GROUP)
            .WithTags("币种管理")
            .RequireAuthorization();

        group.MapGet("", async (
                ClaimsPrincipal principal,
                IUserService users,
                ICurrencyService currencies,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var all = await currencies.ListAllAsync(cancellationToken);
                var effectiveDefault = await currencies.GetDefaultAsync(cancellationToken);

                return Results.Ok(all
                    .Select(currency => CurrencyDto.From(currency, currency.Id == effectiveDefault?.Id))
                    .ToArray());
            })
            .WithName("ListAllCurrencies")
            .WithSummary("币种列表（含停用）")
            .WithDescription("返回全部币种及其默认/启用状态，供管理员在币种管理页操作。");

        group.MapPost("", async (
                CurrencyRequest request,
                ClaimsPrincipal principal,
                IUserService users,
                ICurrencyService currencies,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var errors = Validate(request);
                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var code = NormalizeCode(request.Code!);
                if (await currencies.IsCodeTakenAsync(code, null, cancellationToken))
                {
                    return Results.Conflict(new { message = "该币种代码已存在" });
                }

                var created = await currencies.CreateAsync(
                    code,
                    request.Name!.Trim(),
                    request.Symbol,
                    request.SortOrder,
                    cancellationToken);

                return Results.Created(
                    $"{ApiPathConst.ADMIN_CURRENCIES_GROUP}/{created.Id}",
                    CurrencyDto.From(created, false));
            })
            .WithName("CreateCurrency")
            .WithSummary("新建币种")
            .WithDescription(
                $"币种代码必填且全局唯一（{CODE_MAX_LENGTH} 位以内、仅字母、不区分大小写，入库统一大写），" +
                $"名称必填（{NAME_MAX_LENGTH} 位以内），符号可省略（{SYMBOL_MAX_LENGTH} 位以内）。" +
                "新建的币种**一律启用、且不是默认币种**——默认币种由「设为默认」显式指定。");

        group.MapPut("/{id:int}", async (
                int id,
                CurrencyRequest request,
                ClaimsPrincipal principal,
                IUserService users,
                ICurrencyService currencies,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var currency = await currencies.FindAsync(id, cancellationToken);
                if (currency is null)
                {
                    return NotFound();
                }

                var errors = Validate(request, requireCode: false);
                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                return await currencies.UpdateAsync(
                    currency,
                    request.Name!.Trim(),
                    request.Symbol,
                    request.SortOrder,
                    cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("UpdateCurrency")
            .WithSummary("修改币种")
            .WithDescription(
                "修改币种的名称、符号与排序。**代码不可修改**——它是币种的身份，账户按代码绑定币种，" +
                "中途改代码等于让所有已绑定的账户指向另一个币种；请求体里的 code 不会被读取。" +
                "需要更正代码时应停用后重新创建。");

        group.MapPost("/{id:int}/activate", (int id, ClaimsPrincipal principal, IUserService users, ICurrencyService currencies, CancellationToken cancellationToken) =>
                SetActiveAsync(id, true, principal, users, currencies, cancellationToken))
            .WithName("ActivateCurrency")
            .WithSummary("启用币种")
            .WithDescription("把已停用的币种恢复为可用，恢复后重新出现在记账与账户表单的币种候选中。");

        group.MapPost("/{id:int}/deactivate", (int id, ClaimsPrincipal principal, IUserService users, ICurrencyService currencies, CancellationToken cancellationToken) =>
                SetActiveAsync(id, false, principal, users, currencies, cancellationToken))
            .WithName("DeactivateCurrency")
            .WithSummary("停用币种")
            .WithDescription(
                "停用币种（软删除）：停用后不再接受新的绑定（新建账户与记账都不再能选它），" +
                "但既有账户与流水照常可用——否则历史金额会失去计价单位。" +
                "**默认币种不可停用**：它是新建账户与历史回填的兜底取值，停用它会让这两条路径同时失去可用值；" +
                "确需停用请先把另一个币种设为默认。");

        group.MapPost("/{id:int}/set-default", async (
                int id,
                ClaimsPrincipal principal,
                IUserService users,
                ICurrencyService currencies,
                CancellationToken cancellationToken) =>
            {
                var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
                if (failure is not null)
                {
                    return failure;
                }

                var currency = await currencies.FindAsync(id, cancellationToken);
                if (currency is null)
                {
                    return NotFound();
                }

                if (!currency.IsActive)
                {
                    return Results.BadRequest(new { message = "已停用的币种不能设为默认币种，请先启用它" });
                }

                await currencies.SetDefaultAsync(currency, cancellationToken);
                return Results.NoContent();
            })
            .WithName("SetDefaultCurrency")
            .WithSummary("设为默认币种")
            .WithDescription(
                "把该币种设为系统默认币种。默认币种是新建账户与交易币种的初值，" +
                "**全系统至多一个**：设置时会在同一事务内先清除其余币种的默认标记。");
    }

    /// <summary>启用或停用币种。</summary>
    /// <param name="id">币种主键。</param>
    /// <param name="isActive">目标状态。</param>
    /// <param name="principal">当前请求的用户主体。</param>
    /// <param name="users">用户服务。</param>
    /// <param name="currencies">币种服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功返回 204；币种不存在返回 404；停用默认币种返回 400。</returns>
    /// <remarks>
    /// 抽出为方法而非把两段各写一遍：启用与停用的差异只有目标状态与那条默认币种校验，
    /// 分头写会得到两份近乎相同、却各自可能漏改的鉴权与查库代码。
    /// </remarks>
    private static async Task<IResult> SetActiveAsync(
        int id,
        bool isActive,
        ClaimsPrincipal principal,
        IUserService users,
        ICurrencyService currencies,
        CancellationToken cancellationToken)
    {
        var (_, failure) = await AdminGuard.ResolveAdminAsync(principal, users, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        var currency = await currencies.FindAsync(id, cancellationToken);
        if (currency is null)
        {
            return NotFound();
        }

        // 停用默认币种会让「新建账户的兜底币种」与「历史账户回填的币种」同时失去可用值，
        // 故直接拒绝，并把出路写进提示——比允许后让下游各自兜底要清楚得多
        if (!isActive && currency.IsDefault)
        {
            return Results.BadRequest(new { message = "默认币种不能停用，请先把另一个币种设为默认币种" });
        }

        return await currencies.SetActiveAsync(currency, isActive, cancellationToken)
            ? Results.NoContent()
            : NotFound();
    }

    /// <summary>币种不存在时的响应。</summary>
    /// <returns>404 响应。</returns>
    private static IResult NotFound() => Results.NotFound(new { message = "币种不存在" });

    /// <summary>校验币种请求体，返回按字段聚合的错误信息。</summary>
    /// <param name="request">请求体。</param>
    /// <param name="requireCode">是否要求校验代码；修改路径上传 <c>false</c>（代码不可改，传了也不读）。</param>
    /// <returns>错误字典；无错误时为空。</returns>
    private static Dictionary<string, string[]> Validate(CurrencyRequest request, bool requireCode = true)
    {
        var errors = new Dictionary<string, string[]>();

        if (requireCode)
        {
            var code = NormalizeCode(request.Code);
            if (code.Length == 0)
            {
                errors["code"] = ["币种代码不能为空"];
            }
            else if (code.Length > CODE_MAX_LENGTH)
            {
                errors["code"] = [$"币种代码不能超过 {CODE_MAX_LENGTH} 位"];
            }
            else if (!code.All(char.IsAsciiLetter))
            {
                // 限定纯字母而非「字母数字混合」：ISO 4217 的代码恒为三字母，
                // 放开数字只会让「CNY」与「CN1」这类无法互换的代码混进同一份字典
                errors["code"] = ["币种代码只能是字母"];
            }
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            errors["name"] = ["币种名称不能为空"];
        }
        else if (name.Length > NAME_MAX_LENGTH)
        {
            errors["name"] = [$"币种名称不能超过 {NAME_MAX_LENGTH} 位"];
        }

        if (request.Symbol?.Trim().Length > SYMBOL_MAX_LENGTH)
        {
            errors["symbol"] = [$"币种符号不能超过 {SYMBOL_MAX_LENGTH} 位"];
        }

        return errors;
    }

    /// <summary>币种代码归一化：去空白并转大写。</summary>
    /// <param name="code">原始代码。</param>
    /// <returns>归一化后的代码；为空时返回空串。</returns>
    /// <remarks>
    /// 归一化在**校验之前**做（长度与字符判定都针对大写结果）：否则「cny 」这类带空白的输入
    /// 会以原样参与长度判定，随后才被裁成「CNY」入库，校验与落库的对象不是同一个值。
    /// </remarks>
    private static string NormalizeCode(string? code) => code?.Trim().ToUpperInvariant() ?? string.Empty;
}

/// <summary>新建 / 修改币种请求体。</summary>
/// <param name="Code">
/// ISO 4217 三字母代码。**仅新建时读取**——代码不可修改，修改路径传了也不会被读取
/// （与账户类型同一取舍：让「不可改」成为事实，而非依赖调用方自觉）。
/// </param>
/// <param name="Name">币种中文名。</param>
/// <param name="Symbol">币种符号，可省略。</param>
/// <param name="SortOrder">呈现顺序，越小越靠前。</param>
public sealed record CurrencyRequest(string? Code, string? Name, string? Symbol, int SortOrder);
