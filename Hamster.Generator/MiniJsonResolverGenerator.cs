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
internal sealed class MiniJsonResolverGenerator : IIncrementalGenerator
{
    private const string MINI_JSON_RESOLVER_ATTRIBUTE = "Hamster.Attributing.MiniJsonResolverAttribute";
    private const string MINI_JSON_SERIALIZER_ATTRIBUTE = "Hamster.Attributing.MiniJsonSerializerAttribute";

    /// <summary>
    /// 初始化生成器
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var miniJsonResolverProvider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                MINI_JSON_RESOLVER_ATTRIBUTE,
                predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
                transform: static (ctx, _) => GetResolverInfo(ctx))
            .Where(static m => m is not null);

        var compilation = context.CompilationProvider.Combine(miniJsonResolverProvider.Collect());

        context.RegisterSourceOutput(compilation, static (spc, source) => Execute(spc, source.Left, source.Right));
    }

    /// <summary>
    /// 获取 Resolver 信息
    /// </summary>
    private static ResolverInfo? GetResolverInfo(GeneratorAttributeSyntaxContext context)
    {
        var classSymbol = (INamedTypeSymbol)context.TargetSymbol;
        var className = classSymbol.Name;
        var namespaceName = classSymbol.ContainingNamespace.ToString();

        return new ResolverInfo(className, namespaceName);
    }

    /// <summary>
    /// 执行生成
    /// </summary>
    private static void Execute(SourceProductionContext context, Compilation compilation, ImmutableArray<ResolverInfo?> resolverInfos)
    {
        var serializerInfos = GetSerializerInfos(compilation);

        foreach (var resolverInfo in resolverInfos)
        {
            if (resolverInfo is not null)
            {
                var source = GenerateSource(resolverInfo, serializerInfos);
                context.AddSource($"{resolverInfo.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        }
    }

    /// <summary>
    /// 获取所有 Serializer 信息
    /// </summary>
    private static List<SerializerInfo> GetSerializerInfos(Compilation compilation)
    {
        var result = new List<SerializerInfo>();
        var miniJsonSerializerAttributeSymbol = compilation.GetTypeByMetadataName(MINI_JSON_SERIALIZER_ATTRIBUTE);

        if (miniJsonSerializerAttributeSymbol is null)
        {
            return result;
        }

        foreach (var syntaxTree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);

            var classDeclarations = syntaxTree.GetRoot()
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>();

            foreach (var classDeclaration in classDeclarations)
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classDeclaration);

                if (classSymbol is null)
                {
                    continue;
                }

                var hasSerializerAttribute = classSymbol.GetAttributes()
                    .Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, miniJsonSerializerAttributeSymbol));

                if (hasSerializerAttribute)
                {
                    var className = classSymbol.Name;
                    var namespaceName = classSymbol.ContainingNamespace.ToString();
                    var containingTypeName = classSymbol.ContainingType?.Name;

                    result.Add(new SerializerInfo(className, namespaceName, containingTypeName));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 生成源代码
    /// </summary>
    private static string GenerateSource(ResolverInfo resolverInfo, List<SerializerInfo> serializerInfos)
    {
        var sb = new StringBuilder();

        sb.AppendLine("using Hamster.Attributing;");
        sb.AppendLine("using System.Text.Json.Serialization.Metadata;");
        sb.AppendLine();
        sb.AppendLine($"namespace {resolverInfo.NamespaceName};");
        sb.AppendLine();
        sb.AppendLine($"public partial class {resolverInfo.ClassName}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// 获取 Resolver 集合");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <returns></returns>");
        sb.AppendLine("    public static List<IJsonTypeInfoResolver> GetResolvers()");
        sb.AppendLine("    {");
        sb.AppendLine("        return");
        sb.AppendLine("        [");

        foreach (var serializerInfo in serializerInfos)
        {
            var fullQualifiedName = GetFullQualifiedName(serializerInfo);
            sb.AppendLine($"            {fullQualifiedName},");
        }

        sb.AppendLine("        ];");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// 获取完整限定名
    /// </summary>
    private static string GetFullQualifiedName(SerializerInfo serializerInfo)
    {
        if (string.IsNullOrEmpty(serializerInfo.ContainingTypeName))
        {
            return $"{serializerInfo.NamespaceName}.{serializerInfo.ClassName}.Default";
        }
        else
        {
            return $"{serializerInfo.NamespaceName}.{serializerInfo.ContainingTypeName}.{serializerInfo.ClassName}.Default";
        }
    }

    /// <summary>
    /// Resolver 信息
    /// </summary>
    private sealed class ResolverInfo
    {
        public string ClassName { get; }
        public string NamespaceName { get; }

        public ResolverInfo(string className, string namespaceName)
        {
            ClassName = className;
            NamespaceName = namespaceName;
        }
    }

    /// <summary>
    /// Serializer 信息
    /// </summary>
    private sealed class SerializerInfo
    {
        public string ClassName { get; }
        public string NamespaceName { get; }
        public string? ContainingTypeName { get; }

        public SerializerInfo(string className, string namespaceName, string? containingTypeName)
        {
            ClassName = className;
            NamespaceName = namespaceName;
            ContainingTypeName = containingTypeName;
        }
    }
}