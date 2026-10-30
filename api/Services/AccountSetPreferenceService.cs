using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的个人账套配置实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <param name="accounts">
/// 账户服务：本服务只借用它的**可见性判定**与钱账户口径，不写任何账户数据。
/// 依赖方向不成环——<see cref="AccountService"/> 只依赖 <see cref="ITransactionService"/>，
/// 不会回头依赖本服务。
/// </param>
/// <param name="tags">
/// 标签服务：本服务只借用它的**账套内字典**（同样只读）。理由与 <paramref name="accounts"/> 一致：
/// 收敛用的判据必须与页面候选同源。
/// </param>
/// <remarks>
/// 同 <see cref="EntryQueryService"/> 的做法：可见性、以及「哪些账户算钱账户」这两件事
/// **一律向既有服务借**，本实现里不出现第二份判据。
/// </remarks>
public sealed class AccountSetPreferenceService(
    ISqlSugarClient db,
    IAccountService accounts,
    ITagService tags) : IAccountSetPreferenceService
{
    /// <summary>主键串的分隔符。</summary>
    private const char ID_SEPARATOR = ',';

    /// <inheritdoc />
    public async Task<EntryFilterPreference> GetEntryFilterAsync(
        int accountSetId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var row = await FindRowAsync(accountSetId, userId, cancellationToken);

        // 查无此行时返回两个空集合，而不是 null 或抛异常：
        // 「没保存过」是首次使用该页面的正常状态，调用方据此回落到默认视图即可
        // （与「保存了一份空条件」不作区分，理由见接口注释）。
        return row is null
            ? new EntryFilterPreference([], [])
            : new EntryFilterPreference(
                ParseIds(row.EntryAccountIds),
                ParseIds(row.EntryTagIds));
    }

    /// <inheritdoc />
    public async Task SaveEntryFilterAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        IReadOnlyCollection<int>? accountIds,
        IReadOnlyCollection<int>? tagIds,
        CancellationToken cancellationToken = default)
    {
        var (accountText, tagText) = await ConvergeAsync(
            accountSetId, userId, isAdmin, accountIds, tagIds, cancellationToken);

        var existing = await FindRowAsync(accountSetId, userId, cancellationToken);

        if (existing is null)
        {
            await db.Insertable(new AccountSetPreference
            {
                AccountSetId = accountSetId,
                UserId = userId,
                EntryAccountIds = accountText,
                EntryTagIds = tagText,
            }).ExecuteCommandAsync(cancellationToken);

            return;
        }

        // 只改三列：账套与归属人构成这一行的身份（唯一索引就是它们俩），不在改动之列；
        // created_at 记的是「这份配置什么时候开始存在」，改写内容不该把它抹成刚才。
        await db.Updateable<AccountSetPreference>()
            .SetColumns(target => new AccountSetPreference
            {
                EntryAccountIds = accountText,
                EntryTagIds = tagText,
                UpdatedAt = DateTime.UtcNow,
            })
            .Where(target => target.Id == existing.Id)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReportFilterPreference> GetDailyReportFilterAsync(
        int accountSetId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var row = await FindRowAsync(accountSetId, userId, cancellationToken);

        // 新列在升级库上是 NULL，ParseIds 把 null 与空串一并当成「没有保存过」——
        // 这正是「从未用过日报表」与「保存了一份空条件」的共同行为（账户全选、不限标签）。
        return row is null
            ? new ReportFilterPreference([], [])
            : new ReportFilterPreference(
                ParseIds(row.DailyReportAccountIds),
                ParseIds(row.DailyReportTagIds));
    }

    /// <inheritdoc />
    public async Task SaveDailyReportFilterAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        IReadOnlyCollection<int>? accountIds,
        IReadOnlyCollection<int>? tagIds,
        CancellationToken cancellationToken = default)
    {
        var (accountText, tagText) = await ConvergeAsync(
            accountSetId, userId, isAdmin, accountIds, tagIds, cancellationToken);

        var existing = await FindRowAsync(accountSetId, userId, cancellationToken);

        if (existing is null)
        {
            await db.Insertable(new AccountSetPreference
            {
                AccountSetId = accountSetId,
                UserId = userId,
                DailyReportAccountIds = accountText,
                DailyReportTagIds = tagText,
            }).ExecuteCommandAsync(cancellationToken);

            return;
        }

        // 只动日报表那一组列：同一行里的账目明细与月报表条件是别人的页面在用的，改写它们
        // 会让「在日报表点一次查询」把另外两页的视图一并改掉。
        await db.Updateable<AccountSetPreference>()
            .SetColumns(target => new AccountSetPreference
            {
                DailyReportAccountIds = accountText,
                DailyReportTagIds = tagText,
                UpdatedAt = DateTime.UtcNow,
            })
            .Where(target => target.Id == existing.Id)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ReportFilterPreference> GetMonthlyReportFilterAsync(
        int accountSetId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        var row = await FindRowAsync(accountSetId, userId, cancellationToken);

        return row is null
            ? new ReportFilterPreference([], [])
            : new ReportFilterPreference(
                ParseIds(row.MonthlyReportAccountIds),
                ParseIds(row.MonthlyReportTagIds));
    }

    /// <inheritdoc />
    public async Task SaveMonthlyReportFilterAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        IReadOnlyCollection<int>? accountIds,
        IReadOnlyCollection<int>? tagIds,
        CancellationToken cancellationToken = default)
    {
        var (accountText, tagText) = await ConvergeAsync(
            accountSetId, userId, isAdmin, accountIds, tagIds, cancellationToken);

        var existing = await FindRowAsync(accountSetId, userId, cancellationToken);

        if (existing is null)
        {
            await db.Insertable(new AccountSetPreference
            {
                AccountSetId = accountSetId,
                UserId = userId,
                MonthlyReportAccountIds = accountText,
                MonthlyReportTagIds = tagText,
            }).ExecuteCommandAsync(cancellationToken);

            return;
        }

        await db.Updateable<AccountSetPreference>()
            .SetColumns(target => new AccountSetPreference
            {
                MonthlyReportAccountIds = accountText,
                MonthlyReportTagIds = tagText,
                UpdatedAt = DateTime.UtcNow,
            })
            .Where(target => target.Id == existing.Id)
            .ExecuteCommandAsync(cancellationToken);
    }

    /// <summary>
    /// 把入参收敛成「本账套内、对当前用户可见的钱账户」与「本账套内标签」两段主键文本。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="userId">配置归属人主键。</param>
    /// <param name="isAdmin">是否为系统管理员。</param>
    /// <param name="accountIds">入参账户主键；<c>null</c> 视为空。</param>
    /// <param name="tagIds">入参标签主键；<c>null</c> 视为空。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>两段可直接写入库的逗号分隔主键文本。</returns>
    /// <remarks>
    /// **三页共用这一份收敛**：账目明细与两个报表页的账户候选都是「可见的钱账户」，
    /// 差别只在报表页还按系统默认币种收窄一层——那一层留给页面恢复时与候选求交，
    /// 不写进这里（默认币种会被管理员改，把币种判据复制到写入侧只会多出一份会漂移的规则）。
    /// <para>
    /// includeInactive 恒为 true：账户与标签都是软删除，停用项上的历史明细照常在页面呈现，
    /// 故筛选条件里保留它们是合法状态（与 <see cref="EntryQueryService"/> 取可见账户时的口径一致）。
    /// </para>
    /// </remarks>
    private async Task<(string AccountText, string TagText)> ConvergeAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        IReadOnlyCollection<int>? accountIds,
        IReadOnlyCollection<int>? tagIds,
        CancellationToken cancellationToken)
    {
        var visible = await accounts.ListByAccountSetAsync(
            accountSetId,
            userId,
            isAdmin,
            includeInactive: true,
            cancellationToken);

        var allowedAccounts = visible
            .Where(item => item.Account.Type.IsMoneyAccount())
            .Select(item => item.Account.Id)
            .ToHashSet();

        // **含已停用**——筛选区本就把停用标签列进候选（它查的正是历史账），
        // 在这里剔掉会让用户上次按它筛的视图静默变样。
        var allowedTags = (await tags.ListByAccountSetAsync(
                accountSetId,
                includeInactive: true,
                cancellationToken))
            .Select(tag => tag.Id)
            .ToHashSet();

        return (
            FormatIds(Converge(accountIds, allowedAccounts)),
            FormatIds(Converge(tagIds, allowedTags)));
    }

    /// <summary>
    /// 取某用户在某账套里的配置行。
    /// </summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="userId">配置归属人主键。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置行；不存在时返回 <c>null</c>。</returns>
    /// <remarks>
    /// 唯一索引保证至多一行；仍取首行是为了给「库里万一存在历史重复行」一个确定答案，
    /// 而不是让结果随查询计划漂移（同 <see cref="TagService.FindByNameAsync"/> 的处置）。
    /// </remarks>
    private async Task<AccountSetPreference?> FindRowAsync(
        int accountSetId,
        int userId,
        CancellationToken cancellationToken)
    {
        var matched = await db.Queryable<AccountSetPreference>()
            .Where(row => row.AccountSetId == accountSetId && row.UserId == userId)
            .OrderBy(row => row.Id)
            .Take(1)
            .ToListAsync(cancellationToken);

        return matched.FirstOrDefault();
    }

    /// <summary>
    /// 把入参主键收敛为「允许集合内、去重、保序」的一份。
    /// </summary>
    /// <param name="ids">入参主键集合；<c>null</c> 视为空。</param>
    /// <param name="allowed">允许的主键集合（本账套内的有效主键）。</param>
    /// <returns>收敛后的主键数组。</returns>
    /// <remarks>
    /// 不在 <paramref name="allowed"/> 里的主键**静默丢弃**（理由见接口注释），
    /// 且**保序**：按入参首次出现的顺序，重复项只留第一次
    /// （与交易标签关联行「顺序 = 首次出现的顺序」的口径一致）。
    /// </remarks>
    private static int[] Converge(IReadOnlyCollection<int>? ids, HashSet<int> allowed) =>
        ids is null
            ? []
            : ids.Where(allowed.Contains).Distinct().ToArray();

    /// <summary>
    /// 把主键数组拼成库里存的文本形态。
    /// </summary>
    /// <param name="ids">主键数组。</param>
    /// <returns>逗号分隔的文本；空数组得到空串。</returns>
    private static string FormatIds(int[] ids) => string.Join(ID_SEPARATOR, ids);

    /// <summary>
    /// 解析库里存的主键文本。
    /// </summary>
    /// <param name="text">逗号分隔的文本（可能为空串）。</param>
    /// <returns>主键数组；空串与解析不出数字的片段都跳过。</returns>
    /// <remarks>
    /// **解析失败一律跳过而不是抛异常**：这一列是纯文本，理论上存在被外部工具改脏的可能，
    /// 而一处脏值让整个页面读不出配置（进而是 500）是不成比例的后果——
    /// 跳过它的效果只是「少恢复一个勾选」，页面照常可用。
    /// <para>
    /// 空白一并容忍（<c>Split</c> 后 <c>Trim</c>）：分隔符两侧多一个空格是手工改库时最常见的形态。
    /// </para>
    /// </remarks>
    private static int[] ParseIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var parsed = new List<int>();
        foreach (var part in text.Split(ID_SEPARATOR))
        {
            if (int.TryParse(part.Trim(), out var id))
            {
                parsed.Add(id);
            }
        }

        return parsed.ToArray();
    }
}
