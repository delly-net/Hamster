namespace Hamster.Api.Config;

/// <summary>
/// 对外公开的前端访问地址配置，对应 appsettings.json 的 <c>PublicUrl</c> 节点。
/// </summary>
/// <remarks>
/// 密码重置链接由用户在浏览器中打开，必须指向**前端**页面（开发环境 5173、后端 5004，两者不同源）。
/// 不能取请求头中的 <c>Origin</c> 拼装——该值可被伪造，管理员拿到的链接会指向攻击者站点。
/// </remarks>
public sealed class PublicUrlOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "PublicUrl";

    /// <summary>前端站点基址（末尾斜杠会被忽略）。</summary>
    public string BaseUrl { get; set; } = ConfigConst.DEFAULT_PUBLIC_BASE_URL;

    /// <summary>密码重置页路径。</summary>
    public const string RESET_PASSWORD_PATH = "/reset-password";

    /// <summary>
    /// 从配置与环境变量解析，环境变量优先。
    /// </summary>
    /// <param name="configuration">应用配置。</param>
    /// <returns>公开地址配置实例。</returns>
    public static PublicUrlOptions From(IConfiguration configuration)
    {
        var options = new PublicUrlOptions();
        configuration.GetSection(SectionName).Bind(options);

        var envBaseUrl = Environment.GetEnvironmentVariable(ConfigConst.PUBLIC_BASE_URL_ENV);
        if (!string.IsNullOrWhiteSpace(envBaseUrl))
        {
            options.BaseUrl = envBaseUrl.Trim();
        }

        return options;
    }

    /// <summary>
    /// 拼装密码重置链接。
    /// </summary>
    /// <param name="username">用户名（重置页需回填，与令牌构成双重校验）。</param>
    /// <param name="token">明文重置令牌。</param>
    /// <returns>可直接发给用户的完整链接。</returns>
    public string BuildResetUrl(string username, string token)
    {
        var baseUrl = BaseUrl.TrimEnd('/');
        var query = $"username={Uri.EscapeDataString(username)}&token={Uri.EscapeDataString(token)}";
        return $"{baseUrl}{RESET_PASSWORD_PATH}?{query}";
    }
}
