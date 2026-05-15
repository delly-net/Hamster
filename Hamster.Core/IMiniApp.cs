using System;
using System.Collections.Generic;
using System.Text;

namespace Hamster.Core;

/// <summary>
/// Minimal API 应用
/// </summary>
public interface IMiniApp
{
    /// <summary>
    /// 路由注册
    /// </summary>
    /// <param name="routeBuilder"></param>
    void Map(IEndpointRouteBuilder routeBuilder);
}
