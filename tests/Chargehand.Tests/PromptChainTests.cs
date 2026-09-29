using Chargehand.Contracts;
using Chargehand.Prompts;
using Chargehand.Runtime;

namespace Chargehand.Tests;

public class PromptChainTests
{
    [Fact]
    public void Hash_ignores_line_endings_and_trailing_whitespace()
    {
        Assert.Equal(PromptBlock.Hash("a  \nb\n"), PromptBlock.Hash("a\r\nb   \r\n"));
        Assert.NotEqual(PromptBlock.Hash("a\nb"), PromptBlock.Hash("a\nc"));
        Assert.Matches("^[0-9a-f]{64}$", PromptBlock.Hash("x"));
    }

    [Fact]
    public void Registry_reads_version_and_hashes_the_body_only()
    {
        using var dir = new TempDir();
        dir.Write("core/worker.md", "---\nversion: 1.2.0\n---\nBe brief.\n");
        var block = new PromptRegistry(dir.Path).Get("core/worker");
        Assert.Equal("1.2.0", block.Version);
        Assert.Equal("Be brief.\n", block.Text);
        Assert.Equal(PromptBlock.Hash("Be brief.\n"), block.Sha256);
    }

    [Fact]
    public void Registry_rejects_a_block_without_a_semver_version()
    {
        using var dir = new TempDir();
        dir.Write("core/worker.md", "no front matter");
        Assert.Throws<InvalidDataException>(() => new PromptRegistry(dir.Path).Get("core/worker"));
    }

    [Fact]
    public void Caller_blocks_are_verified_and_recorded_as_caller()
    {
        var text = "Write in first person.";
        var caller = new PromptBlock("generator/update", "1.0.0", PromptBlock.Hash(text), text);
        var chain = PromptChains.Build(
            [(new PromptBlock("core/worker", "0.1.0", PromptBlock.Hash("c"), "c"), BlockSource.Registry), (caller, BlockSource.Caller)],
            new AsSent("2.0.16", "build", "p/m", "2026-09-26"));
        Assert.Equal(["core/worker", "generator/update"], chain.Blocks.Select(b => b.Name));
        Assert.Equal(BlockSource.Caller, chain.Blocks[1].Source);
    }

    [Fact]
    public void Caller_block_with_a_wrong_hash_is_rejected()
    {
        var bad = new PromptBlock("g", "1.0.0", new string('0', 64), "text");
        var e = Assert.Throws<ChargehandException>(() => PromptChains.VerifyCallerBlock(bad));
        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.NotNull(e.Action);
    }

    [Fact]
    public void Tool_set_hash_follows_version_agent_and_ruleset()
    {
        PermissionRule[] a = [new("*", "*", PermissionEffect.Allow), new("edit", "*", PermissionEffect.Deny)];
        PermissionRule[] b = [new("*", "*", PermissionEffect.Allow)];
        var h = PromptChains.ToolsSha256("2.0.16", "build", a);
        Assert.Equal(h, PromptChains.ToolsSha256("2.0.16", "build", a.ToArray()));
        Assert.NotEqual(h, PromptChains.ToolsSha256("2.0.16", "build", b));
        Assert.NotEqual(h, PromptChains.ToolsSha256("2.0.17", "build", a));
        Assert.NotEqual(h, PromptChains.ToolsSha256("2.0.16", "plan", a));
    }

    [Fact]
    public void Tool_set_hash_follows_the_granted_tools_and_is_unchanged_without_any()
    {
        PermissionRule[] rules = [new("*", "*", PermissionEffect.Allow)];
        static ServiceGrant Grant(string server, string[] tools, string sha) => new(server, new StdioServiceTransport(["x"], new Dictionary<string, string>()), tools, sha);
        var none = PromptChains.ToolsSha256("2.0.16", "build", rules);

        // The hash of a run without services, as computed before grants existed.
        Assert.Equal("759da0a2a43b2d34040cce77192cd27275471bf08c14f75a60a8e22985474283", none);
        Assert.Equal(none, PromptChains.ToolsSha256("2.0.16", "build", rules, null));
        Assert.Equal(none, PromptChains.ToolsSha256("2.0.16", "build", rules, []));
        var docs = PromptChains.ToolsSha256("2.0.16", "build", rules, [Grant("docs", ["read_doc"], "aa")]);
        Assert.NotEqual(none, docs);
        Assert.Equal(docs, PromptChains.ToolsSha256("2.0.16", "build", rules, [Grant("docs", ["read_doc"], "aa")]));
        Assert.NotEqual(docs, PromptChains.ToolsSha256("2.0.16", "build", rules, [Grant("docs", ["read_doc", "search_docs"], "aa")]));
        Assert.NotEqual(docs, PromptChains.ToolsSha256("2.0.16", "build", rules, [Grant("docs", ["read_doc"], "bb")]));
        Assert.NotEqual(docs, PromptChains.ToolsSha256("2.0.16", "build", rules, [Grant("wiki", ["read_doc"], "aa")]));
    }

    [Fact]
    public void Instruction_hash_depends_on_keys_values_and_order()
    {
        var h = PromptChains.InstructionsSha256([("core", "a"), ("preset", "b")]);
        Assert.NotEqual(h, PromptChains.InstructionsSha256([("preset", "b"), ("core", "a")]));
        Assert.NotEqual(h, PromptChains.InstructionsSha256([("core", "a"), ("preset", "c")]));
    }
}
