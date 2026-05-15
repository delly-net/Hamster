# 简介

针对MiniJsonSerializerAttribute特性进行源生成支持

# 规则

- 添加针对MiniNamedMiniJsonSerializerAttribute特性的源生成能力
- 源生成的代码文件以.g.cs结尾
- 读取程序集中所有的MiniNamedAttribute特性定义的类信息，生成JsonSerializable特性信息
- 需要生成类与List两个JsonSerializable特性
- 严格按照项目规范进行代码生成，评估可行性，当现有技术规范不满足需求时，停止代码生成并提示
- 不允许修改源生成子项目以外的代码文件

# 目标

- 达到**生成代码示例**中的效果，格式完全一样

# 生成代码示例

```
using System.Text.Json.Serialization;
using Hamster.Modules.Example.Simple;

namespace Hamster;

/// <summary>
/// Json序列化上下文
/// </summary>
[JsonSerializable(typeof(Todo))]
[JsonSerializable(typeof(List<Todo>))]
public partial class JsonSerializer : JsonSerializerContext;
```