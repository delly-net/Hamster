using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 分类端点（任意已登录用户）：在当前账套内查询、新建、改名分类，以及停用/启用。
/// </summary>
/// <remarks>
/// **分类一律挂在账套下**：每个端点先解析当前账套（请求头 <c>X-Account-Set-Id</c>），
/// 未指定账套即拒绝请求——分类字典是账套内的共有资产，无账套即无字典可操作。
/// 这一取舍与 <see cref="AccountEndpoints"/> 一致、与 <see cref="CurrencyEndpoints"/> 相反。
/// <para>
/// **读写不分开、也不要求管理员身份**：分类是账套内所有成员共用的字典，
/// 记账时手工输入即自动建分类是它的主用途——若把维护能力收到管理端，
/// 普通用户会面对「自己刚建的分类却无权改名」的局面。这与币种刻意不同
/// （币种是全局字典，改动会影响所有账套，故维护归管理员）。
/// </para>
/// <para>
/// **本层不做可见性判定**：分类没有归属人、也没有可见性维度，
/// 账套内的分类对所有成员一视同仁（见 <see cref="ICategoryService"/>）。
/// 唯一的防护是「一切查询都限定在当前账套内」——
/// 跨账套的主键与不存在的主键在这里得到同一个 404，不泄露存在性。
/// </para>
/// <para>
/// 删除为**软删除**：以 <c>/deactivate</c> 与 <c>/activate</c> 取代 DELETE，
/// 历史流水挂靠分类，物理删除会让既有明细的分类凭空消失。
/// </para>
/// </remarks>
public sealed class CategoryEndpoints : IEndpoint
{
    /// <summary>分类名称最大长度，与 <see cref="Category.Name"/> 的列长一致。</summary>
    private const int NAME_MAX_LENGTH = 32;

    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.CATEGORY_GROUP)
            .WithTags("分类")
            .RequireAuthorization();

        group.MapGet("", async (
                bool? includeInactive,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICategoryService categories,
                CancellationToken cancellationToken) =>
            {
                var (accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var list = await categories.ListByAccountSetAsync(
                    accountSet!.Id,
                    includeInactive ?? false,
                    cancellationToken);

                return Results.Ok(list.Select(CategoryDto.From).ToArray());
            })
            .WithName("ListCategories")
            .WithSummary("分类列表")
            .WithDescription(
                "返回**当前账套内**的分类，按主键升序（即建立先后）。默认只返回启用的分类，" +
                "includeInactive=true 时含已停用的（分类管理页用后者，记账表单用前者）。" +
                "分类**按账套隔离**：请求头 X-Account-Set-Id 决定看到的是哪一本字典，未携带时返回 400。" +
                "**不区分收入/支出/转账**：同一份字典三类记账共用，不给分类加类型维度——" +
                "「餐饮」在支出与转账下含义相同，强制二选一只会让用户为同一件事建两个分类。" +
                "分类**没有可见性维度**：账套内所有成员看到的是同一份完整列表，不存在他人私有的分类。");

        group.MapPost("", async (
                CategoryRequest request,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICategoryService categories,
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
                if (await categories.IsNameTakenAsync(accountSet!.Id, name, null, cancellationToken))
                {
                    return NameConflict();
                }

                var created = await categories.CreateAsync(accountSet.Id, name, cancellationToken);

                return Results.Created(
                    $"{ApiPathConst.CATEGORY_GROUP}/{created.Id}",
                    CategoryDto.From(created));
            })
            .WithName("CreateCategory")
            .WithSummary("新建分类")
            .WithDescription(
                "在当前账套内新建分类，名称**不区分大小写唯一**，重复返回 409。" +
                "新建的分类**一律启用**。名称 32 位以内，前后空白会被去掉后入库。" +
                "**记账端点并不需要先调本端点**：`POST /api/transactions` 传 categoryName 且未命中既有分类时" +
                "会自动创建，本端点是分类管理页用来「事先建好一份字典」的入口。");

        group.MapPut("/{id:int}", async (
                int id,
                CategoryUpdateRequest request,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICategoryService categories,
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

                var category = await categories.FindAsync(accountSet!.Id, id, cancellationToken);

                if (category is null)
                {
                    return NotFound();
                }

                var name = request.Name!.Trim();
                if (await categories.IsNameTakenAsync(accountSet.Id, name, id, cancellationToken))
                {
                    return NameConflict();
                }

                return await categories.UpdateAsync(category, name, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("UpdateCategory")
            .WithSummary("修改分类名称")
            .WithDescription(
                "修改分类名称，**本端点只改名称**（请求体因此只有 name）。所属账套一经创建不可修改：" +
                "改归属等于把这个分类从一家的字典搬到另一家，而挂在它上面的历史流水并不跟着搬家；" +
                "传 accountSetId 也不会被读取。" +
                "改名**不产生任何连带影响**：流水挂的是分类主键而非名称，改名后历史明细自动显示新名字。");

        group.MapPost("/{id:int}/deactivate", async (
                int id,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICategoryService categories,
                CancellationToken cancellationToken) =>
            {
                var (accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var category = await categories.FindAsync(accountSet!.Id, id, cancellationToken);

                if (category is null)
                {
                    return NotFound();
                }

                return await categories.SetActiveAsync(category, false, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("DeactivateCategory")
            .WithSummary("停用分类（软删除）")
            .WithDescription(
                "停用后不再出现在记账表单的分类候选中，可用 includeInactive=true 查看并重新启用；数据行保留。" +
                "**已挂该分类的历史流水照常显示其名称**：停用是「不再供新记账选择」，不是「历史上从未用过」。" +
                "**手工输入分类名时仍会命中已停用的分类**：用户把名字原样敲出来即归到它上面，" +
                "此时另建一个同名的只会让同一个名字在库里存在两行、把历史明细拆到两处。");

        group.MapPost("/{id:int}/activate", async (
                int id,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                ICategoryService categories,
                CancellationToken cancellationToken) =>
            {
                var (accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var category = await categories.FindAsync(accountSet!.Id, id, cancellationToken);

                if (category is null)
                {
                    return NotFound();
                }

                return await categories.SetActiveAsync(category, true, cancellationToken)
                    ? Results.NoContent()
                    : NotFound();
            })
            .WithName("ActivateCategory")
            .WithSummary("启用分类")
            .WithDescription("恢复此前停用的分类，使其重新出现在记账表单的分类候选中。");
    }

    /// <summary>分类不存在、或存在但不属于当前账套时的响应。</summary>
    /// <returns>404 响应。</returns>
    /// <remarks>
    /// 两种情形刻意给同一个响应：分类没有可见性维度，分不出「不存在」与「不归你这一账套」，
    /// 分开回复只会泄露「别处存在这么个主键」。
    /// </remarks>
    private static IResult NotFound() => Results.NotFound(new { message = "分类不存在" });

    /// <summary>分类名称在当前账套内已被占用时的响应。</summary>
    /// <returns>409 响应。</returns>
    /// <remarks>
    /// 用 409 而非 400：请求体本身合法，冲突的是**当前数据状态**（与账户重名同一口径）。
    /// 唯一性由服务层在写入前判定，不建数据库唯一索引——
    /// 「不区分大小写」在 Sqlite 与 PostgreSQL 上没有一致的表达方式（见 <see cref="Category"/>）。
    /// </remarks>
    private static IResult NameConflict() => Results.Conflict(new { message = "该账套内已存在同名分类" });

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
    /// 与 <see cref="AccountEndpoints"/> / <see cref="TransactionEndpoints"/> 的同名方法同构，
    /// 只少返回一个 <c>AccountSet</c> 之外的操作者：分类没有「谁的分类」这一维度，
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

    /// <summary>校验分类名称，把错误写入错误字典。</summary>
    /// <param name="name">原始名称。</param>
    /// <param name="errors">按字段聚合的错误字典。</param>
    private static void ValidateName(string? name, Dictionary<string, string[]> errors)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            errors["name"] = ["分类名称不能为空"];
        }
        else if (trimmed.Length > NAME_MAX_LENGTH)
        {
            errors["name"] = [$"分类名称不能超过 {NAME_MAX_LENGTH} 位"];
        }
    }
}

/// <summary>新建分类请求体。</summary>
/// <param name="Name">分类名称，必填，32 位以内；前后空白会被去掉后入库。</param>
public sealed record CategoryRequest(string? Name);

/// <summary>修改分类请求体。</summary>
/// <param name="Name">分类名称，必填，32 位以内。</param>
/// <remarks>
/// 刻意不含所属账套：归属一经创建不可修改（见更新端点的说明），
/// 字段不在这里，**请求里带上它也不会被读取**。
/// </remarks>
public sealed record CategoryUpdateRequest(string? Name);

/// <summary>分类（对外暴露）。</summary>
/// <param name="Id">分类主键。交易写入时传的 <c>categoryId</c> 即此值。</param>
/// <param name="AccountSetId">所属账套主键；分类按账套隔离，此值恒等于当前请求账套。</param>
/// <param name="Name">分类名称。</param>
/// <param name="IsActive">是否启用；<c>false</c> 表示已停用（软删除）。</param>
/// <param name="CreatedAt">创建时间（UTC，ISO 8601）。</param>
/// <remarks>
/// **不区分记账类型**：本 DTO 刻意没有 type 字段，同一份字典供收入/支出/转账三类共用。
/// <para>
/// 也**没有排序字段**：分类按主键（建立先后）呈现，理由见
/// <see cref="ICategoryService.ListByAccountSetAsync"/>。
/// </para>
/// </remarks>
public sealed record CategoryDto(
    int Id,
    int AccountSetId,
    string Name,
    bool IsActive,
    DateTime CreatedAt)
{
    /// <summary>由实体构造 DTO。</summary>
    /// <param name="category">分类实体。</param>
    /// <returns>分类 DTO。</returns>
    public static CategoryDto From(Category category) => new(
        category.Id,
        category.AccountSetId,
        category.Name,
        category.IsActive,
        // 从 Sqlite 读回的时间为 DateTimeKind.Unspecified，显式标记为 UTC，
        // 保证序列化输出带 Z 后缀、语义不产生歧义（与 AccountDto.From 一致）
        DateTime.SpecifyKind(category.CreatedAt, DateTimeKind.Utc));
}
