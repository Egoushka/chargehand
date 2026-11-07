// Fixtures for PublicApiShapeTests: each type here is its ApiShapeBefore twin after one breaking change.
namespace Chargehand.Tests.ApiShapeAfter;

public sealed class MadeSealed;
public abstract class MadeAbstract;
public static class MadeStatic;
public class Base;
public class DropsBase;
public interface IShape { int Size { get; } }
public class DropsInterface { public int Size { get; init; } = 1; }
public class SetToInit { public string? Name { get; init; } }
public class NullabilityChange { public string Name { get; init; } = ""; }
public static class ParameterNullability { public static int Take(string name) => name.Length; }
public enum Renumbered { None = 0, First = 2, Second = 1 }
public enum DropsConverter { None }
public class DropsPropertyName { public string? Name { get; init; } }
public sealed record Unchanged(string Name, int? Count = null) : IShape { public int Size => 1; }
