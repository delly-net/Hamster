namespace Hamster.Core;

/// <summary>
/// 支持Aot编译的Minimal API应用
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class MiniAppAttribute(string? template = null) : Attribute
{
    /// <summary>
    /// 模板
    /// </summary>
    public string? Template { get; } = template;
}
