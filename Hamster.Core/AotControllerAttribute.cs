namespace Hamster.Core;

/// <summary>
/// 支持 Aot 编译的控制器
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class AotControllerAttribute : Attribute
{
}
