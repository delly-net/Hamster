using SqlSugar;

namespace Hamster.Modules.Example.Simple;

/// <summary>
/// Todo 实体
/// </summary>
[SugarTable("todos")]
public sealed class Todo
{
    /// <summary>
    /// ID
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    /// <summary>
    /// 标题
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 截止日期
    /// </summary>
    public DateOnly? DueBy { get; set; }

    /// <summary>
    /// 是否完成
    /// </summary>
    public bool IsComplete { get; set; }
}