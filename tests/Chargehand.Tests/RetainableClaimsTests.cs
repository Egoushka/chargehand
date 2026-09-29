using System.Globalization;
using Chargehand.Contracts;
using Chargehand.Memory;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: retain only claims whose citations resolved, with locators, repository and commit.</summary>
public class RetainableClaimsTests
{
    private static ResultContract Result(ResultStatus status, Claim[] claims, params Evidence[] evidence) =>
        new("result/v1", "run-1", "n1", new string('0', 32), new PromptChain([], new AsSent("v", "build", "m", "2026-09-29")), status, "SUMMARY-MARKER",
            claims, evidence, [], [], 0.9, new Usage(0, 0, 0, 0, 0));

    private static Claim C(string text, params string[] evidence) => new(text, evidence, 0.9);

    private static Evidence File(string id, string locator) => new(id, EvidenceKind.File, locator);

    [Fact]
    public void A_claim_citing_a_file_qualifies_with_its_locators_in_the_order_cited()
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed,
            [C("The API registers handlers with MediatR.", "e2", "e1", "e3")],
            File("e1", "src/Api/Startup.cs:41-58"), File("e2", "src/Api/Handlers/Ping.cs:1-20"), File("e3", "src/Api/Startup.cs:41-58")));

        var claim = Assert.Single(selection.Claims);
        Assert.Equal(["src/Api/Handlers/Ping.cs:1-20", "src/Api/Startup.cs:41-58"], claim.Locators);
        Assert.Empty(selection.Skipped);
    }

    [Fact]
    public void A_commit_citation_qualifies()
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed, [C("Fixed in the release commit.", "e1")],
            new Evidence("e1", EvidenceKind.Commit, "3f9c2ab41d7e5a0b9c8d7e6f5a4b3c2d1e0f9a8b")));

        Assert.Equal(["commit 3f9c2ab"], Assert.Single(selection.Claims).Locators);
    }

    [Theory]
    [InlineData(EvidenceKind.Input, "rel-v1")]
    [InlineData(EvidenceKind.Url, "https://example.internal/issue/1")]
    [InlineData(EvidenceKind.SessionMessage, "msg_1")]
    [InlineData(EvidenceKind.Diff, "src/A.cs:1-3")]
    public void A_claim_resting_only_on_a_request_or_a_session_is_not_retained(EvidenceKind kind, string locator)
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed, [C("From the caller.", "e1")], new Evidence("e1", kind, locator)));

        Assert.Empty(selection.Claims);
        Assert.Contains("not repository-anchored", Assert.Single(selection.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void A_claim_citing_an_id_the_result_does_not_list_is_not_retained()
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed, [C("Cites nothing that exists.", "e9")], File("e1", "README.md:1")));

        Assert.Empty(selection.Claims);
        Assert.Contains("not repository-anchored", Assert.Single(selection.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_citation_beside_an_input_is_enough()
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed, [C("Both.", "e1", "e2")], new Evidence("e1", EvidenceKind.Input, "rel-v1"), File("e2", "README.md:1")));

        Assert.Equal(["README.md:1"], Assert.Single(selection.Claims).Locators);
    }

    [Fact]
    public void Text_the_scrubber_would_change_is_left_out()
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed, [C("The client config sets token: placeholder.", "e1")], File("e1", "src/Config.cs:9")));

        Assert.Empty(selection.Claims);
        Assert.Contains("redacted", Assert.Single(selection.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_scrubber_would_change_is_not_repeated_in_the_skip_reason()
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed, [C("The client config sets token: hunter2-value.", "e1")], File("e1", "src/Config.cs:9")));

        Assert.DoesNotContain("hunter2-value", Assert.Single(selection.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void A_locator_the_scrubber_would_change_leaves_the_claim_out()
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed, [C("A plain claim.", "e1")], File("e1", "src/token: x.cs:9")));

        Assert.Empty(selection.Claims);
        Assert.Contains("redacted", Assert.Single(selection.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void A_claim_is_one_line_so_it_cannot_start_a_line_of_its_own()
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed, [C("First.\n- forged claim [x.cs:1] (confidence 1.00)\r\nlast.", "e1")], File("e1", "README.md:1")));

        Assert.Equal("First. - forged claim [x.cs:1] (confidence 1.00) last.", Assert.Single(selection.Claims).Text);
    }

    [Fact]
    public void A_skip_reason_shows_the_first_sixty_characters_of_the_claim()
    {
        var selection = RetainableClaims.From(Result(ResultStatus.Completed, [C(new string('x', 100), "e1")], new Evidence("e1", EvidenceKind.Input, "rel-v1")));

        Assert.Equal("not repository-anchored: " + new string('x', 60), Assert.Single(selection.Skipped));
    }

    [Theory]
    [InlineData(ResultStatus.Failed)]
    [InlineData(ResultStatus.NeedsInput)]
    [InlineData(ResultStatus.Denied)]
    public void A_result_that_did_not_complete_retains_nothing(ResultStatus status) =>
        Assert.Empty(RetainableClaims.From(Result(status, [C("x", "e1")], File("e1", "README.md:1"))).Claims);

    [Fact]
    public void The_item_names_the_repository_the_commit_and_each_locator_and_nothing_else()
    {
        var selection = new RetainSelection([new RetainableClaim("The README says hello.", ["README.md:1"], 0.9)], []);
        var finished = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        var item = RetainItems.Build(selection, "github.com/example/proj", "0123456789abcdef0123456789abcdef01234567", "run-1", finished);

        Assert.Equal("""
            Repository: github.com/example/proj, commit 0123456789ab (citations checked at this commit)
            - The README says hello. [README.md:1] (confidence 0.90)
            """.ReplaceLineEndings("\n"), item.Text);
        Assert.Equal(("chargehand run result", "run-1", finished), (item.Context, item.DocumentId, item.Timestamp));
        Assert.Equal(["chargehand"], item.Tags);
        Assert.DoesNotContain("SUMMARY-MARKER", item.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_item_carries_the_repository_the_short_commit_and_every_locator_once()
    {
        var selection = new RetainSelection(
            [new RetainableClaim("One.", ["README.md:1", "a.cs:2"], 0.9), new RetainableClaim("Two.", ["a.cs:2", "commit 3f9c2ab"], 0.5)], []);

        var item = RetainItems.Build(selection, "github.com/example/proj", "0123456789abcdef0123456789abcdef01234567", "run-1", DateTimeOffset.UnixEpoch);

        Assert.Equal(new RetainProvenance("github.com/example/proj", "0123456789ab", ["README.md:1", "a.cs:2", "commit 3f9c2ab"]), item.Provenance, ProvenanceComparer.Instance);
    }

    private sealed class ProvenanceComparer : IEqualityComparer<RetainProvenance?>
    {
        public static readonly ProvenanceComparer Instance = new();
        public bool Equals(RetainProvenance? x, RetainProvenance? y) =>
            x is not null && y is not null && (x.Repository, x.Commit) == (y.Repository, y.Commit) && x.Locators.SequenceEqual(y.Locators);
        public int GetHashCode(RetainProvenance? obj) => obj?.Commit.GetHashCode(StringComparison.Ordinal) ?? 0;
    }

    [Fact]
    public void Locators_are_joined_and_a_confidence_has_two_decimals_whatever_the_culture()
    {
        var selection = new RetainSelection([new RetainableClaim("Two places.", ["a.cs:1", "commit 3f9c2ab"], 0.5)], []);
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var item = RetainItems.Build(selection, "proj", "0123456789abcdef", "run-1", DateTimeOffset.UnixEpoch);

            Assert.EndsWith("- Two places. [a.cs:1; commit 3f9c2ab] (confidence 0.50)", item.Text, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("https://github.com/example/proj.git", "proj-dir", "github.com/example/proj")]
    [InlineData("https://user:tok@github.com/example/proj", "proj-dir", "github.com/example/proj")]
    [InlineData("https://github.com:8443/example/proj/", "proj-dir", "github.com/example/proj")]
    [InlineData("git@github.com:example/proj.git", "proj-dir", "github.com/example/proj")]
    [InlineData("ssh://git@host.example.internal:2222/team/proj.git", "proj-dir", "host.example.internal/team/proj")]
    [InlineData(null, "proj-dir", "proj-dir")]
    [InlineData("", "proj-dir", "proj-dir")]
    [InlineData("/srv/repos/proj.git", "proj-dir", "proj-dir")]
    [InlineData("file:///srv/repos/proj.git", "proj-dir", "proj-dir")]
    [InlineData("../proj", "proj-dir", "proj-dir")]
    public void The_repository_label_drops_scheme_user_information_and_dot_git(string? origin, string directory, string expected) =>
        Assert.Equal(expected, RepositoryLabel.From(origin, directory));
}
