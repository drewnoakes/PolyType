﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using TypeShape.Roslyn;
using TypeShape.SourceGenerator.Model;

namespace TypeShape.SourceGenerator;

internal static partial class SourceFormatter
{
    private const string InstanceBindingFlagsConstMember = "InstanceBindingFlags";

    public static void FormatProvider(SourceProductionContext context, TypeShapeProviderModel provider)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        context.AddSource($"{provider.Declaration.SourceFilenamePrefix}.g.cs", FormatMainFile(provider));
        context.AddSource($"{provider.Declaration.SourceFilenamePrefix}.ITypeShapeProvider.g.cs", FormatProviderInterfaceImplementation(provider));

        foreach (TypeShapeModel type in provider.ProvidedTypes.Values)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.AddSource($"{provider.Declaration.SourceFilenamePrefix}.{type.Type.GeneratedPropertyName}.g.cs", FormatType(provider, type));
        }

        foreach (TypeDeclarationModel typeDeclaration in provider.GenerateShapeTypes)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            context.AddSource($"{typeDeclaration.SourceFilenamePrefix}.ITypeShapeProviderOfT.g.cs", FormatGenericProviderInterfaceImplementation(typeDeclaration, provider));
        }
    }

    private static SourceText FormatMainFile(TypeShapeProviderModel provider)
    {
        var writer = new SourceWriter();
        StartFormatSourceFile(writer, provider.Declaration);

        writer.WriteLine(provider.Declaration.TypeDeclarationHeader);
        writer.WriteLine('{');
        writer.Indentation++;

        writer.WriteLine($$"""
            private const global::System.Reflection.BindingFlags {{InstanceBindingFlagsConstMember}} = 
                global::System.Reflection.BindingFlags.Public | 
                global::System.Reflection.BindingFlags.NonPublic | 
                global::System.Reflection.BindingFlags.Instance;

            public static {{provider.Declaration.Name}} Default { get; } = new();

            public {{provider.Declaration.Name}}() { }
            """);

        writer.Indentation--;
        writer.WriteLine('}');
        EndFormatSourceFile(writer);

        return writer.ToSourceText();
    }

    private static void StartFormatSourceFile(SourceWriter writer, TypeDeclarationModel typeDeclaration)
    {
        writer.WriteLine("// <auto-generated/>");
        writer.WriteLine();

#if DEBUG
        writer.WriteLine("""
            #nullable enable

            """);
#else
        writer.WriteLine("""
            #nullable enable annotations
            #nullable disable warnings

            """);
#endif
        
        if (typeDeclaration.Namespace is string @namespace)
        {
            writer.WriteLine($"namespace {@namespace}");
            writer.WriteLine('{');
            writer.Indentation++;
        }

        foreach (string containingType in typeDeclaration.ContainingTypes)
        {
            writer.WriteLine(containingType);
            writer.WriteLine('{');
            writer.Indentation++;
        }
    }

    private static void EndFormatSourceFile(SourceWriter writer)
    {
        while (writer.Indentation > 0) 
        {
            writer.Indentation--;
            writer.WriteLine('}');
        }
    }

    private static string FormatBool(bool value) => value ? "true" : "false";
    private static string FormatNull(string? stringExpr) => stringExpr is null ? "null" : stringExpr;
    private static string FormatStringLiteral(string value)
        => SymbolDisplay.FormatLiteral(value, quote: true);
}
