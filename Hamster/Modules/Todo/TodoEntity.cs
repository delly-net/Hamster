using SqlSugar;
using System.Text.Json.Serialization;

namespace Hamster.Modules.Todo;

/// <summary>
/// Todo 实体
/// </summary>
/// <param name="Id">ID</param>
/// <param name="Title">标题</param>
/// <param name="DueBy">截止日期</param>
/// <param name="IsComplete">是否完成</param>
[SugarTable("todos")]
public sealed record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);

/// <summary>
/// Todo 模块 JSON 序列化上下文
/// </summary>
[JsonSerializable(typeof(Todo[]))]
[JsonSerializable(typeof(Todo))]
internal sealed partial class TodoJsonSerializerContext : JsonSerializerContext
{
}