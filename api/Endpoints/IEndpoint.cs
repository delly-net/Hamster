namespace Hamster.Api.Endpoints;

/// <summary>
/// 端点模块约定：实现该接口即会被自动注册，无需修改 Program.cs。
/// </summary>
/// <remarks>
/// 新增业务模块的标准做法：在 <c>Endpoints</c> 目录下新建一个实现本接口的 <c>sealed</c> 类，
/// 在 <see cref="Map"/> 中声明路由即可。
/// </remarks>
public interface IEndpoint
{
    /// <summary>注册该模块的路由。</summary>
    /// <param name="app">端点路由构建器。</param>
    void Map(IEndpointRouteBuilder app);
}
