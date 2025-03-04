﻿using Microsoft.CodeAnalysis;
using PolyType.Roslyn;
using PolyType.SourceGenerator.Helpers;
using PolyType.SourceGenerator.Model;
using System.Diagnostics;

namespace PolyType.SourceGenerator;

public sealed partial class Parser
{
    private TypeShapeModel MapModel(TypeDataModel model, TypeId typeId, string sourceIdentifier)
    {
        TypeShapeModel incrementalModel = MapModelCore(model, typeId, sourceIdentifier);
        return model.DerivedTypes is [] ? incrementalModel : MapUnionModel(model, incrementalModel);
    }

    private TypeShapeModel MapModelCore(TypeDataModel model, TypeId typeId, string sourceIdentifier, bool isFSharpUnionCase = false)
    {
        return model switch
        {
            EnumDataModel enumModel => new EnumShapeModel
            {
                Type = typeId,
                SourceIdentifier = sourceIdentifier,
                UnderlyingType = CreateTypeId(enumModel.UnderlyingType),
            },

            OptionalDataModel optionalModel => new OptionalShapeModel
            {
                Type = typeId,
                Kind = optionalModel.Type switch
                {
                    { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } => OptionalKind.NullableOfT,
                    { Name: "FSharpOption" } => OptionalKind.FSharpOption,
                    { Name: "FSharpValueOption" } => OptionalKind.FSharpValueOption,
                    _ => throw new InvalidOperationException(),
                },
                SourceIdentifier = sourceIdentifier,
                ElementType = CreateTypeId(optionalModel.ElementType),
            },
            
            SurrogateTypeDataModel surrogateModel => new SurrogateShapeModel
            {
                Type = typeId,
                SourceIdentifier = sourceIdentifier,
                SurrogateType = CreateTypeId(surrogateModel.SurrogateType),
                MarshallerType = CreateTypeId(surrogateModel.MarshallerType),
            },

            EnumerableDataModel enumerableModel => new EnumerableShapeModel
            {
                Type = typeId,
                SourceIdentifier = sourceIdentifier,
                ElementType = CreateTypeId(enumerableModel.ElementType),
                ConstructionStrategy = enumerableModel.ConstructionStrategy switch
                {
                    _ when enumerableModel.EnumerableKind is EnumerableKind.ArrayOfT or EnumerableKind.MemoryOfT or EnumerableKind.ReadOnlyMemoryOfT
                        => CollectionConstructionStrategy.Span, // use ReadOnlySpan.ToArray() to create the collection

                    CollectionModelConstructionStrategy.Mutable => CollectionConstructionStrategy.Mutable,
                    CollectionModelConstructionStrategy.Span => CollectionConstructionStrategy.Span,
                    CollectionModelConstructionStrategy.List => 
                        IsFactoryAcceptingIEnumerable(enumerableModel.FactoryMethod) 
                        ? CollectionConstructionStrategy.Enumerable 
                        : CollectionConstructionStrategy.Span,

                    _ => CollectionConstructionStrategy.None,
                },

                AddElementMethod = enumerableModel.AddElementMethod?.Name,
                ImplementationTypeFQN = 
                    enumerableModel.ConstructionStrategy is CollectionModelConstructionStrategy.Mutable &&
                    enumerableModel.FactoryMethod is { IsStatic: false, ContainingType: INamedTypeSymbol implType } && 
                    !SymbolEqualityComparer.Default.Equals(implType, enumerableModel.Type)

                    ? implType.GetFullyQualifiedName()
                    : null,

                StaticFactoryMethod = enumerableModel.FactoryMethod is { IsStatic: true } m ? m.GetFullyQualifiedName() : null,
                CtorRequiresListConversion = 
                    enumerableModel.ConstructionStrategy is CollectionModelConstructionStrategy.List &&
                    !IsFactoryAcceptingIEnumerable(enumerableModel.FactoryMethod),

                Kind = enumerableModel.EnumerableKind,
                Rank = enumerableModel.Rank,
                ElementTypeContainsNullableAnnotations = enumerableModel.ElementType.ContainsNullabilityAnnotations(),
            },

            DictionaryDataModel dictionaryModel => new DictionaryShapeModel
            {
                Type = typeId,
                SourceIdentifier = sourceIdentifier,
                KeyType = CreateTypeId(dictionaryModel.KeyType),
                ValueType = CreateTypeId(dictionaryModel.ValueType),
                ConstructionStrategy = dictionaryModel.ConstructionStrategy switch
                {
                    CollectionModelConstructionStrategy.Mutable => CollectionConstructionStrategy.Mutable,
                    CollectionModelConstructionStrategy.Span => CollectionConstructionStrategy.Span,
                    CollectionModelConstructionStrategy.Dictionary =>
                        IsFactoryAcceptingIEnumerable(dictionaryModel.FactoryMethod)
                        ? CollectionConstructionStrategy.Enumerable
                        : CollectionConstructionStrategy.Span,

                    CollectionModelConstructionStrategy.TupleEnumerable => CollectionConstructionStrategy.Enumerable,
                    _ => CollectionConstructionStrategy.None,
                },

                ImplementationTypeFQN =
                    dictionaryModel.ConstructionStrategy is CollectionModelConstructionStrategy.Mutable &&
                    dictionaryModel.FactoryMethod is { IsStatic: false, ContainingType: INamedTypeSymbol implType } &&
                    !SymbolEqualityComparer.Default.Equals(implType, dictionaryModel.Type)

                    ? implType.GetFullyQualifiedName()
                    : null,

                StaticFactoryMethod = dictionaryModel.FactoryMethod is { IsStatic: true } m ? m.GetFullyQualifiedName() : null,
                IsTupleEnumerableFactory = dictionaryModel.ConstructionStrategy is CollectionModelConstructionStrategy.TupleEnumerable,
                Kind = dictionaryModel.DictionaryKind,
                CtorRequiresDictionaryConversion =
                    dictionaryModel.ConstructionStrategy is CollectionModelConstructionStrategy.Dictionary &&
                    !IsFactoryAcceptingIEnumerable(dictionaryModel.FactoryMethod),
                KeyValueTypesContainNullableAnnotations = 
                    dictionaryModel.KeyType.ContainsNullabilityAnnotations() ||
                    dictionaryModel.ValueType.ContainsNullabilityAnnotations(),
            },

            ObjectDataModel objectModel => new ObjectShapeModel
            {
                Type = typeId,
                SourceIdentifier = sourceIdentifier,
                Constructor = objectModel.Constructors
                    .Select(c => MapConstructor(objectModel, typeId, c, isFSharpUnionCase))
                    .FirstOrDefault(),

                Properties = objectModel.Properties
                    .Select(p => MapProperty(model.Type, typeId, p))
                    .OrderBy(p => p.Order)
                    .ToImmutableEquatableArray(),

                IsValueTupleType = false,
                IsTupleType = false,
                IsRecordType = model.Type.IsRecord,
            },

            TupleDataModel tupleModel => new ObjectShapeModel
            {
                Type = typeId,
                SourceIdentifier = sourceIdentifier,
                Constructor = MapTupleConstructor(typeId, tupleModel),
                Properties = tupleModel.Elements
                    .Select((e, i) => MapProperty(model.Type, typeId, e, tupleElementIndex: i, isClassTupleType: !tupleModel.IsValueTuple))
                    .ToImmutableEquatableArray(),

                IsValueTupleType = tupleModel.IsValueTuple,
                IsTupleType = true,
                IsRecordType = false,
            },

            FSharpUnionDataModel unionModel => new FSharpUnionShapeModel
            {
                Type = typeId,
                SourceIdentifier = sourceIdentifier,
                TagReader = unionModel.TagReader switch
                {
                    { MethodKind: MethodKind.PropertyGet } tagReader => tagReader.AssociatedSymbol!.Name,
                    var tagReader => tagReader.GetFullyQualifiedName(),
                },

                TagReaderIsMethod = unionModel.TagReader.MethodKind is not MethodKind.PropertyGet,
                UnderlyingModel = new ObjectShapeModel
                { 
                    Type = typeId,
                    Constructor = null,
                    Properties = [],
                    SourceIdentifier = sourceIdentifier + "__Underlying",
                    IsValueTupleType = false,
                    IsTupleType = false,
                    IsRecordType = false,
                },
                UnionCases = unionModel.UnionCases
                    .Select(unionCase => new FSharpUnionCaseShapeModel(
                        Name: unionCase.Name, 
                        Tag: unionCase.Tag, 
                        TypeModel: MapModelCore(unionCase.Type, CreateTypeId(unionCase.Type.Type), $"{sourceIdentifier}__Case_{unionCase.Name}", isFSharpUnionCase: true)))
                    .ToImmutableEquatableArray(),
            },

            _ => new ObjectShapeModel
            { 
                Type = typeId,
                SourceIdentifier = sourceIdentifier,
                Constructor = null,
                Properties = [],
                IsValueTupleType = false,
                IsTupleType = false,
                IsRecordType = false,
            }
        };

        static bool IsFactoryAcceptingIEnumerable(IMethodSymbol? method)
        {
            return method?.Parameters is [{ Type: INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Collections_Generic_IEnumerable_T } }];
        }
    }

    private static UnionShapeModel MapUnionModel(TypeDataModel model, TypeShapeModel underlyingIncrementalModel)
    {
        Debug.Assert(model.DerivedTypes.Length > 0);

        return new UnionShapeModel
        {
            Type = underlyingIncrementalModel.Type,
            SourceIdentifier = underlyingIncrementalModel.SourceIdentifier,
            UnderlyingModel = underlyingIncrementalModel with
            {
                SourceIdentifier = underlyingIncrementalModel.SourceIdentifier + "__Underlying",
            },

            UnionCases = model.DerivedTypes
                .Select(derived => new UnionCaseModel
                {
                    Type = CreateTypeId(derived.Type),
                    Name = derived.Name,
                    Tag = derived.Tag,
                    IsTagSpecified = derived.IsTagSpecified,
                    Index = derived.Index,
                    IsBaseType = derived.IsBaseType,
                })
                .ToImmutableEquatableArray(),
        };
    }

    private PropertyShapeModel MapProperty(ITypeSymbol parentType, TypeId parentTypeId, PropertyDataModel property, bool isClassTupleType = false, int tupleElementIndex = -1)
    {
        ParsePropertyShapeAttribute(property.PropertySymbol, out string propertyName, out int order);

        bool emitGetter = property.IncludeGetter;
        bool emitSetter = property.IncludeSetter && !property.IsInitOnly;
        
        return new PropertyShapeModel
        {
            Name = isClassTupleType ? $"Item{tupleElementIndex + 1}" : propertyName ?? property.Name,
            UnderlyingMemberName = isClassTupleType
                ? $"{string.Join("", Enumerable.Repeat("Rest.", tupleElementIndex / 7))}Item{(tupleElementIndex % 7) + 1}"
                : property.Name,

            DeclaringType = SymbolEqualityComparer.Default.Equals(parentType, property.DeclaringType) ? parentTypeId : CreateTypeId(property.DeclaringType),
            CanUseUnsafeAccessors = _knownSymbols.TargetFramework switch
            {
                // .NET 8 or later supports unsafe accessors for properties of non-generic types.
                var target when target >= TargetFramework.Net80 => !property.DeclaringType.IsGenericType,
                _ => false
            },

            PropertyType = CreateTypeId(property.PropertyType),
            IsGetterNonNullable = emitGetter && property.IsGetterNonNullable,
            IsSetterNonNullable = emitSetter && property.IsSetterNonNullable,
            PropertyTypeContainsNullabilityAnnotations = property.PropertyType.ContainsNullabilityAnnotations(),
            EmitGetter = emitGetter,
            EmitSetter = emitSetter,
            IsGetterAccessible = property.IsGetterAccessible,
            IsSetterAccessible = property.IsSetterAccessible,
            IsGetterPublic = emitGetter && property.BaseSymbol is IPropertySymbol { GetMethod.DeclaredAccessibility: Accessibility.Public } or IFieldSymbol { DeclaredAccessibility: Accessibility.Public },
            IsSetterPublic = emitSetter && property.BaseSymbol is IPropertySymbol { SetMethod.DeclaredAccessibility: Accessibility.Public } or IFieldSymbol { DeclaredAccessibility: Accessibility.Public },
            IsInitOnly = property.IsInitOnly,
            IsField = property.IsField,
            Order = order,
        };
    }

    private ConstructorShapeModel MapConstructor(ObjectDataModel objectModel, TypeId declaringTypeId, ConstructorDataModel constructor, bool isFSharpUnionCase)
    {
        int position = constructor.Parameters.Length;
        List<ConstructorParameterShapeModel>? requiredMembers = null;
        List<ConstructorParameterShapeModel>? optionalMembers = null;
        
        bool isAccessibleConstructor = IsAccessibleSymbol(constructor.Constructor);
        bool isParameterizedConstructor = position > 0 || constructor.MemberInitializers.Any(p => p.IsRequired || p.IsInitOnly);
        IEnumerable<PropertyDataModel> memberInitializers = isParameterizedConstructor
            // Include all settable members but process required members first.
            ? constructor.MemberInitializers.OrderByDescending(p => p.IsRequired)
            // Do not include any member initializers in parameterless constructors.
            : [];

        foreach (PropertyDataModel propertyModel in memberInitializers)
        {
            ParsePropertyShapeAttribute(propertyModel.PropertySymbol, out string propertyName, out _);

            var memberInitializer = new ConstructorParameterShapeModel
            {
                ParameterType = CreateTypeId(propertyModel.PropertyType),
                DeclaringType = SymbolEqualityComparer.Default.Equals(propertyModel.DeclaringType, objectModel.Type)
                    ? declaringTypeId
                    : CreateTypeId(propertyModel.DeclaringType),

                Name = propertyName,
                UnderlyingMemberName = propertyModel.Name,
                Position = position++,
                IsRequired = propertyModel.IsRequired,
                IsAccessible = propertyModel.IsSetterAccessible,
                CanUseUnsafeAccessors = _knownSymbols.TargetFramework switch
                {
                    // .NET 8 or later supports unsafe accessors for properties of non-generic types.
                    var target when target >= TargetFramework.Net80 => !propertyModel.DeclaringType.IsGenericType,
                    _ => false
                },
                IsInitOnlyProperty = propertyModel.IsInitOnly,
                Kind = propertyModel.IsRequired ? ParameterKind.RequiredMember : ParameterKind.OptionalMember,
                RefKind = RefKind.None,
                IsNonNullable = propertyModel.IsSetterNonNullable,
                ParameterTypeContainsNullabilityAnnotations = propertyModel.PropertyType.ContainsNullabilityAnnotations(),
                IsPublic = propertyModel.PropertySymbol.DeclaredAccessibility is Accessibility.Public,
                IsField = propertyModel.IsField,
                HasDefaultValue = false,
                DefaultValueExpr = null,
            };

            if (memberInitializer.Kind is ParameterKind.RequiredMember)
            {
                // Member must be set using an object initializer expression
                (requiredMembers ??= []).Add(memberInitializer);
            }
            else
            {
                // Member can be set optionally post construction
                (optionalMembers ??= []).Add(memberInitializer);
            }
        }

        return new ConstructorShapeModel
        {
            DeclaringType = SymbolEqualityComparer.Default.Equals(constructor.DeclaringType, objectModel.Type)
                ? declaringTypeId
                : CreateTypeId(constructor.DeclaringType),

            Parameters = constructor.Parameters.Select(p => MapConstructorParameter(objectModel, declaringTypeId, p, isFSharpUnionCase)).ToImmutableEquatableArray(),
            RequiredMembers = requiredMembers?.ToImmutableEquatableArray() ?? [],
            OptionalMembers = optionalMembers?.ToImmutableEquatableArray() ?? [],
            OptionalMemberFlagsType = (optionalMembers?.Count ?? 0) switch
            {
                0 => OptionalMemberFlagsType.None,
                <= 8 => OptionalMemberFlagsType.Byte,
                <= 16 => OptionalMemberFlagsType.UShort,
                <= 32 => OptionalMemberFlagsType.UInt32,
                <= 64 => OptionalMemberFlagsType.ULong,
                _ => OptionalMemberFlagsType.BitArray,
            },

            StaticFactoryName = constructor.Constructor switch
            {
                { IsStatic: true, MethodKind: MethodKind.PropertyGet } ctor => ctor.AssociatedSymbol!.GetFullyQualifiedName(),
                { IsStatic: true } ctor => ctor.GetFullyQualifiedName(),
                _ => null,
            },
            StaticFactoryIsProperty = constructor.Constructor.MethodKind is MethodKind.PropertyGet,
            ResultRequiresCast = !SymbolEqualityComparer.Default.Equals(constructor.Constructor.ReturnType, objectModel.Type),
            IsPublic = constructor.Constructor.DeclaredAccessibility is Accessibility.Public,
            CanUseUnsafeAccessors = _knownSymbols.TargetFramework switch
            {
                // .NET 8 or later supports unsafe accessors for properties of non-generic types.
                var tfm when tfm >= TargetFramework.Net80 => !constructor.DeclaringType.IsGenericType,
                _ => false,
            },
            IsAccessible = isAccessibleConstructor,
        };
    }

    private ConstructorParameterShapeModel MapConstructorParameter(ObjectDataModel objectModel, TypeId declaringTypeId, ConstructorParameterDataModel parameter, bool isFSharpUnionCase)
    {
        string name = parameter.Parameter.Name;

        AttributeData? parameterAttr = parameter.Parameter.GetAttribute(_knownSymbols.ParameterShapeAttribute);
        if (parameterAttr != null &&
            parameterAttr.TryGetNamedArgument("Name", out string? value) && value != null)
        {
            // Resolve the [ParameterShape] attribute name override
            name = value;
        }
        else if (isFSharpUnionCase && 
                 name.StartsWith("_", StringComparison.Ordinal) &&
                 objectModel.Properties.All(p => p.Name != name))
        {
            name = name[1..];
        }
        else
        {
            foreach (PropertyDataModel property in objectModel.Properties)
            {
                // Match property names to parameters up to Pascal/camel case conversion.
                if (SymbolEqualityComparer.Default.Equals(property.PropertyType, parameter.Parameter.Type) &&
                    CommonHelpers.CamelCaseInvariantComparer.Instance.Equals(parameter.Parameter.Name, property.Name))
                {
                    // We have a matching property, use its name in the parameter.
                    if (property.PropertySymbol.GetAttribute(_knownSymbols.PropertyShapeAttribute) is AttributeData attributeData &&
                        attributeData.TryGetNamedArgument("Name", out string? result) && result != null)
                    {
                        // Resolve the [PropertyShape] attribute name override.
                        name = result;
                    }
                    else
                    {
                        // Use the name of the matching property.
                        name = property.Name;
                    }

                    break;
                }
            }
        }

        return new ConstructorParameterShapeModel
        {
            Name = name,
            UnderlyingMemberName = parameter.Parameter.Name,
            Position = parameter.Parameter.Ordinal,
            DeclaringType = declaringTypeId,
            ParameterType = CreateTypeId(parameter.Parameter.Type),
            Kind = ParameterKind.ConstructorParameter,
            RefKind = parameter.Parameter.RefKind,
            IsRequired = !parameter.HasDefaultValue,
            IsAccessible = true,
            CanUseUnsafeAccessors = false,
            IsInitOnlyProperty = false,
            IsNonNullable = parameter.IsNonNullable,
            ParameterTypeContainsNullabilityAnnotations = parameter.Parameter.Type.ContainsNullabilityAnnotations(),
            IsPublic = true,
            IsField = false,
            HasDefaultValue = parameter.HasDefaultValue,
            DefaultValueExpr = parameter.DefaultValueExpr,
        };
    }

    private static ConstructorShapeModel MapTupleConstructor(TypeId typeId, TupleDataModel tupleModel)
    {
        if (tupleModel.IsValueTuple)
        {
            // Return the default constructor for value tuples
            return new ConstructorShapeModel
            {
                DeclaringType = typeId,
                Parameters = [],
                RequiredMembers = [],
                OptionalMembers = [],
                OptionalMemberFlagsType = OptionalMemberFlagsType.None,
                StaticFactoryName = null,
                StaticFactoryIsProperty = false,
                ResultRequiresCast = false,
                IsAccessible = true,
                CanUseUnsafeAccessors = false,
                IsPublic = true,
            };
        }
        else
        {
            // Return the parameterized constructor for object tuples
            return new ConstructorShapeModel
            {
                DeclaringType = typeId,
                Parameters = tupleModel.Elements.Select((p, i) => MapTupleConstructorParameter(typeId, p, i)).ToImmutableEquatableArray(),
                RequiredMembers = [],
                OptionalMembers = [],
                OptionalMemberFlagsType = OptionalMemberFlagsType.None,
                StaticFactoryName = null,
                StaticFactoryIsProperty = false,
                ResultRequiresCast = false,
                IsAccessible = true,
                CanUseUnsafeAccessors = false,
                IsPublic = true,
            };   
        }

        static ConstructorParameterShapeModel MapTupleConstructorParameter(TypeId typeId, PropertyDataModel tupleElement, int position)
        {
            string name = $"Item{position + 1}";
            return new ConstructorParameterShapeModel
            {
                Name = name,
                UnderlyingMemberName = name,
                Position = position,
                ParameterType = CreateTypeId(tupleElement.PropertyType),
                DeclaringType = typeId,
                HasDefaultValue = false,
                Kind = ParameterKind.ConstructorParameter,
                RefKind = RefKind.None,
                IsRequired = true,
                IsAccessible = true,
                CanUseUnsafeAccessors = false,
                IsInitOnlyProperty = false,
                IsPublic = true,
                IsField = true,
                IsNonNullable = tupleElement.IsSetterNonNullable,
                ParameterTypeContainsNullabilityAnnotations = tupleElement.PropertyType.ContainsNullabilityAnnotations(),
                DefaultValueExpr = null,
            };
        }
    }

    private void ParseTypeShapeAttribute(
        ITypeSymbol typeSymbol,
        out TypeShapeKind? kind,
        out ITypeSymbol? marshaller,
        out Location? location)
    {
        kind = null;
        marshaller = null;
        location = null;

        if (typeSymbol.GetAttribute(_knownSymbols.TypeShapeAttribute) is AttributeData propertyAttr)
        {
            location = propertyAttr.GetLocation();
            foreach (KeyValuePair<string, TypedConstant> namedArgument in propertyAttr.NamedArguments)
            {
                switch (namedArgument.Key)
                {
                    case "Kind":
                        kind = (TypeShapeKind)namedArgument.Value.Value!;
                        break;
                    case "Marshaller":
                        marshaller = namedArgument.Value.Value as ITypeSymbol;
                        break;
                }
            }
        }
    }

    private static TypeDataKind? MapTypeShapeKindToDataKind(TypeShapeKind? kind)
    {
        Debug.Assert(kind is not TypeShapeKind.Surrogate);
        return kind switch
        {
            null => null,
            TypeShapeKind.Enum => TypeDataKind.Enum,
            TypeShapeKind.Optional => TypeDataKind.Optional,
            TypeShapeKind.Enumerable => TypeDataKind.Enumerable,
            TypeShapeKind.Dictionary => TypeDataKind.Dictionary,
            TypeShapeKind.Object => TypeDataKind.Object,
            _ => TypeDataKind.None,
        };
    }

    private void ParsePropertyShapeAttribute(ISymbol propertySymbol, out string propertyName, out int order)
    {
        propertyName = propertySymbol.Name;
        order = 0;

        if (propertySymbol.GetAttribute(_knownSymbols.PropertyShapeAttribute) is AttributeData propertyAttr)
        {
            foreach (KeyValuePair<string, TypedConstant> namedArgument in propertyAttr.NamedArguments)
            {
                switch (namedArgument.Key)
                {
                    case "Name":
                        propertyName = (string?)namedArgument.Value.Value ?? propertyName;
                        break;
                    case "Order":
                        order = (int)namedArgument.Value.Value!;
                        break;
                }
            }
        }
    }
}