using System.Reflection;

namespace Chargehand.Tests;

/// <summary>A reflection listing of an assembly's public surface, one line per type, enum value, property, field,
/// constructor or method: enough to catch a removal or a changed shape (<see cref="PublicApiSnapshotTests"/>).</summary>
internal static class PublicApi
{
    public static IEnumerable<string> Describe(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            yield return $"type {Name(type)}";
            if (type.IsEnum)
            {
                foreach (var value in Enum.GetNames(type).OrderBy(n => n, StringComparer.Ordinal))
                    yield return $"{Name(type)}.{value}";
                continue;
            }
            foreach (var line in Members(type).OrderBy(l => l, StringComparer.Ordinal))
                yield return line;
        }
    }

    private static IEnumerable<string> Members(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        List<string> lines = [];
        foreach (var p in type.GetProperties(flags))
            lines.Add($"{Name(type)}.{p.Name} : {Name(p.PropertyType)}");
        foreach (var f in type.GetFields(flags).Where(f => !f.Name.Contains('<', StringComparison.Ordinal)))
            lines.Add($"{Name(type)}.{f.Name} : {Name(f.FieldType)}");
        foreach (var c in type.GetConstructors(flags).Where(c => !c.IsStatic))
            lines.Add($"{Name(type)}({string.Join(", ", c.GetParameters().Select(Param))})");
        foreach (var m in type.GetMethods(flags).Where(IsOwnMethod))
            lines.Add($"{Name(type)}.{m.Name}({string.Join(", ", m.GetParameters().Select(Param))}) : {Name(m.ReturnType)}");
        return lines;
    }

    // Compiler-generated for every record: Equals, GetHashCode, ToString, Deconstruct, PrintMembers, the
    // positional-clone method (its name starts with '<'), and property/event accessors (IsSpecialName).
    private static bool IsOwnMethod(MethodInfo m) =>
        !m.IsSpecialName
        && m.Name is not ("ToString" or "Equals" or "GetHashCode" or "Deconstruct" or "PrintMembers")
        && !m.Name.StartsWith('<');

    private static string Param(ParameterInfo p) => $"{Name(p.ParameterType)} {p.Name}" + (p.HasDefaultValue ? " = default" : "");

    private static string Name(Type t)
    {
        if (t.IsGenericType)
        {
            var args = string.Join(", ", t.GetGenericArguments().Select(Name));
            var baseName = (t.IsGenericTypeDefinition ? t : t.GetGenericTypeDefinition()).FullName!.Split('`')[0];
            return $"{baseName}<{args}>";
        }
        return t.FullName ?? t.Name;
    }
}
