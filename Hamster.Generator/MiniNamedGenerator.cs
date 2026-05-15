using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Hamster.Generator;

[Generator(LanguageNames.CSharp)]
internal sealed class MiniNamedGenerator : IIncrementalGenerator
{
    private const string MINI_NAMED_ATTRIBUTE = "Hamster.Attributing.MiniNamedAttribute";
    private const string TABLE_ATTRIBUTE = "System.ComponentModel.DataAnnotations.Schema.TableAttribute";
    private const string COLUMN_ATTRIBUTE = "System.ComponentModel.DataAnnotations.Schema.ColumnAttribute";

    /// <summary>
    /// 初始化生成器
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var miniNamedProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                MINI_NAMED_ATTRIBUTE,
                predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, _) => GetMiniNamedInfo(ctx))
            .Where(static m => m is not null);

        context.RegisterSourceOutput(miniNamedProvider, static (spc, source) => Execute(spc, source!));
    }

    /// <summary>
    /// 获取 MiniNamed 信息
    /// </summary>
    private static MiniNamedInfo? GetMiniNamedInfo(GeneratorAttributeSyntaxContext context)
    {
        var classSymbol = (INamedTypeSymbol)context.TargetSymbol;
        var className = classSymbol.Name;
        var namespaceName = classSymbol.ContainingNamespace.ToString();
        var tableAttribute = classSymbol.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToString() == TABLE_ATTRIBUTE);

        var tableName = className.ToLowerInvariant();

        if (tableAttribute is not null)
        {
            var tableNameArg = tableAttribute.NamedArguments.FirstOrDefault(a => a.Key == "Name");
            if (tableNameArg.Value.Value is string tableNameValue)
            {
                tableName = tableNameValue;
            }
        }

        var properties = new List<PropertyInfo>();

        foreach (var member in classSymbol.GetMembers())
        {
            if (member is not IPropertySymbol propertySymbol)
            {
                continue;
            }

            if (propertySymbol.DeclaredAccessibility != Accessibility.Public)
            {
                continue;
            }

            if (propertySymbol.IsStatic)
            {
                continue;
            }

            var columnName = null as string;
            var columnAttributes = propertySymbol.GetAttributes();

            foreach (var attr in columnAttributes)
            {
                var attrType = attr.AttributeClass?.ToDisplayString();
                if (attrType?.EndsWith("ColumnAttribute") == true)
                {
                    // Try named argument first
                    var namedArg = attr.NamedArguments.FirstOrDefault(a => a.Key == "Name");
                    if (namedArg.Value.Value is string namedName)
                    {
                        columnName = namedName;
                        break;
                    }

                    // Try constructor argument (first positional argument)
                    if (attr.ConstructorArguments.Length > 0)
                    {
                        if (attr.ConstructorArguments[0].Value is string constructorName)
                        {
                            columnName = constructorName;
                            break;
                        }
                    }
                }
            }

            columnName ??= propertySymbol.Name.ToLowerInvariant();

            properties.Add(new PropertyInfo(propertySymbol.Name, columnName));
        }

        return new MiniNamedInfo(className, namespaceName, tableName, properties);
    }

    /// <summary>
    /// 执行生成
    /// </summary>
    private static void Execute(SourceProductionContext context, MiniNamedInfo info)
    {
        var source = GenerateSource(info);
        context.AddSource($"{info.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
    }

    /// <summary>
    /// 生成源代码
    /// </summary>
    private static string GenerateSource(MiniNamedInfo info)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"namespace {info.NamespaceName};");
        sb.AppendLine();
        sb.AppendLine($"public partial class {info.ClassName}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// 命名");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <param name=\"alias\">别名</param>");
        sb.AppendLine("    public class Named(string? alias = null)");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// 实例");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static Named Instance { get; } = new();");
        sb.AppendLine();
        sb.AppendLine("        private readonly string? _alias = alias;");
        sb.AppendLine();
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// 通配符");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public string Any => string.IsNullOrWhiteSpace(_alias) ? \"*\" : $\"{_alias}.*\";");
        sb.AppendLine();

        foreach (var property in info.Properties)
        {
            var displayName = GetDisplayName(property.PropertyName);
            sb.AppendLine("        /// <summary>");
            sb.AppendLine($"        /// {displayName}");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine($"        public string {property.PropertyName} => string.IsNullOrWhiteSpace(_alias) ? \"{property.ColumnName}\" : $\"{{_alias}}.{property.ColumnName}\";");
            sb.AppendLine();
        }

        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// 字符串表示");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        /// <returns></returns>");
        sb.AppendLine("        public override string ToString()");
        sb.AppendLine("        {");
        sb.AppendLine($"            return string.IsNullOrWhiteSpace(_alias) ? \"{info.TableName}\" : _alias;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// 定义");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    public class Defined()");
        sb.AppendLine("    {");
        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// 实例");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        public static Defined Instance { get; } = new();");
        sb.AppendLine();

        foreach (var property in info.Properties)
        {
            var displayName = GetDisplayName(property.PropertyName);
            sb.AppendLine("        /// <summary>");
            sb.AppendLine($"        /// {displayName}");
            sb.AppendLine("        /// </summary>");
            sb.AppendLine($"        public string {property.PropertyName} {{ get; }} = nameof({property.PropertyName});");
            sb.AppendLine();
        }

        sb.AppendLine("        /// <summary>");
        sb.AppendLine("        /// 字符串表示");
        sb.AppendLine("        /// </summary>");
        sb.AppendLine("        /// <returns></returns>");
        sb.AppendLine("        public override string ToString()");
        sb.AppendLine("        {");
        sb.AppendLine($"            return \"{info.ClassName}\";");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// 获取显示名称
    /// </summary>
    private static string GetDisplayName(string propertyName)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < propertyName.Length; i++)
        {
            var c = propertyName[i];
            if (char.IsUpper(c) && i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 属性信息
    /// </summary>
    private sealed class PropertyInfo
    {
        public string PropertyName { get; }
        public string ColumnName { get; }

        public PropertyInfo(string propertyName, string columnName)
        {
            PropertyName = propertyName;
            ColumnName = columnName;
        }
    }

    /// <summary>
    /// MiniNamed 信息
    /// </summary>
    private sealed class MiniNamedInfo
    {
        public string ClassName { get; }
        public string NamespaceName { get; }
        public string TableName { get; }
        public List<PropertyInfo> Properties { get; }

        public MiniNamedInfo(string className, string namespaceName, string tableName, List<PropertyInfo> properties)
        {
            ClassName = className;
            NamespaceName = namespaceName;
            TableName = tableName;
            Properties = properties;
        }
    }
}