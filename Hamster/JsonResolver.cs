using Hamster.Attributing;
using Hamster.Modules.Example.Simple.Entities;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Hamster;

public partial class JsonResolver
{
    /// <summary>
    /// 获取 Resolver 集合
    /// </summary>
    /// <returns></returns>
    public static List<IJsonTypeInfoResolver> GetResolvers()
    {
        return
        [
            Hamster.Modules.Example.Simple.Entities.Todo.JsonSerializer.Default,
        ];
    }
}


/// <summary>
/// Json序列化上下文
/// </summary>
[MiniJsonResolver]
public partial class JsonResolver;