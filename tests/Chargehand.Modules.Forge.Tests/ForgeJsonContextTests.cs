using System.Text.Json;
using Chargehand.Modules.Forge.Api;

namespace Chargehand.Modules.Forge.Tests;

/// <summary>ADR 0060: the forge clients write their request bodies without reflection, byte for byte as the anonymous types before them.</summary>
public class ForgeJsonContextTests
{
    // Characters the default encoder escapes, so a difference in encoders shows.
    private const string Text = "Fix <b> & \"quotes\" — ünïcode";

    public static TheoryData<object, object> Bodies() => new()
    {
        { new { iat = 1L, exp = 2L, iss = "123" }, new AppClaims(1, 2, "123") },
        { new { title = Text, head = "chargehand/run1", @base = "main", body = Text, draft = true, maintainer_can_modify = false }, new DraftPullRequest(Text, "chargehand/run1", "main", Text, true, false) },
        { new { title = Text, body = Text }, new TitleAndBody(Text, Text) },
        { new { expected_head_sha = "abc1234" }, new BranchUpdate("abc1234") },
        { new { merge_method = "squash", sha = "abc1234" }, new SquashMerge("squash", "abc1234") },
        { new { body = Text }, new CommentBody(Text) },
        { new { source_branch = "b", target_branch = "main", title = Text, description = Text }, new MergeRequestDraft("b", "main", Text, Text) },
        { new { title = Text, description = Text }, new TitleAndDescription(Text, Text) },
        { new { title = Text }, new TitleOnly(Text) },
        { new { }, new NoFields() },
        { new { merge_when_pipeline_succeeds = true, sha = "abc1234", squash = true }, new AutoMergeRequest(true, "abc1234", true) },
        { new { sha = "abc1234", squash = true }, new GitLabMerge("abc1234", true) },
        { new { query = "query($id:ID!){x}", variables = (object)new { owner = "o", name = "r", number = 7 } }, new GraphQlRequest("query($id:ID!){x}", new PullRequestVariables("o", "r", 7)) },
        { new { query = "q", variables = (object)new { id = "PR_1" } }, new GraphQlRequest("q", new NodeVariables("PR_1")) },
        { new { query = "q", variables = (object)new { id = "PR_1", oid = "abc1234" } }, new GraphQlRequest("q", new AutoMergeVariables("PR_1", "abc1234")) },
    };

    [Theory]
    [MemberData(nameof(Bodies))]
    public void A_body_is_written_as_the_anonymous_type_wrote_it(object before, object after) =>
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after, after.GetType(), ForgeJsonContext.Default));
}
