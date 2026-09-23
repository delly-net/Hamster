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

    /// <summary>账目明细查询端点路由分组前缀（任意已登录用户，须携带当前账套请求头）。</summary>
    public const string ENTRY_GROUP = "/api/entries";

    /// <summary>记账端点路由分组前缀（任意已登录用户，须携带当前账套请求头）。</summary>
    public const string TRANSACTION_GROUP = "/api/transactions";

    /// <summary>示例账户端点路由分组前缀。</summary>
    public const string SAMPLE_ACCOUNT_GROUP = "/api/sample/accounts";
}
