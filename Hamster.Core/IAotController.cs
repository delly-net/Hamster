using System;
using System.Collections.Generic;
using System.Text;

namespace Hamster.Core;

/// <summary>
/// 控制器
/// </summary>
public interface IAotController
{
    /// <summary>
    /// 路由注册
    /// </summary>
    /// <param name="routeBuilder"></param>
    void RouteRegister(IEndpointRouteBuilder routeBuilder);
}
