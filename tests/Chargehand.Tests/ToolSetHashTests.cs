using Chargehand.Prompts;
using Chargehand.Runtime;

namespace Chargehand.Tests;

// agent-checks: allow deleted-tests, deleted-assertions -- the other prompt chain tests moved to Chargehand.Modules.Research.Tests
public class ToolSetHashTests
{
    [Fact]
    public void Tool_set_hash_follows_version_agent_and_ruleset()
    {
        PermissionRule[] a = [new("*", "*", PermissionEffect.Allow), new("edit", "*", PermissionEffect.Deny)];
        PermissionRule[] b = [new("*", "*", PermissionEffect.Allow)];
        var h = ToolSetHash.ToolsSha256("2.0.16", "build", a);
        Assert.Equal(h, ToolSetHash.ToolsSha256("2.0.16", "build", a.ToArray()));
        Assert.NotEqual(h, ToolSetHash.ToolsSha256("2.0.16", "build", b));
        Assert.NotEqual(h, ToolSetHash.ToolsSha256("2.0.17", "build", a));
        Assert.NotEqual(h, ToolSetHash.ToolsSha256("2.0.16", "plan", a));
    }

    [Fact]
    public void Tool_set_hash_follows_the_granted_tools_and_is_unchanged_without_any()
    {
        PermissionRule[] rules = [new("*", "*", PermissionEffect.Allow)];
        static ServiceGrant Grant(string server, string[] tools, string sha) => new(server, new StdioServiceTransport(["x"], new Dictionary<string, string>()), tools, sha);
        var none = ToolSetHash.ToolsSha256("2.0.16", "build", rules);

        // The hash of a run without services, as computed before grants existed.
        Assert.Equal("759da0a2a43b2d34040cce77192cd27275471bf08c14f75a60a8e22985474283", none);
        Assert.Equal(none, ToolSetHash.ToolsSha256("2.0.16", "build", rules, null));
        Assert.Equal(none, ToolSetHash.ToolsSha256("2.0.16", "build", rules, []));
        var docs = ToolSetHash.ToolsSha256("2.0.16", "build", rules, [Grant("docs", ["read_doc"], "aa")]);
        Assert.NotEqual(none, docs);
        Assert.Equal(docs, ToolSetHash.ToolsSha256("2.0.16", "build", rules, [Grant("docs", ["read_doc"], "aa")]));
        Assert.NotEqual(docs, ToolSetHash.ToolsSha256("2.0.16", "build", rules, [Grant("docs", ["read_doc", "search_docs"], "aa")]));
        Assert.NotEqual(docs, ToolSetHash.ToolsSha256("2.0.16", "build", rules, [Grant("docs", ["read_doc"], "bb")]));
        Assert.NotEqual(docs, ToolSetHash.ToolsSha256("2.0.16", "build", rules, [Grant("wiki", ["read_doc"], "aa")]));
    }
}
