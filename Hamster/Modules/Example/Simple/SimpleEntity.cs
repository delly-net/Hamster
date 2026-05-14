using System.Text.Json.Serialization;

namespace Hamster.Modules.Example.Simple;

/// <summary>
/// Simple分类Json序列化上下文
/// </summary>
[JsonSerializable(typeof(Todo))]
[JsonSerializable(typeof(List<Todo>))]
public sealed partial class SimpleJsonSerializerContext : JsonSerializerContext;

/// <summary>
/// Todo 实体
/// </summary>
/// <param name="Id">ID</param>
/// <param name="Title">标题</param>
/// <param name="DueBy">截止日期</param>
/// <param name="IsComplete">是否完成</param>
public sealed record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);