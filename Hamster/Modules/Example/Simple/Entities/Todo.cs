using Hamster.Attributing;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace Hamster.Modules.Example.Simple.Entities;

/// <summary>
/// Todo 实体
/// </summary>
[Table("todo")]
[MiniNamed]
public partial class Todo
{
    /// <summary>
    /// Id
    /// </summary>
    [Column("id")]
    public int Id { get; set; }

    /// <summary>
    /// 标题
    /// </summary>
    [Column("title")]
    [Description("标题")]
    public string? Title { get; set; }

    /// <summary>
    /// 操作时间
    /// </summary>
    [Column("due_by")]
    [Description("操作时间")]
    public DateOnly? DueBy { get; set; }

    /// <summary>
    /// 是否完成
    /// </summary>
    [Column("is_complete")]
    [Description("是否完成")]
    public bool IsComplete { get; set; }
}
