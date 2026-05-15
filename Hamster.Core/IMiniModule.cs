using System;
using System.Collections.Generic;
using System.Text;

namespace Hamster.Core;

/// <summary>
/// Minimal API 应用模块
/// </summary>
public interface IMiniModule
{
    /// <summary>
    /// 路由注册
    /// </summary>
    /// <param name="routeBuilder"></param>
    void Map(IEndpointRouteBuilder routeBuilder);
}
