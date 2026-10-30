using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>
/// Stability plan Task 2 (docs/plans/2026-09-30-stability-1.0.md): Chargehand.Contracts' public API, named as the
/// repository's only published library. Additive only: a type or member may be added, nothing checked in here may
/// disappear or change shape. The snapshot is a floor, not an exact match, so an addition does not need to touch it.
/// </summary>
public class PublicApiSnapshotTests
{
    private static readonly string SnapshotPath = Repo.Path("tests", "Chargehand.Tests", "public-api", "Chargehand.Contracts.txt");

    [Fact]
    public void Public_API_only_grows_since_the_checked_in_snapshot()
    {
        var current = PublicApi.Describe(typeof(ResultContract).Assembly).ToHashSet(StringComparer.Ordinal);
        var baseline = File.ReadAllLines(SnapshotPath).Where(l => l.Length > 0);
        var removed = baseline.Where(l => !current.Contains(l)).OrderBy(l => l, StringComparer.Ordinal).ToList();
        Assert.True(removed.Count == 0,
            $"Removed or changed from Chargehand.Contracts' public API since {SnapshotPath} (a breaking change; add a new member instead of changing this one):\n" +
            string.Join('\n', removed));
    }
}
