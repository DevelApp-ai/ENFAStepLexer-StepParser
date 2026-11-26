using Mono.Cecil;

namespace DevelApp.PublicApiSurface;

/// <summary>
/// Extracts the canonical public-API surface of a compiled assembly.
/// </summary>
/// <remarks>
/// The surface is the set of externally visible types and members (public,
/// and family / family-or-assembly, i.e. protected) with container,
/// parameter, and return types rendered in a stable canonical text form.
/// Two builds of a package have an identical surface if and only if their
/// extracted line sets are identical. The CD pipeline compares the surface
/// of the build under release against the surface of the last released
/// packages: identical &rarr; patch release, different &rarr; minor release
/// (the public interface changed). Deliberately conservative: renames of
/// generic parameter names, or a const value / default parameter value
/// change, count as interface changes (they are visible to consumers).
/// </remarks>
public static class ApiSurface
{
    /// <summary>
    /// Extracts the sorted canonical public-API surface of an assembly.
    /// </summary>
    /// <param name="assembly">The compiled assembly to inspect.</param>
    /// <param name="typeFilter">
    /// Optional filter; only top-level types for which it returns
    /// <c>true</c> are visited (nested types follow their parents).
    /// </param>
    public static IReadOnlyList<string> Extract(
        AssemblyDefinition assembly,
        Func<TypeDefinition, bool>? typeFilter = null)
    {
        var lines = new List<string>();
        foreach (var module in assembly.Modules)
        {
            foreach (var type in module.Types)
            {
                if (type.Name == "<Module>" || IsCompilerGenerated(type) || !type.IsPublic)
                {
                    continue;
                }

                if (typeFilter is not null && !typeFilter(type))
                {
                    continue;
                }

                Collect(type, lines);
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static void Collect(TypeDefinition type, List<string> lines)
    {
        // The type is reachable here only when it is externally visible.
        lines.Add($"T:{type.FullName}");

        if (type.BaseType is not null
            && type.BaseType.FullName != "System.Object"
            && type.BaseType.FullName != "System.ValueType"
            && type.BaseType.FullName != "System.Enum")
        {
            lines.Add($"T:{type.FullName} : base {Render(type.BaseType)}");
        }

        foreach (var implementation in type.Interfaces)
        {
            lines.Add($"T:{type.FullName} : implements {Render(implementation.InterfaceType)}");
        }

        foreach (var nested in type.NestedTypes)
        {
            if (IsExternallyVisible(nested) && !IsCompilerGenerated(nested))
            {
                Collect(nested, lines);
            }
        }

        foreach (var method in type.Methods)
        {
            if (!IsExternallyVisible(method) || IsCompilerGenerated(method))
            {
                continue;
            }

            // Property accessors are represented by P: lines, event
            // accessors by E: lines; the compiler emits them as methods.
            if (method.IsGetter || method.IsSetter
                || method.Name.StartsWith("add_", StringComparison.Ordinal)
                || method.Name.StartsWith("remove_", StringComparison.Ordinal))
            {
                continue;
            }

            if (method.IsConstructor)
            {
                if (method.IsStatic)
                {
                    // Static constructors are not part of the interface.
                    continue;
                }

                lines.Add($"M:{type.FullName}.#ctor({RenderParameters(method)})");
                continue;
            }

            var line = $"M:{type.FullName}.{method.Name}({RenderParameters(method)})";
            if (method.HasGenericParameters)
            {
                line = $"M:{type.FullName}.{method.Name}``{method.GenericParameters.Count}({RenderParameters(method)})";
            }

            if (method.ReturnType.FullName != "System.Void")
            {
                line += $":{Render(method.ReturnType)}";
            }

            lines.Add(line);
        }

        foreach (var property in type.Properties)
        {
            var accessors = new List<string>();
            if (property.GetMethod is not null && IsExternallyVisible(property.GetMethod))
            {
                accessors.Add("get");
            }

            if (property.SetMethod is not null && IsExternallyVisible(property.SetMethod))
            {
                accessors.Add("set");
            }

            if (accessors.Count == 0)
            {
                // Only reachable through an explicit interface implementation.
                continue;
            }

            lines.Add($"P:{type.FullName}.{property.Name}:{string.Join(";", accessors)}:{Render(property.PropertyType)}");
        }

        foreach (var eventDefinition in type.Events)
        {
            var addVisible = eventDefinition.AddMethod is not null && IsExternallyVisible(eventDefinition.AddMethod);
            var removeVisible = eventDefinition.RemoveMethod is not null && IsExternallyVisible(eventDefinition.RemoveMethod);
            if (!addVisible && !removeVisible)
            {
                continue;
            }

            lines.Add($"E:{type.FullName}.{eventDefinition.Name}:{Render(eventDefinition.EventType)}");
        }

        foreach (var field in type.Fields)
        {
            if (!IsExternallyVisible(field) || IsCompilerGenerated(field) || field.Name == "value__")
            {
                continue;
            }

            var line = $"F:{type.FullName}.{field.Name}:{Render(field.FieldType)}";
            if (field.HasConstant)
            {
                line += $":const={RenderValue(field.Constant)}";
            }

            lines.Add(line);
        }
    }

    private static string RenderParameters(MethodDefinition method)
    {
        var rendered = new List<string>();
        foreach (var parameter in method.Parameters)
        {
            var prefix = parameter.CustomAttributes.Any(a => a.AttributeType.Name == "ParamArrayAttribute")
                ? "params "
                : string.Empty;
            var suffix = parameter.HasConstant ? $"={RenderValue(parameter.Constant)}" : string.Empty;
            rendered.Add($"{prefix}{Render(parameter.ParameterType)}{suffix}");
        }

        return string.Join(",", rendered);
    }

    private static string RenderValue(object? value) => value switch
    {
        null => "null",
        string s => $"\"{s}\"",
        bool b => b ? "true" : "false",
        char c => ((int)c).ToString(System.Globalization.CultureInfo.InvariantCulture),
        IConvertible convertible => convertible.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "<unknown>",
    };

    internal static string Render(TypeReference typeReference) => typeReference switch
    {
        GenericInstanceType genericInstance => $"{Render(genericInstance.ElementType)}<{string.Join(",", genericInstance.GenericArguments.Select(Render))}>",
        ArrayType array => $"{Render(array.ElementType)}[{(array.Rank > 1 ? new string(',', array.Rank - 1) : string.Empty)}]",
        ByReferenceType byReference => $"{Render(byReference.ElementType)}&",
        PointerType pointer => $"{Render(pointer.ElementType)}*",
        RequiredModifierType requiredModifier => $"{Render(requiredModifier.ElementType)} modreq({Render(requiredModifier.ModifierType)})",
        OptionalModifierType optionalModifier => $"{Render(optionalModifier.ElementType)} modopt({Render(optionalModifier.ModifierType)})",
        PinnedType pinned => Render(pinned.ElementType),
        _ => typeReference.FullName,
    };

    private static bool IsExternallyVisible(IMemberDefinition member)
    {
        if (member is TypeDefinition type)
        {
            if (!type.IsNested)
            {
                return type.IsPublic;
            }

            return type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly;
        }

        if (member is MethodDefinition method)
        {
            return method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
        }

        if (member is FieldDefinition field)
        {
            return field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;
        }

        return false;
    }

    private static bool IsCompilerGenerated(IMemberDefinition member)
    {
        return member.CustomAttributes.Any(a => a.AttributeType.Name == "CompilerGeneratedAttribute")
            || member.Name.Contains('<');
    }
}
