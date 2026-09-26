using Hamster.Api.Data.Entities;
using SqlSugar;

namespace Hamster.Api.Services;

/// <summary>
/// 基于 SqlSugar 的账目明细查询实现。
/// </summary>
/// <param name="db">SqlSugar 客户端（单例 Scope，可安全并发使用）。</param>
/// <param name="accounts">
/// 账户服务：本服务只借用它的**可见性判定**（列出当前用户可见的账户），不写任何账户数据。
/// 依赖方向不成环——<see cref="AccountService"/> 只依赖 <see cref="ITransactionService"/>，
/// 不会回头依赖本服务。
/// </param>
public sealed class EntryQueryService(ISqlSugarClient db, IAccountService accounts) : IEntryQueryService
{
    /// <inheritdoc />
    public async Task<EntryQueryPage> QueryAsync(
        int accountSetId,
        int userId,
        bool isAdmin,
        DateTime? from,
        DateTime? to,
        IReadOnlyCollection<int>? accountIds,
        IReadOnlyCollection<int>? tagIds,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        // 可见账户集：一次取回，**两用**——但它对两用的口径并不相同，故下面拆成两个集合。
        // includeInactive 恒为 true：账户是软删除，停用账户上的历史明细仍然查得到（见接口注释）。
        var visible = await accounts.ListByAccountSetAsync(
            accountSetId,
            userId,
            isAdmin,
            includeInactive: true,
            cancellationToken);

        // 用途一：**账户名的来源**，取全部可见账户（含往来账户）。
        // 往来账户必须留在这里：它要在「现金 → 老王」这类行的**对手方列**上显示名字。
        // 若把它一并删掉，ResolveCounterpartiesAsync 会走「不在 nameById 里 → 查类型」的分支，
        // 命中 Contact ≠ Ledger 而落到 CounterpartyKind.Hidden，界面把「—」渲染到对手方列——
        // 等于把用户自己的往来账户伪装成不可见账户（Hidden 是权限结论，不是「我不呈现它」）。
        var nameById = visible.ToDictionary(item => item.Account.Id, item => item.Account.Name);

        // 用途二：**明细行的过滤依据**，只取钱账户（资金/负债）。
        // 本页回答的是「钱动在哪个账户」，而往来账户记的是「谁欠谁」而不是「钱放在哪」——
        // 它上面的明细是另一本账，不在本页呈现（同一条依据也是转账两端的限制，
        // 定义见 AccountTypeExtensions.IsMoneyAccount）。它的余额与来往由账户管理页承担。
        //
        // 过滤发生在**内存里的 visible 列表**上：SqlSugar 不翻译扩展方法，
        // 该谓词不能写进 BuildBaseQuery 的表达式树（同 AccountService 里那处枚举字面量）。
        var moneyIds = visible
            .Where(item => item.Account.Type.IsMoneyAccount())
            .Select(item => item.Account.Id)
            .ToHashSet();

        // 目标账户集：未指定账户时即全部**钱账户**；指定了则与钱账户集**求交**而非报错
        // （不可见的账户、以及往来账户都被静默剔除，交集为空就返回空页——
        // 不泄露「该账户是否存在」）。求交同时挡住「传往来账户主键」这条路径：
        // 候选列表（前端）本就不含往来账户，这里再挡一次，直接构造的请求也查不到它们。
        var targetIds = accountIds is null || accountIds.Count == 0
            ? moneyIds.ToArray()
            : accountIds.Where(moneyIds.Contains).Distinct().ToArray();

        if (targetIds.Length == 0)
        {
            return EmptyPage(page, pageSize);
        }

        // 标签筛选集：去重后即条件用的一份（**不与任何集合求交**，与 targetIds 的处理刻意不同）。
        // 不属于本账套的标签主键在这里不需要被剔除：它匹配不到任何关联行，
        // 结果与「该标签在库里不存在」完全相同，而这正是想要的——报错才会变成探针（见接口注释）。
        var tagFilter = tagIds is null || tagIds.Count == 0
            ? []
            : tagIds.Distinct().ToArray();

        // 条件先落到非空局部变量再进表达式树：SqlSugar 对「DateTime 与 DateTime? 比较」的翻译不可靠，
        // 拆开后表达式里只剩两个 DateTime 的比较。
        var hasFrom = from is not null;
        var fromValue = from ?? default;
        var hasTo = to is not null;
        var toValue = to ?? default;

        // 计数与取数各自新建查询对象：ISugarQueryable 是会被链式方法改写的，
        // 复用同一个实例会让计数结果带上分页条件。
        var total = await BuildBaseQuery(accountSetId, targetIds, hasFrom, fromValue, hasTo, toValue, tagFilter)
            .CountAsync(cancellationToken);

        if (total == 0)
        {
            return EmptyPage(page, pageSize);
        }

        // 先投影再排序分页：投影后的查询只带一个泛型参数，`OrderBy` / `Skip` / `Take` 的签名明确，
        // 不会在「联表双泛型」的链式方法里撞上重载歧义。
        // **`MergeTable()` 不可省**：`Select` 之后 SqlSugar 仍把查询视为双表联查，
        // 直接接单参数 `OrderBy(row => ...)` 会撞上别名一致性检查（要求排序 lambda 的形参名与联表别名一致）
        // 并抛「多表查询存在别名不一致」。MergeTable 把投影结果集并成单表，此后排序分页不再受此限制
        // ——这是该异常提示给出的官方出路。投影里的每个属性都直接来自某个表的列，故排序键仍是确切的表列。
        // 三级排序键的最后一级是明细主键，它让「同一时刻的多条明细」也有确定次序——
        // 否则翻页时同一行可能在两页里各出现一次。
        var rows = await BuildBaseQuery(accountSetId, targetIds, hasFrom, fromValue, hasTo, toValue, tagFilter)
            .Select((entry, tx) => new EntryRow
            {
                EntryId = entry.Id,
                TransactionId = entry.TransactionId,
                AccountId = entry.AccountId,
                Direction = entry.Direction,
                Amount = entry.Amount,
                OccurredAt = tx.OccurredAt,
                Summary = tx.Summary,
                Remark = tx.Remark,
                Type = tx.Type,
                // 只取分类主键，名称稍后批量解析：分类名要查分类表，而联表已到两张表，
                // 再挂一张会让 BuildBaseQuery 变成三表联查（它的别名一致性本就脆弱）。
                // 名称也不随交易行冗余存储——那样分类改名后历史明细会停留在旧名字上。
                CategoryId = tx.CategoryId,
            })
            .MergeTable()
            .OrderBy(row => row.OccurredAt, OrderByType.Asc)
            .OrderBy(row => row.TransactionId, OrderByType.Asc)
            .OrderBy(row => row.EntryId, OrderByType.Asc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var counterparties = await ResolveCounterpartiesAsync(rows, nameById, cancellationToken);
        var categoryNames = await ResolveCategoryNamesAsync(rows, cancellationToken);
        var tagsByTransaction = await ResolveTagsAsync(rows, cancellationToken);

        var items = rows
            .Select(row =>
            {
                var counterparty = counterparties[row.EntryId];

                // 分类主键与名称**从同一处取**，故同生同灭：只给出主键而名字查不到，
                // 界面上就是一个渲染不出任何文字的空档（分类行只软删除，正常不会查不到）。
                int? categoryId = null;
                string? categoryName = null;
                if (row.CategoryId is { } id && categoryNames.TryGetValue(id, out var resolved))
                {
                    categoryId = id;
                    categoryName = resolved;
                }

                // 「这条明细是否挂在主账户上」的判据只有一处定义（含收入为何是借方、
                // 支出与转账为何同向），见 TransactionTypeExtensions.PrimaryDirection。
                // **在内存里算、不进 Select 投影**：SqlSugar 不翻译扩展方法（同上面 moneyIds 那处）。
                // 期初余额没有主账户方向（PrimaryDirection 返回 null），故期初行恒为 false——
                // 那不是「算不出来」，而是该概念对它本就不存在（详见 EntryQueryRow.IsPrimary 的说明）。
                var isPrimary = row.Type.PrimaryDirection() is { } primaryDirection
                    && row.Direction == primaryDirection;

                return new EntryQueryRow(
                    row.EntryId,
                    row.TransactionId,
                    row.OccurredAt,
                    row.Summary,
                    row.Remark,
                    row.Type,
                    row.AccountId,
                    nameById[row.AccountId],
                    row.Direction,
                    row.Amount,
                    counterparty.Kind,
                    counterparty.AccountId,
                    counterparty.Name,
                    categoryId,
                    categoryName,
                    isPrimary,
                    // 标签按**交易**取（同一笔交易的两条明细得到同一份），查不到即空列表。
                    // 与分类一样「没有标签」是合法常态，故不做任何占位
                    tagsByTransaction.TryGetValue(row.TransactionId, out var tags) ? tags : []);
            })
            .ToArray();

        return new EntryQueryPage(items, total, page, pageSize);
    }

    /// <summary>构造基础查询：联表 + 账套与账户过滤 + 时间区间。</summary>
    /// <param name="accountSetId">账套主键。</param>
    /// <param name="targetIds">
    /// 目标账户主键（必然已与**钱账户集**求交，故只含资金/负债账户）。
    /// </param>
    /// <param name="hasFrom">是否限制下界。</param>
    /// <param name="fromValue">下界（含）。</param>
    /// <param name="hasTo">是否限制上界。</param>
    /// <param name="toValue">上界（含）。</param>
    /// <param name="tagIds">标签筛选集；**空数组表示不限标签**（与 <paramref name="targetIds"/> 不同，
    /// 后者空数组是不可达状态——调用方在它为空时就返回空页了）。</param>
    /// <returns>每次调用**新建**的查询对象。</returns>
    /// <remarks>
    /// 每次新建而非复用：计数与分页取数用的是两份互不干扰的查询。
    /// <para>
    /// 时间条件写在**父交易的业务发生时间**上（<see cref="Transaction.OccurredAt"/>）：
    /// 不是落库时间，也不是明细上的字段——明细刻意没有自己的时间列。
    /// </para>
    /// <para>
    /// **标签条件用相关子查询，不用联表**：一笔交易可以挂多个标签，把
    /// <see cref="TransactionTag"/> 联进来会让一笔多标签的交易在结果里**出现多行**
    /// （每个标签一行），于是 <c>CountAsync</c> 得到的总数比真正渲染的行数大，
    /// 页数也跟着说谎——而本页的口径是「total 与真正渲染的行数恒等」（见 #46 对明细行过滤的注释）。
    /// <c>EXISTS</c> 形式的子查询只判有无、不产生行，这个口径不受标签个数影响。
    /// </para>
    /// <para>
    /// 匹配语义是「**任一命中**」而不是「全部命中」：多选标签的常规意图是「这几类我都想看看」。
    /// </para>
    /// </remarks>
    private ISugarQueryable<TransactionEntry, Transaction> BuildBaseQuery(
        int accountSetId,
        int[] targetIds,
        bool hasFrom,
        DateTime fromValue,
        bool hasTo,
        DateTime toValue,
        int[] tagIds) =>
        db.Queryable<TransactionEntry>()
            .LeftJoin<Transaction>((entry, tx) => entry.TransactionId == tx.Id)
            .Where((entry, tx) => tx.AccountSetId == accountSetId && targetIds.Contains(entry.AccountId))
            .WhereIF(hasFrom, (entry, tx) => tx.OccurredAt >= fromValue)
            .WhereIF(hasTo, (entry, tx) => tx.OccurredAt <= toValue)
            .WhereIF(
                tagIds.Length > 0,
                (entry, tx) => SqlFunc.Subqueryable<TransactionTag>()
                    .Where(link => link.TransactionId == tx.Id && tagIds.Contains(link.TagId))
                    .Any());

    /// <summary>
    /// 解析本页每条明细的对手方。
    /// </summary>
    /// <param name="rows">本页明细。</param>
    /// <param name="nameById">
    /// 可见账户主键到名称的映射（**含往来账户**：对手方列要显示「老王」这样的名字）。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>明细主键到对手方描述的映射。</returns>
    /// <remarks>
    /// 对手方定义为**同一交易中方向相反的第一条明细**（按明细主键升序）。
    /// 当前每笔交易恰有借贷两条明细，故对手方唯一；若将来出现多明细交易，本处以「取首条」给出确定答案，
    /// 而不是造出一个多对多的对手方列表——那需要的是另一种界面，不是本方法悄悄扩大返回值。
    /// <para>
    /// 只查本页交易涉及的明细（行数受页大小约束），且对不可见对手方**只取主键与类型、不取名称**：
    /// 判断「它是账本账户还是他人的个人账户」需要类型，而呈现它们则必须什么也不给。
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, Counterparty>> ResolveCounterpartiesAsync(
        IReadOnlyList<EntryRow> rows,
        IReadOnlyDictionary<int, string> nameById,
        CancellationToken cancellationToken)
    {
        var transactionIds = rows.Select(row => row.TransactionId).Distinct().ToArray();
        if (transactionIds.Length == 0)
        {
            return new Dictionary<int, Counterparty>();
        }

        var siblings = await db.Queryable<TransactionEntry>()
            .Where(entry => transactionIds.Contains(entry.TransactionId))
            .Select(entry => new SiblingEntry
            {
                EntryId = entry.Id,
                TransactionId = entry.TransactionId,
                AccountId = entry.AccountId,
                Direction = entry.Direction,
            })
            .ToListAsync(cancellationToken);

        var byTransaction = siblings
            .GroupBy(entry => entry.TransactionId)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.EntryId).ToArray());

        // 先在内存里定出每行明细的对手方账户主键，再统一去查「不可见对手方」的类型——
        // 避免在循环里逐行查库（N+1）。
        var counterpartyAccountByEntry = new Dictionary<int, int>();
        var unresolvedAccountIds = new HashSet<int>();

        foreach (var row in rows)
        {
            if (!byTransaction.TryGetValue(row.TransactionId, out var group))
            {
                continue;
            }

            var counterparty = group.FirstOrDefault(candidate => candidate.Direction != row.Direction);
            if (counterparty is null)
            {
                continue;
            }

            counterpartyAccountByEntry[row.EntryId] = counterparty.AccountId;

            if (!nameById.ContainsKey(counterparty.AccountId))
            {
                unresolvedAccountIds.Add(counterparty.AccountId);
            }
        }

        // 仅取主键与类型：名称对不可见对手方一律不外泄。
        // 无不可见对手方时不查库（本页可能全部对手方都可见，那是常态）。
        var hiddenTypes = new Dictionary<int, AccountType>();
        if (unresolvedAccountIds.Count > 0)
        {
            var hiddenAccounts = await db.Queryable<Account>()
                .Where(account => unresolvedAccountIds.Contains(account.Id))
                .Select(account => new HiddenAccount { Id = account.Id, Type = account.Type })
                .ToListAsync(cancellationToken);

            hiddenTypes = hiddenAccounts.ToDictionary(row => row.Id, row => row.Type);
        }

        var result = new Dictionary<int, Counterparty>();
        foreach (var row in rows)
        {
            if (!counterpartyAccountByEntry.TryGetValue(row.EntryId, out var counterpartyAccountId))
            {
                result[row.EntryId] = new Counterparty(CounterpartyKind.None, null, null);
                continue;
            }

            if (nameById.TryGetValue(counterpartyAccountId, out var name))
            {
                result[row.EntryId] = new Counterparty(CounterpartyKind.Account, counterpartyAccountId, name);
                continue;
            }

            // 不可见：只区分「系统账本账户」与「其余不可见账户」。账本账户是每账套至多一个的系统自有账户、
            // 其存在性已在 README 公开，故单独一档不构成信息披露；他人个人账户则连主键都不给。
            var kind = hiddenTypes.TryGetValue(counterpartyAccountId, out var type) && type == AccountType.Ledger
                ? CounterpartyKind.Ledger
                : CounterpartyKind.Hidden;

            result[row.EntryId] = new Counterparty(kind, null, null);
        }

        return result;
    }

    /// <summary>
    /// 批量取本页交易用到的分类名称。
    /// </summary>
    /// <param name="rows">本页明细。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>分类主键到名称的映射；本页无分类时为空字典。</returns>
    /// <remarks>
    /// **查名称而不是随投影联表带出**：给 <see cref="BuildBaseQuery"/> 再挂一张表会让它变成三表联查，
    /// 而那条查询的别名一致性本就脆弱（见 <c>QueryAsync</c> 中关于 <c>MergeTable()</c> 的注释）。
    /// 分类名是纯粹的查表，用一次 <c>IN</c> 查询批量取回即可，代价与页大小同阶。
    /// <para>
    /// **不过滤 <see cref="Category.IsActive"/>**：停用是「不再出现在记账候选里」，
    /// 不是「历史上从未用过」。给历史明细隐藏分类名，等于让用户的旧账凭空少了一列。
    /// </para>
    /// <para>
    /// 本页一条分类都没有时不查库：那是常态（分类是可选的）。
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, string>> ResolveCategoryNamesAsync(
        IReadOnlyList<EntryRow> rows,
        CancellationToken cancellationToken)
    {
        var categoryIds = rows
            .Where(row => row.CategoryId.HasValue)
            .Select(row => row.CategoryId!.Value)
            .Distinct()
            .ToArray();

        if (categoryIds.Length == 0)
        {
            return new Dictionary<int, string>();
        }

        var categories = await db.Queryable<Category>()
            .Where(category => categoryIds.Contains(category.Id))
            .Select(category => new CategoryName { Id = category.Id, Name = category.Name })
            .ToListAsync(cancellationToken);

        return categories.ToDictionary(row => row.Id, row => row.Name);
    }

    /// <summary>
    /// 批量取本页交易挂着的标签。
    /// </summary>
    /// <param name="rows">本页明细。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>交易主键到其标签列表的映射；不带标签的交易**不出现在映射里**（调用方按空列表处理）。</returns>
    /// <remarks>
    /// 与 <see cref="ResolveCategoryNamesAsync"/> 同一取舍：**查名称而不是随投影联表带出**——
    /// 给 <see cref="BuildBaseQuery"/> 再挂一张表会让它变成三表联查，且标签是多值的，
    /// 联表会像标签筛选那样把行数乘开。一次 <c>IN</c> 查询批量取回，代价与页大小同阶。
    /// <para>
    /// **不过滤 <see cref="Tag.IsActive"/>**：停用是「不再出现在候选里」，
    /// 不是「历史上从未用过」。给历史明细隐藏标签名，等于让用户的旧账凭空少了这一层标注
    /// （与分类名同一口径）。
    /// </para>
    /// <para>
    /// 排序在**内存里按关联行主键**做：SqlSugar 在投影后的联表查询上做 <c>OrderBy</c>
    /// 会撞上别名一致性检查（与 <c>QueryAsync</c> 里 <c>MergeTable()</c> 那段注释同一问题），
    /// 而这里的数据量受页大小约束，排序代价可以忽略。
    /// 按关联行主键（而非标签名或标签主键）升序 = **用户当初提交标签的次序**，
    /// 界面上标签的先后与记账时填的一致。
    /// </para>
    /// <para>
    /// 本页一笔交易都没挂标签时不查库：那是常态（标签是可选的）。
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<EntryTag>>> ResolveTagsAsync(
        IReadOnlyList<EntryRow> rows,
        CancellationToken cancellationToken)
    {
        var transactionIds = rows.Select(row => row.TransactionId).Distinct().ToArray();
        if (transactionIds.Length == 0)
        {
            return new Dictionary<int, IReadOnlyList<EntryTag>>();
        }

        // 标签名取的是关联**指向的那条标签行**的当前名字（改名后历史明细自动显示新名字），
        // 而不是把名字冗余在关联行上——关联行刻意只有两个主键（见 TransactionTag 的类头注释）
        var links = await db.Queryable<TransactionTag>()
            .LeftJoin<Tag>((link, tag) => link.TagId == tag.Id)
            .Where((link, tag) => transactionIds.Contains(link.TransactionId))
            .Select((link, tag) => new EntryTagLink
            {
                LinkId = link.Id,
                TransactionId = link.TransactionId,
                TagId = link.TagId,
                Name = tag.Name,
            })
            .ToListAsync(cancellationToken);

        return links
            // 标签行缺失（外键未被数据库强制，且本系统只软删除标签、正常不会有）时静默跳过该关联行：
            // 造不出名字的标签对界面毫无意义，而 Name 为 null 的标签会渲染成一片空白
            .Where(link => !string.IsNullOrEmpty(link.Name))
            .OrderBy(link => link.LinkId)
            .GroupBy(link => link.TransactionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<EntryTag>)[.. group.Select(link => new EntryTag(link.TagId, link.Name))]);
    }

    /// <summary>空页（无匹配明细，或目标账户集为空时直接给出，不必查库）。</summary>
    /// <param name="page">页码。</param>
    /// <param name="pageSize">每页条数。</param>
    /// <returns>条目为空、总数为 0 的一页。</returns>
    private static EntryQueryPage EmptyPage(int page, int pageSize) => new([], 0, page, pageSize);

    /// <summary>
    /// 分页查询的投影结果。
    /// </summary>
    /// <remarks>
    /// 用具名类型而非匿名类型：它要跨越两个方法（取数与对手方解析），匿名类型无法作为参数类型声明。
    /// **必须是可写属性的类**而非位置记录——SqlSugar 的 <c>Select</c> 靠属性赋值而非构造参数映射。
    /// </remarks>
    private sealed class EntryRow
    {
        /// <summary>明细主键。</summary>
        public int EntryId { get; set; }

        /// <summary>所属交易主键。</summary>
        public int TransactionId { get; set; }

        /// <summary>挂靠账户主键。</summary>
        public int AccountId { get; set; }

        /// <summary>借贷方向。</summary>
        public EntryDirection Direction { get; set; }

        /// <summary>金额（恒正）。</summary>
        public decimal Amount { get; set; }

        /// <summary>业务发生时间（UTC）。</summary>
        public DateTime OccurredAt { get; set; }

        /// <summary>交易摘要。</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>交易备注。</summary>
        public string? Remark { get; set; }

        /// <summary>交易类型。</summary>
        public TransactionType Type { get; set; }

        /// <summary>交易分类主键；未分类时为 <c>null</c>。</summary>
        public int? CategoryId { get; set; }
    }

    /// <summary>同笔交易的兄弟明细（用于定位对手方）。</summary>
    private sealed class SiblingEntry
    {
        /// <summary>明细主键。</summary>
        public int EntryId { get; set; }

        /// <summary>所属交易主键。</summary>
        public int TransactionId { get; set; }

        /// <summary>挂靠账户主键。</summary>
        public int AccountId { get; set; }

        /// <summary>借贷方向。</summary>
        public EntryDirection Direction { get; set; }
    }

    /// <summary>不可见对手方账户的查询结果（只取主键与类型）。</summary>
    private sealed class HiddenAccount
    {
        /// <summary>账户主键。</summary>
        public int Id { get; set; }

        /// <summary>账户类型。</summary>
        public AccountType Type { get; set; }
    }

    /// <summary>分类名称的查询结果（只取主键与名称）。</summary>
    private sealed class CategoryName
    {
        /// <summary>分类主键。</summary>
        public int Id { get; set; }

        /// <summary>分类名称。</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>标签关联的查询结果（关联行主键 + 交易主键 + 标签主键与名称）。</summary>
    /// <remarks>
    /// <see cref="LinkId"/> 只在内存里当排序键用，不会出现在 <see cref="EntryTag"/> 里——
    /// 界面上标签的先后已由列表次序表达，再给一个主键只会诱使调用方自己排序。
    /// <para>
    /// <see cref="Name"/> 声明为非空 <c>string</c>：SqlSugar 的 <c>Select</c> 靠属性赋值，
    /// 左联未命中时会写入 <c>null</c>，故调用方仍须判空（见 <c>ResolveTagsAsync</c>）。
    /// </para>
    /// </remarks>
    private sealed class EntryTagLink
    {
        /// <summary>关联行主键（仅用于排序）。</summary>
        public int LinkId { get; set; }

        /// <summary>所属交易主键。</summary>
        public int TransactionId { get; set; }

        /// <summary>标签主键。</summary>
        public int TagId { get; set; }

        /// <summary>标签名称。</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>对手方描述。</summary>
    /// <param name="Kind">可见性档位。</param>
    /// <param name="AccountId">账户主键；仅 <see cref="CounterpartyKind.Account"/> 时有值。</param>
    /// <param name="Name">账户名称；仅 <see cref="CounterpartyKind.Account"/> 时有值。</param>
    private sealed record Counterparty(CounterpartyKind Kind, int? AccountId, string? Name);
}
