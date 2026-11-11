using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Chargehand.Tests;

/// <summary>A reflection listing of an assembly's public surface, one line per type, type shape (modifiers, base type,
/// interface, serialization attribute), enum value, property, field, constructor or method: enough to catch a removal
/// or a changed shape (<see cref="PublicApiSnapshotTests"/>). Member types carry their nullable-reference annotation
/// (<c>System.String?</c>), properties their accessors (<c>{ get; init; }</c>), enum members their numeric value.</summary>
internal static class PublicApi
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    public static IEnumerable<string> Describe(Assembly assembly) => Describe(assembly.GetExportedTypes());

    public static IEnumerable<string> Describe(IEnumerable<Type> types)
    {
        foreach (var type in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            yield return $"type {Name(type)}";
            foreach (var line in Shape(type).Concat(type.IsEnum ? EnumValues(type) : Members(type)).OrderBy(l => l, StringComparer.Ordinal))
                yield return line;
        }
    }

    private static IEnumerable<string> Shape(Type type)
    {
        yield return $"{Name(type)} is {Kind(type)}";
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            yield return $"{Name(type)} extends {Name(b)}";
        foreach (var i in type.GetInterfaces().Where(i => i.IsVisible))
            yield return $"{Name(type)} implements {Name(i)}";
        foreach (var a in Serialization(type.CustomAttributes))
            yield return $"{Name(type)} {a}";
    }

    private static string Kind(Type t) => t switch
    {
        { IsInterface: true } => "interface",
        { IsEnum: true } => $"enum {Name(Enum.GetUnderlyingType(t))}",
        { IsValueType: true } => "struct",
        { IsAbstract: true, IsSealed: true } => "static class",
        { IsAbstract: true } => "abstract class",
        { IsSealed: true } => "sealed class",
        _ => "class",
    };

    private static IEnumerable<string> EnumValues(Type type)
    {
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            yield return $"{Name(type)}.{f.Name}";
            yield return $"{Name(type)}.{f.Name} = {Convert.ToString(f.GetRawConstantValue(), CultureInfo.InvariantCulture)}";
            foreach (var a in Serialization(f.CustomAttributes))
                yield return $"{Name(type)}.{f.Name} {a}";
        }
    }

    private static List<string> Members(Type type)
    {
        var nullability = new NullabilityInfoContext();
        List<string> lines = [];
        foreach (var p in type.GetProperties(Flags))
        {
            lines.Add($"{Name(type)}.{p.Name} : {Name(nullability.Create(p))} {Accessors(p)}");
            lines.AddRange(Serialization(p.CustomAttributes).Select(a => $"{Name(type)}.{p.Name} {a}"));
        }
        foreach (var f in type.GetFields(Flags).Where(f => !f.Name.Contains('<', StringComparison.Ordinal)))
        {
            lines.Add($"{Name(type)}.{f.Name} : {Name(nullability.Create(f))}");
            lines.AddRange(Serialization(f.CustomAttributes).Select(a => $"{Name(type)}.{f.Name} {a}"));
        }
        foreach (var c in type.GetConstructors(Flags).Where(c => !c.IsStatic))
            lines.Add($"{Name(type)}({Params(c, nullability)})");
        foreach (var m in type.GetMethods(Flags).Where(IsOwnMethod))
            lines.Add($"{Name(type)}.{m.Name}({Params(m, nullability)}) : {Name(nullability.Create(m.ReturnParameter))}");
        return lines;
    }

    // An init accessor is a setter whose return carries the IsExternalInit modifier; a non-public setter is left out.
    private static string Accessors(PropertyInfo p)
    {
        var get = p.GetMethod is { IsPublic: true } ? " get;" : "";
        var set = p.SetMethod is not { IsPublic: true } setter ? ""
            : setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)) ? " init;" : " set;";
        return $"{{{get}{set} }}";
    }

    // Compiler-generated for every record: Equals, GetHashCode, ToString, Deconstruct, PrintMembers, the
    // positional-clone method (its name starts with '<'), and property/event accessors (IsSpecialName).
    private static bool IsOwnMethod(MethodInfo m) =>
        !m.IsSpecialName
        && m.Name is not ("ToString" or "Equals" or "GetHashCode" or "Deconstruct" or "PrintMembers")
        && !m.Name.StartsWith('<');

    private static string Params(MethodBase method, NullabilityInfoContext nullability) =>
        string.Join(", ", method.GetParameters().Select(p =>
            $"{Name(nullability.Create(p))} {p.Name}" + (p.HasDefaultValue ? " = default" : "")));

    // Attributes from System.Text.Json change what goes over the wire, so dropping or changing one is a breaking change.
    private static IEnumerable<string> Serialization(IEnumerable<CustomAttributeData> attributes) =>
        attributes
            .Where(a => a.AttributeType.Namespace?.StartsWith("System.Text.Json", StringComparison.Ordinal) == true)
            .Select(a => $"[{Name(a.AttributeType)}({string.Join(", ", a.ConstructorArguments.Select(Value).Concat(a.NamedArguments.Select(n => $"{n.MemberName} = {Value(n.TypedValue)}")))})]");

    private static string Value(CustomAttributeTypedArgument arg) => arg.Value switch
    {
        null => "null",
        Type t => $"typeof({Name(t)})",
        string s => $"\"{s}\"",
        ReadOnlyCollection<CustomAttributeTypedArgument> items => $"[{string.Join(", ", items.Select(Value))}]",
        var v when arg.ArgumentType.IsEnum => $"{Name(arg.ArgumentType)}.{Enum.ToObject(arg.ArgumentType, v)}",
        var v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    // A reference type annotated nullable gets '?'; a nullable value type already reads System.Nullable<T>.
    private static string Name(NullabilityInfo info)
    {
        var t = info.Type;
        var name = t.IsArray && info.ElementType is { } element ? $"{Name(element)}[]"
            : t.IsGenericType && info.GenericTypeArguments.Length > 0
                ? $"{GenericBaseName(t)}<{string.Join(", ", info.GenericTypeArguments.Select(Name))}>"
                : Name(t);
        return !t.IsValueType && info.ReadState == NullabilityState.Nullable ? name + "?" : name;
    }

    private static string Name(Type t)
    {
        if (t.IsGenericType)
            return $"{GenericBaseName(t)}<{string.Join(", ", t.GetGenericArguments().Select(Name))}>";
        return t.FullName ?? t.Name;
    }

    private static string GenericBaseName(Type t) => (t.IsGenericTypeDefinition ? t : t.GetGenericTypeDefinition()).FullName!.Split('`')[0];
}
