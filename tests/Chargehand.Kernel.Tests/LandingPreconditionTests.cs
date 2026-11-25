using System.Text.Json;

namespace Chargehand.Kernel.Tests;

/// <summary>Landing stage 0 (docs/specs/2026-11-23-agent-git-landing.md): what GitHub answered when the preconditions were run live
/// (scripts/landing-preconditions.mjs), kept as a fixture. Stage 1 is built on these answers; a change that contradicts one fails here.</summary>
public sealed class LandingPreconditionTests
{
    private static JsonElement Fixture() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "landing-stage0.json"))).RootElement;

    [Fact]
    public void An_app_on_a_personal_account_opens_and_merges_pull_requests_and_pushes_the_notes_ref()
    {
        var steps = Fixture().GetProperty("steps");
        Assert.Equal(201, steps.GetProperty("openDraftPullRequest").GetInt32());
        Assert.Equal("ok", steps.GetProperty("markReadyForReview").GetString());
        Assert.Equal(200, steps.GetProperty("mergePullRequest").GetInt32());
        Assert.Equal(201, steps.GetProperty("pushNotesIntentRef").GetInt32());
        Assert.Equal([201, 200], steps.GetProperty("openAndMergeRevertPullRequest").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public void A_merge_queue_is_not_available_on_a_user_owned_repository_so_the_landing_queue_serialises_merges_itself()
    {
        var steps = Fixture().GetProperty("steps");
        Assert.Equal(403, steps.GetProperty("mergeQueueRulesetAsApp").GetInt32());
        Assert.StartsWith("422 Invalid rule 'merge_queue'", steps.GetProperty("mergeQueueRulesetAsOwner").GetString());
    }

    [Fact]
    public void The_recorded_token_shape_is_what_the_redactors_are_tested_against()
    {
        var shape = Fixture().GetProperty("token");
        Assert.Equal("ghs_", shape.GetProperty("prefix").GetString());
        Assert.InRange(shape.GetProperty("length").GetInt32(), 390, 520);
        Assert.Equal(2, shape.GetProperty("dots").GetInt32());
        Assert.Equal(0, shape.GetProperty("otherCharacters").GetInt32());
    }
}
