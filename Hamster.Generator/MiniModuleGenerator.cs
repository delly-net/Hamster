using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Hamster.Generator;

[Generator(LanguageNames.CSharp)]
internal sealed class MiniModuleGenerator : IIncrementalGenerator
{
    private const string MINI_MODULE_ATTRIBUTE = "Hamster.Attributing.MiniModuleAttribute";
    private const string MINI_GROUP_ATTRIBUTE = "Hamster.Attributing.MiniGroupAttribute";

    /// <summary>
    /// 初始化生成器
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var miniModuleProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                MINI_MODULE_ATTRIBUTE,
                predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, _) => GetMiniModuleInfo(ctx))
            .Where(static m => m is not null);

        var miniGroupProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                MINI_GROUP_ATTRIBUTE,
                predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, _) => GetMiniGroupInfo(ctx))
            .Where(static m => m is not null);

        var combined = miniModuleProvider.Collect().Combine(miniGroupProvider.Collect());

        context.RegisterSourceOutput(combined, static (spc, source) => Execute(spc, source.Left, source.Right));
    }

    /// <summary>
    /// 获取 MiniModule 信息
    /// </summary>
    private static MiniModuleInfo? GetMiniModuleInfo(GeneratorAttributeSyntaxContext context)
    {
        var classSymbol = (INamedTypeSymbol)context.TargetSymbol;
        var className = classSymbol.Name;
        var namespaceName = classSymbol.ContainingNamespace.ToString();
        var attribute = context.Attributes.First();

        var rule = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string
            : null;

        var template = attribute.ConstructorArguments.Length > 1
            ? attribute.ConstructorArguments[1].Value as string
            : null;

        if (rule is null)
        {
            return null;
        }

        return new MiniModuleInfo(className, namespaceName, template, rule);
    }

    /// <summary>
    /// 获取 MiniGroup 信息
    /// </summary>
    private static MiniGroupInfo? GetMiniGroupInfo(GeneratorAttributeSyntaxContext context)
    {
        var classSymbol = (INamedTypeSymbol)context.TargetSymbol;
        var className = classSymbol.Name;
        var namespaceName = classSymbol.ContainingNamespace.ToString();

        return new MiniGroupInfo(className, namespaceName);
    }

    /// <summary>
    /// 执行生成
    /// </summary>
    private static void Execute(
        SourceProductionContext context,
        ImmutableArray<MiniModuleInfo?> miniModules,
        ImmutableArray<MiniGroupInfo?> miniGroups)
    {
        var validMiniModules = miniModules.Where(m => m is not null).ToImmutableArray();
        var validMiniGroups = miniGroups.Where(m => m is not null).ToImmutableArray();

        foreach (var miniModuleInfo in validMiniModules)
        {
            var filteredGroups = FilterGroupsByRule(validMiniGroups, miniModuleInfo!.Rule);

            if (filteredGroups.Count > 0)
            {
                var source = GenerateSource(miniModuleInfo!, filteredGroups);
                context.AddSource($"{miniModuleInfo!.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        }
    }

    /// <summary>
    /// 根据规则筛选组
    /// </summary>
    private static List<MiniGroupInfo> FilterGroupsByRule(
        ImmutableArray<MiniGroupInfo?> miniGroups,
        string rule)
    {
        var result = new List<MiniGroupInfo>();

        foreach (var miniGroup in miniGroups)
        {
            if (miniGroup is null)
            {
                continue;
            }

            var matchString = $"{miniGroup.NamespaceName}.{miniGroup.ClassName}";

            if (IsMatch(matchString, rule))
            {
                result.Add(miniGroup);
            }
        }

        return result;
    }

    /// <summary>
    /// 判断是否匹配
    /// </summary>
    /// <param name="matchString">待匹配字符串，格式为"命名空间.类名"</param>
    /// <param name="rule">匹配规则</param>
    /// <returns>是否匹配</returns>
    private static bool IsMatch(string matchString, string rule)
    {
        if (string.IsNullOrEmpty(rule))
        {
            return true;
        }

        var pattern = rule
            .Replace(".", "\\.")
            .Replace("*", ".*")
            .Replace("**", "(\\..*)*");

        var regex = new Regex($"^{pattern}$", RegexOptions.Singleline);
        return regex.IsMatch(matchString);
    }

    /// <summary>
    /// 生成源代码
    /// </summary>
    private static string GenerateSource(MiniModuleInfo miniModuleInfo, List<MiniGroupInfo> groups)
    {
        var sb = new StringBuilder();

        var usingNamespaces = groups.Select(g => g.NamespaceName).Distinct().ToList();

        sb.AppendLine("using Hamster.Core;");

        foreach (var usingNamespace in usingNamespaces)
        {
            sb.AppendLine($"using {usingNamespace};");
        }

        sb.AppendLine();
        sb.AppendLine($"namespace {miniModuleInfo.NamespaceName};");
        sb.AppendLine();

        sb.AppendLine($"public partial class {miniModuleInfo.ClassName} : IMiniModule");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// 映射");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <param name=\"routeBuilder\"></param>");
        sb.AppendLine("    public void Map(IEndpointRouteBuilder routeBuilder)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var group = routeBuilder.MapGroup(\"{miniModuleInfo.Template}\");");

        foreach (var group in groups)
        {
            sb.AppendLine($"        group.MapMiniGroup<{group.ClassName}>();");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// MiniModule 信息
    /// </summary>
    private sealed class MiniModuleInfo
    {
        public string ClassName { get; }
        public string NamespaceName { get; }
        public string Template { get; }
        public string Rule { get; }

        public MiniModuleInfo(string className, string namespaceName, string? template, string rule)
        {
            ClassName = className;
            NamespaceName = namespaceName;
            Template = template ?? $"/{ToKebabCase(className.Replace("Module", ""))}";
            Rule = rule;
        }
    }

    /// <summary>
    /// MiniGroup 信息
    /// </summary>
    private sealed class MiniGroupInfo
    {
        public string ClassName { get; }
        public string NamespaceName { get; }

        public MiniGroupInfo(string className, string namespaceName)
        {
            ClassName = className;
            NamespaceName = namespaceName;
        }
    }

    /// <summary>
    /// 转换为 kebab-case
    /// </summary>
    private static string ToKebabCase(string input)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c) && i > 0)
            {
                sb.Append('-');
            }
            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}