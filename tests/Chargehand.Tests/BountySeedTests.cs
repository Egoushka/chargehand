using System.Globalization;
using System.Text.Json;

namespace Chargehand.Tests;

/// <summary>Claim bounty, Task 1 (docs/specs/2026-11-08-claim-bounty-design.md): each synthetic seed is a false claim whose disproving line exists in this checkout.</summary>
public class BountySeedTests
{
    private sealed record Seed(string Id, string Claim, string Locator, string Contains);

    private static readonly IReadOnlyList<Seed> Seeds = [.. File.ReadLines(Repo.Path("evals", "bounty-seeds.jsonl")).Where(l => l.Length > 0).Select(Parse)];

    private static Seed Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var disproof = root.GetProperty("disproof");
        return new Seed(root.GetProperty("id").GetString()!, root.GetProperty("claim").GetString()!, disproof.GetProperty("locator").GetString()!,
            disproof.GetProperty("contains").GetString()!);
    }

    [Fact]
    public void Every_seed_has_an_id_a_claim_and_a_disproof()
    {
        Assert.NotEmpty(Seeds);
        Assert.Equal(Seeds.Count, Seeds.Select(s => s.Id).Distinct().Count());
        Assert.All(Seeds, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Claim), s.Id);
            Assert.False(string.IsNullOrWhiteSpace(s.Contains), s.Id);
        });
    }

    [Fact]
    public void Every_disproof_cites_lines_in_this_checkout_that_contain_its_text()
    {
        Assert.All(Seeds, s =>
        {
            var colon = s.Locator.LastIndexOf(':');
            Assert.True(colon > 0, $"{s.Id}: the locator is not path:line or path:start-end");
            var range = s.Locator[(colon + 1)..].Split('-');
            var start = int.Parse(range[0], CultureInfo.InvariantCulture);
            var end = range.Length > 1 ? int.Parse(range[1], CultureInfo.InvariantCulture) : start;
            var lines = File.ReadAllLines(Repo.Path(s.Locator[..colon].Split('/')));
            Assert.True(start >= 1 && end >= start && end <= lines.Length, $"{s.Id}: {s.Locator} names no lines in the file");
            var cited = string.Join("\n", lines[(start - 1)..end]);
            Assert.True(cited.Contains(s.Contains, StringComparison.Ordinal), $"{s.Id}: {s.Locator} no longer contains '{s.Contains}'; move the seed's locator with the code");
        });
    }
}
