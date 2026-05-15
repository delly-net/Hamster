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
internal sealed class MiniAppGenerator : IIncrementalGenerator
{
    private const string HTTP_METHOD_ATTRIBUTE = "Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute";

    /// <summary>
    /// 初始化生成器
    /// </summary>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var provider = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "Hamster.Attributing.MiniAppAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0, Modifiers.Count: > 0 },
                transform: static (ctx, _) => GetTarget(ctx))
            .Where(static m => m is not null);

        var compilation = context.CompilationProvider.Combine(provider.Collect());

        context.RegisterSourceOutput(compilation, static (spc, source) => Execute(spc, source.Right));
    }

    /// <summary>
    /// 获取目标类信息
    /// </summary>
    private static ClassInfo? GetTarget(GeneratorAttributeSyntaxContext context)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.TargetNode;

        if (!classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword))
        {
            return null;
        }

        var classSymbol = (INamedTypeSymbol)context.TargetSymbol;

        var template = context.Attributes.First().NamedArguments
            .FirstOrDefault(x => x.Key == "Template").Value.Value as string;

        var methodInfos = classSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m =>
                m.MethodKind == MethodKind.Ordinary &&
                !m.IsStatic &&
                !m.IsAbstract &&
                !m.Name.StartsWith("Map") &&
                m.ReturnType.ToString() == "Microsoft.AspNetCore.Http.IResult")
            .SelectMany(m => GetMethodInfos(m, classSymbol))
            .ToList();

        if (methodInfos.Count == 0)
        {
            return null;
        }

        return new ClassInfo(
            classSymbol.Name,
            classSymbol.ContainingNamespace.ToString(),
            template ?? string.Empty,
            methodInfos
        );
    }

    /// <summary>
    /// 获取方法信息列表（支持一个方法多个 HTTP 特性）
    /// </summary>
    private static List<MethodInfo> GetMethodInfos(IMethodSymbol method, INamedTypeSymbol containingType)
    {
        var httpMethodAttributes = method.GetAttributes()
            .Where(a => IsHttpMethodAttribute(a.AttributeClass))
            .ToList();

        if (httpMethodAttributes.Count == 0)
        {
            return new List<MethodInfo>();
        }

        var result = new List<MethodInfo>();

        foreach (var attribute in httpMethodAttributes)
        {
            var httpMethod = GetHttpMethodFromAttribute(attribute);
            var path = GetPathFromAttribute(attribute);

            var parameters = method.Parameters.Select(p => new ParameterInfo(
                p.Type.ToString(),
                p.Name
            )).ToList();

            result.Add(new MethodInfo(method.Name, path, httpMethod, parameters));
        }

        return result;
    }

    /// <summary>
    /// 判断是否为 HTTP 方法特性
    /// </summary>
    private static bool IsHttpMethodAttribute(INamedTypeSymbol? attributeClass)
    {
        if (attributeClass is null)
        {
            return false;
        }

        var current = attributeClass.BaseType;

        while (current is not null)
        {
            if (current.ToDisplayString() == HTTP_METHOD_ATTRIBUTE)
            {
                return true;
            }

            current = current.BaseType;
        }

        return false;
    }

    /// <summary>
    /// 从特性获取 HTTP 方法
    /// </summary>
    private static string GetHttpMethodFromAttribute(AttributeData attribute)
    {
        var constructorArgs = attribute.ConstructorArguments;

        if (constructorArgs.Length > 0)
        {
            var firstArg = constructorArgs[0];

            if (firstArg.Type?.Name == "HttpMethods" && firstArg.Value is not null)
            {
                return GetHttpMethodFromEnum(firstArg.Value!.ToString()!);
            }
        }

        var namedArg = attribute.NamedArguments.FirstOrDefault(x => x.Key == "HttpMethods");
        if (namedArg.Value.Value is not null)
        {
            return GetHttpMethodFromEnum(namedArg.Value.Value!.ToString()!);
        }

        return GetHttpMethodFromTypeName(attribute.AttributeClass?.ToDisplayString() ?? string.Empty);
    }

    /// <summary>
    /// 从枚举值获取 HTTP 方法
    /// </summary>
    private static string GetHttpMethodFromEnum(string enumValue)
    {
        return enumValue switch
        {
            "Get" => "MapGet",
            "Post" => "MapPost",
            "Put" => "MapPut",
            "Delete" => "MapDelete",
            "Patch" => "MapPatch",
            _ => "MapGet"
        };
    }

    /// <summary>
    /// 从特性类型名称获取 HTTP 方法（备用方案）
    /// </summary>
    private static string GetHttpMethodFromTypeName(string typeName)
    {
        return typeName switch
        {
            string t when t.EndsWith("HttpGetAttribute") => "MapGet",
            string t when t.EndsWith("HttpPostAttribute") => "MapPost",
            string t when t.EndsWith("HttpPutAttribute") => "MapPut",
            string t when t.EndsWith("HttpDeleteAttribute") => "MapDelete",
            string t when t.EndsWith("HttpPatchAttribute") => "MapPatch",
            _ => "MapGet"
        };
    }

    /// <summary>
    /// 从特性获取路径
    /// </summary>
    private static string GetPathFromAttribute(AttributeData attribute)
    {
        var constructorArgs = attribute.ConstructorArguments;
        if (constructorArgs.Length > 0 && constructorArgs[0].Value is string path)
        {
            return path;
        }

        var namedArg = attribute.NamedArguments.FirstOrDefault(x => x.Key == "Route");
        if (namedArg.Value.Value is string namedPath)
        {
            return namedPath;
        }

        return string.Empty;
    }

    /// <summary>
    /// 执行生成
    /// </summary>
    private static void Execute(SourceProductionContext context, ImmutableArray<ClassInfo?> classes)
    {
        foreach (var classInfo in classes)
        {
            if (classInfo is not null)
            {
                var source = GenerateSource(classInfo);
                context.AddSource($"{classInfo.ClassName}.g.cs", SourceText.From(source, Encoding.UTF8));
            }
        }
    }

    /// <summary>
    /// 生成源代码
    /// </summary>
    private static string GenerateSource(ClassInfo classInfo)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"namespace {classInfo.Namespace};");
        sb.AppendLine();
        sb.AppendLine("using Hamster.Core;");
        sb.AppendLine("using Microsoft.AspNetCore.Http;");
        sb.AppendLine("using Microsoft.AspNetCore.Builder;");
        sb.AppendLine("using Microsoft.AspNetCore.Routing;");
        sb.AppendLine();

        var className = classInfo.ClassName;
        var template = string.IsNullOrEmpty(classInfo.Template) ? $"/{ToKebabCase(className.Replace("App", ""))}" : classInfo.Template;

        sb.AppendLine($"/// <summary>");
        sb.AppendLine($"/// {className} 控制器");
        sb.AppendLine($"/// </summary>");
        sb.AppendLine($"public sealed partial class {className} : IMiniApp");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// 路由注册");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    /// <param name=\"routeBuilder\"></param>");
        sb.AppendLine("    public void Map(IEndpointRouteBuilder routeBuilder)");
        sb.AppendLine("    {");
        sb.AppendLine($"        var group = routeBuilder.MapGroup(\"{template}\");");

        foreach (var method in classInfo.Methods)
        {
            var path = method.Path;
            var allParameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type} {p.Name}"));
            var args = string.Join(", ", method.Parameters.Select(p => p.Name));

            sb.AppendLine($"        group.{method.HttpMethod}(\"{path}\", ({allParameters}) =>");
            sb.AppendLine("        {");
            sb.AppendLine($"            var controller = new {className}();");
            sb.AppendLine($"            return controller.{method.OriginalName}({args});");
            sb.AppendLine("        });");
        }

        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
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

    /// <summary>
    /// 类信息
    /// </summary>
    private sealed class ClassInfo
    {
        public string ClassName { get; }
        public string Namespace { get; }
        public string? Template { get; }
        public List<MethodInfo> Methods { get; }

        public ClassInfo(string className, string @namespace, string? template, List<MethodInfo> methods)
        {
            ClassName = className;
            Namespace = @namespace;
            Template = template;
            Methods = methods;
        }
    }

    /// <summary>
    /// 方法信息
    /// </summary>
    private sealed class MethodInfo
    {
        public string OriginalName { get; }
        public string Path { get; }
        public string HttpMethod { get; }
        public List<ParameterInfo> Parameters { get; }

        public MethodInfo(string originalName, string path, string httpMethod, List<ParameterInfo> parameters)
        {
            OriginalName = originalName;
            Path = path;
            HttpMethod = httpMethod;
            Parameters = parameters;
        }
    }

    /// <summary>
    /// 参数信息
    /// </summary>
    private sealed class ParameterInfo
    {
        public string Type { get; }
        public string Name { get; }

        public ParameterInfo(string type, string name)
        {
            Type = type;
            Name = name;
        }
    }
}