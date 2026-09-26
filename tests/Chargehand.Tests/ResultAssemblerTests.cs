using System.Text.Json.Nodes;
using Chargehand.Contracts;
using Chargehand.Results;
using Chargehand.Verification;

namespace Chargehand.Tests;

public class ResultAssemblerTests
{
    private static readonly string Hash = new('a', 64);

    private static ResultEnvelope Envelope() => new(
        TaskId: "t-1",
        NodeId: "n-1",
        TraceId: "0af7651916cd43dd8448eb211c80319c",
        PromptChain: new PromptChain(
            [new ChainBlock("core/worker", "0.1.0", Hash, BlockSource.Registry)],
            new AsSent("2.0.16", "build", "provider/model", "2026-09-26")),
        Usage: new Usage(3, 120, 5600, 90, 0.0002m));

    private const string GoodBlock = """
        {"status":"completed","summary":"divide() raises on zero.",
         "claims":[{"text":"No guard before a / b.","evidence":["e1"],"confidence":0.9}],
         "evidence":[{"id":"e1","kind":"file","locator":"src/calc.py:5-7"}],
         "artifacts":[],"open_questions":[],"confidence":0.9}
        """;

    [Fact]
    public void Extracts_the_last_fenced_json_block()
    {
        var text = $"First try:\n```json\n{{\"x\":1}}\n```\nFinal:\n```json\n{GoodBlock}\n```\n";
        Assert.Contains("\"completed\"", ResultBlocks.ExtractLast(text));
    }

    [Theory]
    [InlineData("no block at all")]
    [InlineData("```python\nprint(1)\n```")]
    [InlineData("```json\n{ not json\n```")]
    public void Missing_or_malformed_block_is_reported(string text)
    {
        var outcome = ResultAssembler.Assemble(text, Envelope());
        Assert.Null(outcome.Contract);
        Assert.NotEmpty(outcome.Errors);
    }

    [Fact]
    public void Worker_block_is_completed_into_a_valid_contract()
    {
        var outcome = ResultAssembler.Assemble($"Done.\n```json\n{GoodBlock}\n```", Envelope());
        Assert.Empty(outcome.Errors);
        var c = outcome.Contract!;
        Assert.Equal("result/v1", c.ContractVersion);
        Assert.Equal("t-1", c.TaskId);
        Assert.Equal(ResultStatus.Completed, c.Status);
        Assert.Equal(Hash, c.PromptChain.Blocks[0].Sha256);
        Assert.Equal(5600, c.Usage.CacheRead);
    }

    [Fact]
    public void Worker_cannot_override_orchestrator_fields()
    {
        var block = JsonNode.Parse(GoodBlock)!.AsObject();
        block["task_id"] = "forged";
        block["usage"] = new JsonObject { ["input"] = 0 };
        var outcome = ResultAssembler.Assemble($"```json\n{block.ToJsonString()}\n```", Envelope());
        Assert.Equal("t-1", outcome.Contract!.TaskId);
        Assert.Equal(3, outcome.Contract.Usage.Input);
    }

    [Fact]
    public void Schema_violation_is_reported_for_the_repair_turn()
    {
        var block = GoodBlock.Replace("\"locator\":\"src/calc.py:5-7\"", "\"locator\":\"src/calc.py:5-7\",\"commit\":\"\"");
        var outcome = ResultAssembler.Assemble($"```json\n{block}\n```", Envelope());
        Assert.Null(outcome.Contract);
        Assert.Contains(outcome.Errors, e => e.Contains("commit", StringComparison.Ordinal));
    }

    [Fact]
    public void Unresolved_claims_move_to_open_questions()
    {
        var contract = ResultAssembler.Assemble($"```json\n{GoodBlock}\n```", Envelope()).Contract!;
        var moved = ResultAssembler.MoveUnresolved(contract, [new EvidenceFailure("e1", "line 7 beyond end of file (5 lines)")]);
        Assert.Empty(moved.Claims);
        Assert.Empty(moved.Evidence);
        Assert.Single(moved.OpenQuestions);
        Assert.Contains("No guard before a / b.", moved.OpenQuestions[0], StringComparison.Ordinal);
        Assert.Contains("line 7 beyond end of file", moved.OpenQuestions[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_claim_keeps_its_place_while_one_of_its_references_resolves()
    {
        var two = GoodBlock
            .Replace("\"evidence\":[\"e1\"]", "\"evidence\":[\"e1\",\"e2\"]")
            .Replace("\"locator\":\"src/calc.py:5-7\"}", "\"locator\":\"src/calc.py:5-7\"},{\"id\":\"e2\",\"kind\":\"file\",\"locator\":\"src/calc.py:99\"}");
        var contract = ResultAssembler.Assemble($"```json\n{two}\n```", Envelope()).Contract!;
        var moved = ResultAssembler.MoveUnresolved(contract, [new EvidenceFailure("e2", "line 99 beyond end of file")]);
        Assert.Single(moved.Claims);
        Assert.Equal(["e1"], moved.Claims[0].Evidence);
        Assert.Single(moved.Evidence);
    }

    [Fact]
    public void Inline_artifacts_carry_the_orchestrators_hash_of_their_content()
    {
        var block = $$"""
            ```json
            {"status":"completed","summary":"s","claims":[],"evidence":[],
             "artifacts":[{"kind":"draft","media_type":"text/markdown","sha256":"{{Hash}}","content":"Hi."}],"open_questions":[],"confidence":0.5}
            ```
            """;
        var artifact = Assert.Single(ResultAssembler.Assemble(block, Envelope()).Contract!.Artifacts);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("Hi."u8)), artifact.Sha256);
    }

    [Fact]
    public void Inline_content_is_bounded_in_bytes_not_characters()
    {
        var content = new string('\u00e9', 40_000); // 40,000 characters, 80,000 UTF-8 bytes
        var block = $$"""
            ```json
            {"status":"completed","summary":"s","claims":[],"evidence":[],
             "artifacts":[{"kind":"draft","media_type":"text/markdown","content":"{{content}}"}],"open_questions":[],"confidence":0.5}
            ```
            """;
        var outcome = ResultAssembler.Assemble(block, Envelope());
        Assert.Null(outcome.Contract);
        Assert.Contains("80000 bytes", Assert.Single(outcome.Errors), StringComparison.Ordinal);
    }
}
