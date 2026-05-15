# 简介

针对MiniJsonResolverAttribute特性进行源生成支持

# 规则

- 添加针对MiniJsonResolverAttribute特性的源生成能力
- 源生成的代码文件以.g.cs结尾
- 读取程序集中所有的MiniJsonSerializerAttribute特性定义的类信息，进行整合
- 需要将类完整的**命名空间.类名.Default**进行生成并添加到GetResolvers函数中
- 严格按照项目规范进行代码生成，评估可行性，当现有技术规范不满足需求时，停止代码生成并提示
- 不允许修改源生成子项目以外的代码文件

# 目标

- 达到**生成代码示例**中的效果，格式完全一样

# 生成代码示例

```
using Hamster.Attributing;
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
```