using System;

namespace Hamster.Attributing
{
    /// <summary>
    /// 支持Aot编译的Minimal API应用
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class MiniGroupAttribute : Attribute
    {
        /// <summary>
        /// 支持Aot编译的Minimal API应用
        /// </summary>
        /// <param name="template"></param>
        public MiniGroupAttribute(string rule, string template = null)
        {
            Rule = rule;
            Template = template;
        }

        /// <summary>
        /// 匹配规则
        /// </summary>
        public string Rule { get; }

        /// <summary>
        /// 模板
        /// </summary>
        public string Template { get; }
    }
}


