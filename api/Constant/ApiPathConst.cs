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

    /// <summary>示例账户端点路由分组前缀。</summary>
    public const string SAMPLE_ACCOUNT_GROUP = "/api/sample/accounts";
}
