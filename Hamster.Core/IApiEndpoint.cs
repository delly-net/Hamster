namespace Hamster.Core
{
    /// <summary>
    /// Api Endpoint
    /// </summary>
    public interface IApiEndpoint
    {
        /// <summary>
        /// 路由映射
        /// </summary>
        /// <param name="routeBuilder"></param>
        void Map(IEndpointRouteBuilder routeBuilder);
    }
}
