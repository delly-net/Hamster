namespace Hamster.Api.Constant;

/// <summary>
/// API 路由常量，避免路径字符串散落在各个端点模块中。
/// </summary>
public static class ApiPathConst
{
    /// <summary>健康检查路由分组前缀。</summary>
    public const string HEALTH_GROUP = "/health";

    /// <summary>认证端点路由分组前缀。</summary>
    public const string AUTH_GROUP = "/api/auth";

    /// <summary>管理员用户管理端点路由分组前缀（需已激活的管理员身份）。</summary>
    public const string ADMIN_USERS_GROUP = "/api/admin/users";

    /// <summary>管理员账套管理端点路由分组前缀（需已激活的管理员身份）。</summary>
    public const string ADMIN_ACCOUNT_SETS_GROUP = "/api/admin/account-sets";

    /// <summary>管理员币种管理端点路由分组前缀（需已激活的管理员身份）。</summary>
    public const string ADMIN_CURRENCIES_GROUP = "/api/admin/currencies";

    /// <summary>币种查询端点路由分组前缀（任意已登录用户，只读）。</summary>
    /// <remarks>
    /// 币种字典本身是全局的，但**不放在管理端**：记账表单与账户新建表单都要用它，
    /// 而这两处面向所有登录用户。读端点在用户区、写端点在管理区，是刻意的分工。
    /// </remarks>
    public const string CURRENCY_GROUP = "/api/currencies";

    /// <summary>账套端点路由分组前缀（任意已登录用户）。</summary>
    public const string ACCOUNT_SET_GROUP = "/api/account-sets";

    /// <summary>账户端点路由分组前缀（任意已登录用户，须携带当前账套请求头）。</summary>
    public const string ACCOUNT_GROUP = "/api/accounts";

    /// <summary>分类端点路由分组前缀（任意已登录用户，须携带当前账套请求头）。</summary>
    /// <remarks>
    /// 分类**按账套隔离**（每个账套各维护一份），故它读 <c>X-Account-Set-Id</c> 请求头，
    /// 这一点与 <see cref="CURRENCY_GROUP"/> 相反、与 <see cref="ACCOUNT_GROUP"/> 一致。
    /// <para>
    /// 读写**不拆成两个分组**（不像币种那样把维护能力放到管理端）：
    /// 分类是账套内所有成员共用的字典，维护它不需要系统管理员身份，
    /// 拆出去只会让普通用户面对一个自己建的分类却无权改名的局面。
    /// </para>
    /// </remarks>
    public const string CATEGORY_GROUP = "/api/categories";

    /// <summary>标签端点路由分组前缀（任意已登录用户，须携带当前账套请求头）。</summary>
    /// <remarks>
    /// 口径与 <see cref="CATEGORY_GROUP"/> **逐条相同**：标签按账套隔离、软删除、
    /// 账套内所有成员共用一份，且记账时手工输入的新名字会被自动创建，
    /// 故读写不拆成两个分组、也不带管理员门槛。
    /// <para>
    /// 与分类**唯一的差别是基数**：一笔交易至多一个分类（分类是交易头上的一列），
    /// 而一笔交易可以有多个标签（落在 <c>hamster_transaction_tag</c> 子表里）。
    /// </para>
    /// </remarks>
    public const string TAG_GROUP = "/api/tags";

    /// <summary>账目明细查询端点路由分组前缀（任意已登录用户，须携带当前账套请求头）。</summary>
    public const string ENTRY_GROUP = "/api/entries";

    /// <summary>记账端点路由分组前缀（任意已登录用户，须携带当前账套请求头）。</summary>
    public const string TRANSACTION_GROUP = "/api/transactions";

    /// <summary>示例账户端点路由分组前缀。</summary>
    public const string SAMPLE_ACCOUNT_GROUP = "/api/sample/accounts";
}
