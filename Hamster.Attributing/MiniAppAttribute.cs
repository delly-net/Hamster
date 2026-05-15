using System;

namespace Hamster.Attributing
{
    /// <summary>
    /// 支持Aot编译的Minimal API应用
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class MiniAppAttribute : Attribute
    {
        /// <summary>
        /// 支持Aot编译的Minimal API应用
        /// </summary>
        /// <param name="template"></param>
        public MiniAppAttribute(string template = null)
        {
            Template = template;
        }

        /// <summary>
        /// 模板
        /// </summary>
        public string Template { get; }
    }
}


