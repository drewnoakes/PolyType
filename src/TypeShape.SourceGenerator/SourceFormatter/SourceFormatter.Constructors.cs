﻿using System.Diagnostics;
using TypeShape.SourceGenerator.Helpers;
using TypeShape.SourceGenerator.Model;

namespace TypeShape.SourceGenerator;

internal static partial class SourceFormatter
{
    private static void FormatConstructorFactory(SourceWriter writer, string methodName, TypeModel type)
    {
        Debug.Assert(type.Constructors.Count > 0);

        writer.WriteLine($"private global::System.Collections.Generic.IEnumerable<global::TypeShape.IConstructor> {methodName}()");
        writer.WriteStartBlock();

        int i = 0;
        var argumentStateFQNs = new List<string>();
        foreach (ConstructorModel constructor in type.Constructors)
        {
            if (i > 0)
                writer.WriteLine();

            string constructorArgumentStateFQN = FormatConstructorArgumentStateFQN(constructor);
            argumentStateFQNs.Add(constructorArgumentStateFQN);

            writer.WriteLine($$"""
                yield return new global::TypeShape.SourceGenModel.SourceGenConstructor<{{type.Id.FullyQualifiedName}}, {{constructorArgumentStateFQN}}>
                {
                    DeclaringType = {{constructor.DeclaringType.GeneratedPropertyName}},
                    ParameterCount = {{constructor.TotalArity}},
                    GetParametersFunc = {{(constructor.TotalArity == 0 ? "null" : FormatConstructorParameterFactoryName(type, i))}},
                    DefaultConstructorFunc = {{FormatDefaultCtor(constructor)}},
                    ArgumentStateConstructorFunc = static () => {{FormatArgumentStateCtorExpr(constructor)}},
                    ParameterizedConstructorFunc = static state => {{FormatParameterizedCtorExpr(constructor, "state")}},
                    AttributeProviderFunc = {{FormatAttributeProviderFunc(constructor)}},
                };
                """);

            i++;

            static string FormatAttributeProviderFunc(ConstructorModel constructor)
            {
                string parameterTypes = constructor.Parameters.Count == 0
                    ? "Array.Empty<Type>()"
                    : $$"""new[] { {{string.Join(", ", constructor.Parameters.Select(p => $"typeof({p.ParameterType.FullyQualifiedName})"))}} }""";

                return $"static () => typeof({constructor.DeclaringType.FullyQualifiedName}).GetConstructor({InstanceBindingFlagsConstMember}, {parameterTypes})";
            }

            static string FormatArgumentStateCtorExpr(ConstructorModel constructor)
                => (constructor.Parameters.Count, constructor.MemberInitializers.Count) switch
                {
                    (0, 0) => "null",
                    (1, 0) => FormatDefaultValueExpr(constructor.Parameters[0]),
                    (0, 1) => FormatDefaultValueExpr(constructor.MemberInitializers[0]),
                    (_, _) => $"({string.Join(", ", constructor.Parameters.Concat(constructor.MemberInitializers).Select(FormatDefaultValueExpr))})"
                };

            static string FormatParameterizedCtorExpr(ConstructorModel constructor, string stateVar)
            {
                return (constructor.Parameters.Count, constructor.MemberInitializers.Count) switch
                {
                    (0, 0) => $$"""new {{constructor.DeclaringType.FullyQualifiedName}}()""",
                    (1, 0) => $$"""new {{constructor.DeclaringType.FullyQualifiedName}}({{stateVar}})""",
                    (0, 1) => $$"""new {{constructor.DeclaringType.FullyQualifiedName}} { {{constructor.MemberInitializers[0].Name}} = {{stateVar}} }""",
                    (_, 0) => $$"""new {{constructor.DeclaringType.FullyQualifiedName}}({{FormatCtorArgumentsBody()}})""",
                    (0, _) => $$"""new {{constructor.DeclaringType.FullyQualifiedName}} { {{FormatInitializerBody()}} }""",
                    (_, _) => $$"""new {{constructor.DeclaringType.FullyQualifiedName}}({{FormatCtorArgumentsBody()}}) { {{FormatInitializerBody()}} }""",
                };

                string FormatCtorArgumentsBody() => string.Join(", ", constructor.Parameters.Select(p => $"state.Item{p.Position + 1}"));
                string FormatInitializerBody() => string.Join(", ", constructor.MemberInitializers.Select(p => $"{p.Name} = state.Item{p.Position + 1}"));
            }

            static string FormatDefaultCtor(ConstructorModel constructor)
                => constructor.TotalArity switch
                {
                    0 => $"static () => new {constructor.DeclaringType.FullyQualifiedName}()",
                    _ => "null",
                };
        }

        writer.WriteEndBlock();

        i = 0;
        foreach (ConstructorModel constructor in type.Constructors)
        {
            if (constructor.TotalArity > 0)
            {
                writer.WriteLine();
                FormatConstructorParameterFactory(writer, FormatConstructorParameterFactoryName(type, i), constructor, argumentStateFQNs[i]);
            }

            i++;
        }

        static string FormatConstructorParameterFactoryName(TypeModel type, int constructorIndex) =>
            $"CreateConstructorParameters_{type.Id.GeneratedPropertyName}_{constructorIndex}";
    }

    private static void FormatConstructorParameterFactory(SourceWriter writer, string methodName, ConstructorModel constructor, string constructorArgumentStateFQN)
    {
        writer.WriteLine($"private global::System.Collections.Generic.IEnumerable<global::TypeShape.IConstructorParameter> {methodName}()");
        writer.WriteStartBlock();

        int i = 0;
        foreach (ConstructorParameterModel parameter in constructor.Parameters.Concat(constructor.MemberInitializers))
        {
            if (i > 0)
                writer.WriteLine();

            writer.WriteLine($$"""
                yield return new global::TypeShape.SourceGenModel.SourceGenConstructorParameter<{{constructorArgumentStateFQN}}, {{parameter.ParameterType.FullyQualifiedName}}>
                {
                    Position = {{parameter.Position}},
                    Name = "{{parameter.Name}}",
                    ParameterType = {{parameter.ParameterType.GeneratedPropertyName}},
                    IsRequired = {{FormatBool(parameter.IsRequired)}},
                    HasDefaultValue = {{FormatBool(parameter.HasDefaultValue)}},
                    DefaultValue = {{FormatDefaultValueExpr(parameter)}},
                    Setter = static (ref {{constructorArgumentStateFQN}} state, {{parameter.ParameterType.FullyQualifiedName}} value) => state = {{FormatSetterBody(constructor, parameter)}},
                    AttributeProviderFunc = {{FormatAttributeProviderFunc(constructor, parameter)}},
                };
                """);

            i++;

            static string FormatAttributeProviderFunc(ConstructorModel constructor, ConstructorParameterModel parameter)
            {
                if (parameter.IsMemberInitializer)
                {
                    return $"static () => typeof({constructor.DeclaringType.FullyQualifiedName}).GetMember(\"{parameter.Name}\", {InstanceBindingFlagsConstMember})[0]";
                }

                string parameterTypes = constructor.Parameters.Count == 0
                    ? "Array.Empty<Type>()"
                    : $$"""new[] { {{string.Join(", ", constructor.Parameters.Select(p => $"typeof({p.ParameterType.FullyQualifiedName})"))}} }""";

                return $"static () => typeof({constructor.DeclaringType.FullyQualifiedName}).GetConstructor({InstanceBindingFlagsConstMember}, {parameterTypes})?.GetParameters()[{parameter.Position}]";
            }

            static string FormatSetterBody(ConstructorModel constructor, ConstructorParameterModel parameter)
                => constructor.TotalArity switch
                {
                    1 => "value",
                    _ => $"({string.Join(", ", constructor.Parameters.Concat(constructor.MemberInitializers).Select(p => p.Position == parameter.Position ? "value" : $"state.Item{p.Position + 1}"))})",
                };
        }

        writer.WriteEndBlock();
    }

    private static string FormatDefaultValueExpr(ConstructorParameterModel constructorParameter)
    {
        if (!constructorParameter.HasDefaultValue)
        {
            return $"default({constructorParameter.ParameterType.FullyQualifiedName})";
        }

        string literalExpr = constructorParameter.DefaultValue switch
        {
            null => "null",
            false => "false",
            true => "true",
            string s => $"\"{s}\"",
            char c => $"'{c}'",
            float f => $"{f}f",
            decimal d => $"{d}M",
            object val => val.ToString(),
        };

        return constructorParameter.DefaultValueRequiresCast 
            ? $"({constructorParameter.ParameterType.FullyQualifiedName}){literalExpr}" 
            : literalExpr;
    }

    private static string FormatConstructorArgumentStateFQN(ConstructorModel constructorModel)
    {
        return (constructorModel.Parameters.Count, constructorModel.MemberInitializers.Count) switch
        {
            (0, 0) => "object?",
            (1, 0) => constructorModel.Parameters[0].ParameterType.FullyQualifiedName,
            (0, 1) => constructorModel.MemberInitializers[0].ParameterType.FullyQualifiedName,
            _ => $"({string.Join(", ", constructorModel.Parameters.Concat(constructorModel.MemberInitializers).Select(p => p.ParameterType.FullyQualifiedName))})",
        };
    }
}
