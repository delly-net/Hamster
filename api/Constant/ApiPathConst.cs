namespace Hamster.Api.Constant;

/// <summary>
/// API 路由常量，避免路径字符串散落在各个端点模块中。
/// </summary>
public static class ApiPathConst
{
    /// <summary>健康检查路由分组前缀。</summary>
    public const string HEALTH_GROUP = "/health";

    /// <summary>示例账户端点路由分组前缀。</summary>
    public const string SAMPLE_ACCOUNT_GROUP = "/api/sample/accounts";
}
