using System.Text.Json.Serialization;

// Fixtures for PublicApiShapeTests: each type here has a twin in ApiShapeAfter that differs by one breaking change.
namespace Chargehand.Tests.ApiShapeBefore;

public class MadeSealed;
public class MadeAbstract;
public class MadeStatic;
public class Base;
public class DropsBase : Base;
public interface IShape { int Size { get; } }
public class DropsInterface : IShape { public int Size { get; init; } = 1; }
public class SetToInit { public string? Name { get; set; } }
public class NullabilityChange { public string? Name { get; init; } }
public static class ParameterNullability { public static int Take(string? name) => name?.Length ?? 0; }
public enum Renumbered { None = 0, First = 1, Second = 2 }
[JsonConverter(typeof(JsonStringEnumConverter<DropsConverter>))]
public enum DropsConverter { None }
public class DropsPropertyName { [JsonPropertyName("n")] public string? Name { get; init; } }
public sealed record Unchanged(string Name, int? Count = null) : IShape { public int Size => 1; }
