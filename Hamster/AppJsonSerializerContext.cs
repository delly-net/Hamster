using System.Text.Json.Serialization;
using Hamster.Modules.Example.Simple;

namespace Hamster;

/// <summary>
/// Json序列化上下文
/// </summary>
[JsonSerializable(typeof(Todo))]
[JsonSerializable(typeof(List<Todo>))]
public sealed partial class AppJsonSerializerContext : JsonSerializerContext;