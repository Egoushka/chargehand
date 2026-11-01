using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Tests;

public sealed partial class RunNamesTests
{
    [Fact]
    public void The_same_id_always_gets_the_same_name()
    {
        // Pinned: a change to the lists or the hash renames every run already recorded.
        Assert.Equal("Zappy Kitten", RunNames.Of("run-20261101-120000-a1b2c3"));
        Assert.Equal("zappy-kitten", RunNames.Slug("run-20261101-120000-a1b2c3"));
    }

    [Theory]
    [InlineData("run-20261101-120000-a1b2c3")]
    [InlineData("run-20261101-120000-000000")]
    [InlineData("5e87fcd7")]
    public void A_name_is_an_adjective_and_an_animal(string id)
    {
        Assert.Matches(NameShape(), RunNames.Of(id));
        Assert.Matches(SlugShape(), RunNames.Slug(id));
        Assert.Equal(RunNames.Of(id).ToLowerInvariant().Replace(' ', '-'), RunNames.Slug(id));
        Assert.Equal($"{RunNames.Of(id)} ({id})", RunNames.Label(id));
    }

    [Fact]
    public void Each_list_has_256_distinct_lowercase_words_and_the_two_share_none()
    {
        foreach (var list in new[] { RunNames.Adjectives, RunNames.Animals })
        {
            Assert.Equal(256, list.Count);
            Assert.Equal(256, list.Distinct().Count());
            Assert.All(list, w => Assert.Matches("^[a-z]{2,12}$", w));
        }
        Assert.Empty(RunNames.Adjectives.Intersect(RunNames.Animals));
    }

    [Fact]
    public void Every_word_is_reachable()
    {
        var adjectives = new HashSet<string>();
        var animals = new HashSet<string>();
        for (var i = 0; i < 50_000; i++)
        {
            var parts = RunNames.Slug(Id(i)).Split('-');
            adjectives.Add(parts[0]);
            animals.Add(parts[1]);
        }
        Assert.Equal(256, adjectives.Count);
        Assert.Equal(256, animals.Count);
    }

    [Fact]
    public void Runs_active_at_once_rarely_share_a_name()
    {
        // 5,000 moments with 20 runs each, ids made the way RunIds makes them. Expected clash rate: 20·19/2 / 65,536 ≈ 0.3%.
        var random = new Random(45);
        var clashes = 0;
        const int moments = 5_000;
        for (var m = 0; m < moments; m++)
        {
            var names = Enumerable.Range(0, 20).Select(_ => RunNames.Of(Id(random.Next()))).ToList();
            if (names.Distinct().Count() < names.Count)
                clashes++;
        }
        Assert.True(clashes < moments / 100, $"{clashes} of {moments} moments had two runs with one name");
    }

    [Fact]
    public void A_new_run_id_has_a_name()
    {
        Assert.Matches(NameShape(), RunNames.Of(RunIds.New()));
    }

    private static string Id(int n) => $"run-20261101-{n % 86400:D6}-{n:x6}";

    [GeneratedRegex("^[A-Z][a-z]+ [A-Z][a-z]+$")]
    private static partial Regex NameShape();

    [GeneratedRegex("^[a-z]+-[a-z]+$")]
    private static partial Regex SlugShape();
}
