namespace Hamster.Modules.Example.Simple;

/// <summary>
/// Todo 实体
/// </summary>
/// <param name="Id">ID</param>
/// <param name="Title">标题</param>
/// <param name="DueBy">截止日期</param>
/// <param name="IsComplete">是否完成</param>
public sealed record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);