using System.Security.Claims;
using Hamster.Api.Constant;
using Hamster.Api.Data.Entities;
using Hamster.Api.Services;

namespace Hamster.Api.Endpoints;

/// <summary>
/// 个人账套配置端点（任意已登录用户）：读写「我在当前账套里」的个人界面设置。
/// </summary>
/// <remarks>
/// **配置是按人存的**：同一本账套里的每个成员都有自己的一份，互不影响。
/// 这一点与账户、分类、标签等「账套内共用」的资源**刚好相反**，故本分组的端点一律
/// 既读账套（请求头 <c>X-Account-Set-Id</c>）又取当前用户主键，两者缺一不可：
/// 少了账套会读到「我在别的账套里的设置」，少了用户则会读到「别人的设置」。
/// <para>
/// **目前只有账目明细页的筛选条件**（路径 <c>/entry-filter</c>）。路径里带上「哪一页的设置」，
/// 而不是把不同页面的设置挤进一个通用键值对里——通用键值对会让字段名变成运行期字符串、
/// 类型校验与默认值全部消失，一处拼错要到用户反馈时才发现。
/// </para>
/// <para>
/// **不要求管理员身份**：这些设置描述的是「我习惯怎么看账」，与权限无关
/// （可见性借的是账户服务里那份判定，管理员看到的账户更多，能存的账户也就更多，如此而已）。
/// </para>
/// <para>
/// **没有 DELETE**：清空筛选条件是一个正常操作，其表达就是「存一份空条件」（PUT 空数组），
/// 一行的价值在于「我下次进来想看什么」，删掉这一行与存一份默认条件在行为上完全等价。
/// </para>
/// </remarks>
public sealed class AccountSetPreferenceEndpoints : IEndpoint
{
    /// <inheritdoc />
    public void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(ApiPathConst.ACCOUNT_SET_PREFERENCE_GROUP)
            .WithTags("个人账套配置")
            .RequireAuthorization();

        group.MapGet("/entry-filter", async (
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IAccountSetPreferenceService preferences,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                var saved = await preferences.GetEntryFilterAsync(
                    accountSet!.Id,
                    actor!.Id,
                    cancellationToken);

                return Results.Ok(EntryFilterPreferenceDto.From(saved));
            })
            .WithName("GetEntryFilterPreference")
            .WithSummary("读取账目明细页筛选条件")
            .WithDescription(
                "返回当前用户在当前账套里保存的账目明细页筛选条件（账户与标签主键）。" +
                "**从未保存过时返回两个空数组**——调用方据此回落到默认视图（账户全选、标签不限），" +
                "「没保存过」与「保存了一份空条件」不作区分。" +
                "返回值**不按当前候选收敛**：库里存的是什么就回什么（收敛只发生在写入侧）；" +
                "**日期区间不在其中**：本配置只记账户与标签，时间区间每次仍回到默认值。" +
                "未携带账套请求头返回 400，未登录返回 401。");

        group.MapPut("/entry-filter", async (
                EntryFilterPreferenceRequest request,
                HttpContext context,
                ClaimsPrincipal principal,
                IUserService users,
                IAccountSetService accountSets,
                IAccountSetPreferenceService preferences,
                CancellationToken cancellationToken) =>
            {
                var (actor, accountSet, failure) = await ResolveContextAsync(
                    context, principal, users, accountSets, cancellationToken);

                if (failure is not null)
                {
                    return failure;
                }

                await preferences.SaveEntryFilterAsync(
                    accountSet!.Id,
                    actor!.Id,
                    actor.IsAdmin,
                    request.AccountIds,
                    request.TagIds,
                    cancellationToken);

                return Results.NoContent();
            })
            .WithName("SaveEntryFilterPreference")
            .WithSummary("保存账目明细页筛选条件")
            .WithDescription(
                "保存当前用户在当前账套里的账目明细页筛选条件，**不存在则新建、存在则原地改写**" +
                "（按「账套 + 用户」唯一，故重复保存不会追加出第二行）。" +
                "**不属于当前账套的主键、以及对本用户不可见的账户会被静默丢弃**，不报错：" +
                "否则这个接口就成了探测「某账户/标签是否属于他人账套」的探针（同 GET /api/entries）。" +
                "请求体里省略或传空数组即存为空（「没筛账户」/「不限标签」）。" +
                "**日期区间不在这里**：本配置只记账户与标签。成功返回 204。" +
                "未携带账套请求头返回 400，未登录返回 401。");

        // 刻意**不提供**「清空配置」的 DELETE：见类头注释。
    }

    /// <summary>
    /// 解析本次请求的「操作者 + 当前账套」。
    /// </summary>
    /// <param name="context">当前 HTTP 上下文。</param>
    /// <param name="principal">当前请求的用户主体。</param>
    /// <param name="users">用户服务。</param>
    /// <param name="accountSets">账套服务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部通过时失败响应为 <c>null</c>；否则操作者与账套为 <c>null</c> 并给出失败响应。</returns>
    /// <remarks>
    /// 与 <see cref="EntryEndpoints"/> 的同名方法同构。用户身份以数据库为准（不信任令牌中的声明）——
    /// 本分组的写入要按用户主键隔离，取到的必须是**真实存在**的那个用户，
    /// 否则一次「令牌里有个已删除用户」的请求会往配置表里写下一行无人认领的设置。
    /// <para>
    /// 账套未携带请求头时 <c>ResolveCurrentAccountSetAsync</c> 返回 <c>(null, null)</c>——
    /// **这不是失败而是「无账套」**，此处显式转成 400：配置必然落在某本账套内，无账套即无处可存。
    /// </para>
    /// </remarks>
    private static async Task<(User? Actor, AccountSet? AccountSet, IResult? Failure)> ResolveContextAsync(
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
            return (null, null, failure);
        }

        if (accountSet is null)
        {
            return (null, null, Results.BadRequest(new { message = "请先选择账套" }));
        }

        var userId = principal.GetUserId();
        if (userId is null)
        {
            return (null, null, Results.Unauthorized());
        }

        var actor = await users.FindByIdAsync(userId.Value, cancellationToken);
        if (actor is null)
        {
            return (null, null, Results.Unauthorized());
        }

        return (actor, accountSet, null);
    }
}

/// <summary>保存账目明细页筛选条件的请求体。</summary>
/// <param name="AccountIds">
/// 选中的账户主键；省略或空数组表示「没筛账户」（页面会回落到全选）。
/// **不属于当前账套的主键、以及对本用户不可见的账户会被静默丢弃**。
/// </param>
/// <param name="TagIds">选中的标签主键；省略或空数组表示不限标签。</param>
/// <remarks>
/// 刻意**不含日期区间**：本配置只记账户与标签，时间区间每次进入页面仍回到默认的「本月 1 日~今天」。
/// 字段不在这里，**请求里带上它也不会被读取**（同 <see cref="TagUpdateRequest"/> 不含账套的做法）。
/// </remarks>
public sealed record EntryFilterPreferenceRequest(
    IReadOnlyCollection<int>? AccountIds,
    IReadOnlyCollection<int>? TagIds);

/// <summary>账目明细页筛选条件（对外暴露）。</summary>
/// <param name="AccountIds">选中的账户主键，按保存时的顺序；空表示没有保存过。</param>
/// <param name="TagIds">选中的标签主键，按保存时的顺序；空表示不限标签。</param>
/// <remarks>
/// **两个数组恒非 <c>null</c>**（没有保存过时是空数组而不是 null）：调用方据此直接遍历，
/// 不必先判空——「没有保存过」与「保存了空集合」的处置本来也完全相同。
/// <para>
/// 与其它 DTO 不同，本 DTO **不带任何时间戳**：配置的创建/修改时间对界面没有任何用处，
/// 带出去只会让前端多出两个没人看的字段。
/// </para>
/// </remarks>
public sealed record EntryFilterPreferenceDto(int[] AccountIds, int[] TagIds)
{
    /// <summary>由服务层结果构造 DTO。</summary>
    /// <param name="preference">服务层返回的筛选条件。</param>
    /// <returns>筛选条件 DTO。</returns>
    public static EntryFilterPreferenceDto From(EntryFilterPreference preference) => new(
        [.. preference.AccountIds],
        [.. preference.TagIds]);
}
