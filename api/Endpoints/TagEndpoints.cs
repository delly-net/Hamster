using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 标签端点（任意已登录用户）：在当前账套内查询、新建、改名标签，以及停用/启用。
/// </summary>
/// <remarks>
/// **标签一律挂在账套下**：每个端点先解析当前账套（请求头 <c>X-Account-Set-Id</c>），
/// 未指定账套即拒绝请求——标签字典是账套内的共有资产，无账套即无字典可操作。
/// 这一取舍与 <see cref="CategoryEndpoints"/>、<see cref="AccountEndpoints"/> 一致、
/// 与 <see cref="CurrencyEndpoints"/> 相反。
/// <para>
/// **读写不分开、也不要求管理员身份**：标签是账套内所有成员共用的词汇表，
/// 记账时手工输入即自动建标签是它的主用途——若把维护能力收到管理端，
/// 普通用户会面对「自己刚建的标签却无权改名」的局面。这与币种刻意不同
/// （币种是全局字典，改动会影响所有账套，故维护归管理员）。
/// </para>
/// <para>
/// **本层不做可见性判定**：标签没有归属人、也没有可见性维度，
/// 账套内的标签对所有成员一视同仁（见 <see cref="ITagService"/>）。
/// 唯一的防护是「一切查询都限定在当前账套内」——
/// 跨账套的主键与不存在的主键在这里得到同一个 404，不泄露存在性。
/// </para>
/// <para>
/// **本层不提供「按交易查标签」或「给交易挂标签」的端点**：标签与交易的关联是**交易的一部分**，
/// 写入走 <c>POST/PUT /api/transactions</c> 的 <c>tagIds</c>/<c>tagNames</c> 字段
/// （与交易头、两条明细同一个事务），读取走 <c>GET /api/entries</c> 返回的 <c>tags</c>。
/// 单独开一个「给交易打标签」的端点会让交易与它的标签分两次写入，
/// 中间失败就留下「交易记下了标签却没挂上」的半成品。
/// </para>
/// <para>
/// 删除为**软删除**：以 <c>/deactivate</c> 与 <c>/activate</c> 取代 DELETE，
/// 历史流水挂靠标签，物理删除会让既有明细的标签凭空消失。
/// </para>
/// </remarks>
public sealed class TagEndpoints : IEndpoint
{
    /// <summary>标签名称最大长度，与 <see cref="Tag.Name"/> 的列长一致。</summary>
    private const int NAME_MAX_LENGTH = 32;

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.TAG_GROUP)
            .WithTags("标签")
            .RequireAuthorization();

        group.MapGet("", async (
                bool? includeInactive,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ITagService tags,
                CancellationToken cancellationToken) =>
            {
                var (accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var list = await tags.ListByAccountSetAsync(
                    accountSet!.Id,
                    includeInactive ?? false,
                    cancellationToken);

                return Results.Ok(list.Select(TagDto.From).ToArray());
            })
            .WithName("ListTags")
            .WithSummary("标签列表")
            .WithDescription(
                "返回**当前账套内**的标签，按主键升序（即建立先后）。默认只返回启用的标签，" +
                "includeInactive=true 时含已停用的（标签管理页与账目明细页的筛选区用后者，" +
                "记账表单用前者）。标签**按账套隔离**：请求头 X-Account-Set-Id 决定看到的是哪一本字典，" +
                "未携带时返回 400。" +
                "**不区分收入/支出/转账**：同一份字典三类记账共用，不给标签加类型维度。" +
                "标签**没有可见性维度**：账套内所有成员看到的是同一份完整列表，不存在他人私有的标签。" +
                "**一笔交易的标签不在这里**：本端点只回字典，交易挂了哪些标签见 GET /api/entries 的 tags 字段。");

        group.MapPost("", async (
                TagRequest request,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ITagService tags,
                CancellationToken cancellationToken) =>
            {
                var (accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var errors = new Dictionary<string, string[]>();
                ValidateName(request.Name, errors);

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var name = request.Name!.Trim();
                if (await tags.IsNameTakenAsync(accountSet!.Id, name, null, cancellationToken))
                {
                    return NameConflict();
                }

                var created = await tags.CreateAsync(accountSet.Id, name, cancellationToken);

                return Results.Created(
                    $"{ApiPathConst.TAG_GROUP}/{created.Id}",
                    TagDto.From(created));
            })
            .WithName("CreateTag")
            .WithSummary("新建标签")
            .WithDescription(
                "在当前账套内新建标签，名称**不区分大小写唯一**，重复返回 409。" +
                "新建的标签**一律启用**。名称 32 位以内，前后空白会被去掉后入库。" +
                "**记账端点并不需要先调本端点**：`POST /api/transactions` 传 tagNames 且未命中既有标签时" +
                "会自动创建，本端点是标签管理页用来「事先建好一份词汇表」的入口。");

        group.MapPut("/{id:int}", async (
                int id,
                TagUpdateRequest request,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ITagService tags,
                CancellationToken cancellationToken) =>
            {
                var (accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var errors = new Dictionary<string, string[]>();
                ValidateName(request.Name, errors);

                if (errors.Count > 0)
                {
                    return Results.ValidationProblem(errors);
                }

                var tag = await tags.FindAsync(accountSet!.Id, id, cancellationToken);

                if (tag is null)
                {
                    return NotFound();
                }

                var name = request.Name!.Trim();
                if (await tags.IsNameTakenAsync(accountSet.Id, name, id, cancellationToken))
                {
                    return NameConflict();
                }

                return await tags.UpdateAsync(tag, name, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("UpdateTag")
            .WithSummary("修改标签名称")
            .WithDescription(
                "修改标签名称，**本端点只改名称**（请求体因此只有 name）。所属账套一经创建不可修改：" +
                "改归属等于把这个标签从一家的词汇表搬到另一家，而挂着它的历史流水并不跟着搬家；" +
                "传 accountSetId 也不会被读取。" +
                "改名**不产生任何连带影响**：流水挂的是标签主键而非名称，改名后历史明细自动显示新名字。");

        group.MapPost("/{id:int}/deactivate", async (
                int id,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ITagService tags,
                CancellationToken cancellationToken) =>
            {
                var (accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var tag = await tags.FindAsync(accountSet!.Id, id, cancellationToken);

                if (tag is null)
                {
                    return NotFound();
                }

                return await tags.SetActiveAsync(tag, false, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("DeactivateTag")
            .WithSummary("停用标签（软删除）")
            .WithDescription(
                "停用后不再出现在记账表单与账目明细页筛选区的标签候选中，可用 includeInactive=true " +
                "查看并重新启用；数据行保留。" +
                "**已挂该标签的历史流水照常在明细页显示其名称**：停用是「不再供新记账选择」，" +
                "不是「历史上从未用过」。" +
                "**手工输入标签名时仍会命中已停用的标签**：用户把名字原样敲出来即归到它上面，" +
                "此时另建一个同名的只会让同一个名字在库里存在两行、把历史明细拆到两处。");

        group.MapPost("/{id:int}/activate", async (
                int id,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ITagService tags,
                CancellationToken cancellationToken) =>
            {
                var (accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var tag = await tags.FindAsync(accountSet!.Id, id, cancellationToken);

                if (tag is null)
                {
                    return NotFound();
                }

                return await tags.SetActiveAsync(tag, true, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("ActivateTag")
            .WithSummary("启用标签")
            .WithDescription("恢复此前停用的标签，使其重新出现在记账表单与账目明细页筛选区的标签候选中。");
    }

    /// <summary>标签不存在、或存在但不属于当前账套时的响应。</summary>
    /// <returns>404 响应。</returns>
    /// <remarks>
    /// 两种情形刻意给同一个响应：标签没有可见性维度，分不出「不存在」与「不归你这一账套」，
    /// 分开回复只会泄露「别处存在这么个主键」。
    /// </remarks>
    private static IResult NotFound() => Results.NotFound(new { message = "标签不存在" });

    /// <summary>标签名称在当前账套内已被占用时的响应。</summary>
    /// <returns>409 响应。</returns>
    /// <remarks>
    /// 用 409 而非 400：请求体本身合法，冲突的是**当前数据状态**（与账户、分类重名同一口径）。
    /// 唯一性由服务层在写入前判定，不建数据库唯一索引——
    /// 「不区分大小写」在 Sqlite 与 PostgreSQL 上没有一致的表达方式（见 <see cref="Tag"/>）。
    /// </remarks>
    private static IResult NameConflict() => Results.Conflict(new { message = "该账套内已存在同名标签" });

    /// <summary>
    /// 解析本次请求的「操作者 + 当前账套」。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="principal">当前请求的用户主体。</param>
    /// <param name="users">用户服务。</param>
    /// <param name="accountSets">账套服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部通过时失败响应为 <c>null</c>；否则账套为 <c>null</c> 并给出失败响应。</returns>
    /// <remarks>
    /// 与 <see cref="CategoryEndpoints"/> 的同名方法同构，只少返回一个操作者：标签没有「谁的标签」这一维度，
    /// 可见性判定与归属判定都不需要操作者，故不取它——**刻意不预留一个用不上的参数**。
    /// <para>
    /// 账套未携带请求头时 <c>ResolveCurrentAccountSetAsync</c> 返回的是 <c>(null, null)</c>——
    /// **这不是失败而是「无账套」**，此处显式转成 400：无账套即无字典可操作。
    /// </para>
    /// </remarks>
    private static async Task<(AccountSet? AccountSet, IResult? Failure)> ResolveContextAsync(
        HttpContext context,
        ClaimsPrincipal principal,
        IUserService users,
        IAccountSetService accountSets,
        CancellationToken cancellationToken)
    {
        var (accountSet, failure) = await context.ResolveCurrentAccountSetAsync(
            users,
            accountSets,
            cancellationToken);

        if (failure is not null)
        {
            return (null, failure);
        }

        if (accountSet is null)
        {
            return (null, Results.BadRequest(new { message = "请先选择账套" }));
        }

        if (principal.GetUserId() is null)
        {
            return (null, Results.Unauthorized());
        }

        return (accountSet, null);
    }

    /// <summary>校验标签名称，把错误写入错误字典。</summary>
    /// <param name="name">原始名称。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    private static void ValidateName(string? name, Dictionary<string, string[]> errors)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            errors["name"] = ["标签名称不能为空"];
        }
        else if (trimmed.Length > NAME_MAX_LENGTH)
        {
            errors["name"] = [$"标签名称不能超过 {NAME_MAX_LENGTH} 位"];
        }
    }
}

/// <summary>新建标签请求体。</summary>
/// <param name="Name">标签名称，必填，32 位以内；前后空白会被去掉后入库。</param>
public sealed record TagRequest(string? Name);

/// <summary>修改标签请求体。</summary>
/// <param name="Name">标签名称，必填，32 位以内。</param>
/// <remarks>
/// 刻意不含所属账套：归属一经创建不可修改（见更新端点的说明），
/// 字段不在这里，**请求里带上它也不会被读取**。
/// </remarks>
public sealed record TagUpdateRequest(string? Name);

/// <summary>标签（对外暴露）。</summary>
/// <param name="Id">标签主键。交易写入时传的 <c>tagIds</c> 即此值。</param>
/// <param name="AccountSetId">所属账套主键；标签按账套隔离，此值恒等于当前请求账套。</param>
/// <param name="Name">标签名称。</param>
/// <param name="IsActive">是否启用；<c>false</c> 表示已停用（软删除）。</param>
/// <param name="CreatedAt">创建时间（UTC，ISO 8601）。</param>
/// <remarks>
/// **不区分记账类型**：本 DTO 刻意没有 type 字段，同一份字典供收入/支出/转账三类共用。
/// <para>
/// 也**没有排序字段**：标签按主键（建立先后）呈现，理由见
/// <see cref="ITagService.ListByAccountSetAsync"/>。
/// </para>
/// <para>
/// 与 <see cref="CategoryDto"/> 逐字同构（只有名称与文案不同）——本 DTO 描述的是**字典里的一条标签**，
/// 「某笔交易挂了哪些标签」是另一件事，见 <c>EntryDto.Tags</c>。
/// </para>
/// </remarks>
public sealed record TagDto(
    int Id,
    int AccountSetId,
    string Name,
    bool IsActive,
    DateTime CreatedAt)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="tag">标签实体。</param>
    /// <returns>标签 DTO。</returns>
    public static TagDto From(Tag tag) => new(
        tag.Id,
        tag.AccountSetId,
        tag.Name,
        tag.IsActive,
        // 从 Sqlite 读回的时间为 DateTimeKind.Unspecified，显式标记为 UTC，
        // 保证序列化输出带 Z 后缀、语义不产生歧义（与 CategoryDto.From 一致）
        DateTime.SpecifyKind(tag.CreatedAt, DateTimeKind.Utc));
}
