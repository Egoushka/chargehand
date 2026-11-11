namespace Chargehand.Tests;

/// <summary>Each breaking change issue #685 names, applied to one type, takes a line out of
/// <see cref="PublicApi.Describe(IEnumerable{Type})"/>, so <see cref="PublicApiSnapshotTests"/> fails on it.</summary>
public class PublicApiShapeTests
{
    [Theory]
    [InlineData(typeof(ApiShapeBefore.MadeSealed), typeof(ApiShapeAfter.MadeSealed))]
    [InlineData(typeof(ApiShapeBefore.MadeAbstract), typeof(ApiShapeAfter.MadeAbstract))]
    [InlineData(typeof(ApiShapeBefore.MadeStatic), typeof(ApiShapeAfter.MadeStatic))]
    [InlineData(typeof(ApiShapeBefore.DropsBase), typeof(ApiShapeAfter.DropsBase))]
    [InlineData(typeof(ApiShapeBefore.DropsInterface), typeof(ApiShapeAfter.DropsInterface))]
    [InlineData(typeof(ApiShapeBefore.SetToInit), typeof(ApiShapeAfter.SetToInit))]
    [InlineData(typeof(ApiShapeBefore.NullabilityChange), typeof(ApiShapeAfter.NullabilityChange))]
    [InlineData(typeof(ApiShapeBefore.ParameterNullability), typeof(ApiShapeAfter.ParameterNullability))]
    [InlineData(typeof(ApiShapeBefore.Renumbered), typeof(ApiShapeAfter.Renumbered))]
    [InlineData(typeof(ApiShapeBefore.DropsConverter), typeof(ApiShapeAfter.DropsConverter))]
    [InlineData(typeof(ApiShapeBefore.DropsPropertyName), typeof(ApiShapeAfter.DropsPropertyName))]
    public void A_breaking_change_removes_a_line(Type before, Type after) =>
        Assert.NotEmpty(Removed(before, after));

    [Fact]
    public void An_unchanged_type_removes_nothing() =>
        Assert.Empty(Removed(typeof(ApiShapeBefore.Unchanged), typeof(ApiShapeAfter.Unchanged)));

    private static List<string> Removed(Type before, Type after)
    {
        var current = PublicApi.Describe([after]).ToHashSet(StringComparer.Ordinal);
        return PublicApi.Describe([before])
            .Select(l => l.Replace(nameof(ApiShapeBefore), nameof(ApiShapeAfter), StringComparison.Ordinal))
            .Where(l => !current.Contains(l))
            .ToList();
    }
}
