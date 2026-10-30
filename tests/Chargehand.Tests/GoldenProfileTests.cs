using System.Text.RegularExpressions;
using Chargehand.Config;
using Chargehand.Modules.Memory.Api;
using Chargehand.Modules.Oversight.Api;
using Chargehand.Modules.Runners.Api;

namespace Chargehand.Tests;

/// <summary>Stability plan task 3: a checked-in profile that uses every key of the reference table loads and yields the
/// expected values, the table and the loader name the same keys, and a memory mapping exercises every placeholder.</summary>
public class GoldenProfileTests
{
    private static readonly string GoldenPath = Repo.Path("tests", "Chargehand.Tests", "golden", "profile.full.json");

    [Fact]
    public void The_golden_profile_loads_with_the_expected_value_of_every_documented_field()
    {
        var profile = Profile.Load(GoldenPath);

        Assert.Equal("profile/v1", profile.Schema);
        Assert.Equal("opencode", profile.Runtime);
        Assert.Equal(2, profile.Secrets!.Count);
        Assert.True(profile.Secrets[0].Env);
        Assert.Equal(["pass", "show", "{item}"], profile.Secrets[1].Command);

        Assert.Equal("https://opencode.chargehand.example.internal", profile.Opencode!.Url);
        Assert.Equal("chargehand-opencode-password", profile.Opencode.PasswordSecret);
        Assert.Equal("2.0.18", profile.Opencode.Version);
        Assert.Equal("opencode", profile.Opencode.Binary);

        Assert.Equal(">=2.1.283", profile.ClaudeCode!.Version);
        Assert.Equal("chargehand-anthropic-key", profile.ClaudeCode.ApiKeySecret);
        Assert.Equal(25, profile.ClaudeCode.ApiFallbackDailyUsd);

        Assert.True(profile.Acp!.AllowUncapped);
        Assert.Equal("/srv/chargehand/work/acp-agents.json", profile.Acp.AgentsFile);
        Assert.Equal("chargehand-agent-key", profile.Acp.CredentialFor("example-agent")!.ApiKeySecret);

        Assert.Equal("alice", profile.Inbox!.OwnerOf("example-owner/some-repo"));

        Assert.Equal("/srv/chargehand/work", profile.WorkerRoot);
        Assert.Equal(["/srv/chargehand/work/local-only", "~/local-example"], profile.Delivery!.LocalOnly);
        Assert.Equal(["/srv/chargehand/work", "/srv/chargehand/repos"], profile.RepositoryRoots);
        Assert.True(profile.SupportCheck);
        Assert.Equal("/srv/chargehand/signing-key.pem", profile.Signing!.KeyFile);
        Assert.Equal(("bubblewrap", false), (profile.Sandbox!.Kind, profile.Sandbox.Network));
        Assert.Equal(["EXAMPLE_VAR"], profile.Sandbox.Env);
        Assert.Equal("cheap", profile.DefaultPreset);
        Assert.Equal("provider/intake-model", profile.IntakeModel);
        Assert.Equal(2.5m, profile.RunCapUsd);
        Assert.Equal(2, profile.MaxParallelNodes);
        Assert.Equal("/srv/chargehand/work/run-log.jsonl", profile.RunLog);

        Assert.Equal("https://otel.chargehand.example.internal", profile.Telemetry!.OtlpEndpoint);
        Assert.True(profile.Telemetry.UsageOnSpans);
        Assert.Equal(3, profile.Prices!["provider/worker-model"].Input);
        Assert.Equal("anthropic/claude-sonnet-5", profile.Models!["provider/worker-model"]);

        Assert.Equal("chargehand-api-key", profile.Http!.ApiKeySecret);
        Assert.Equal(4300, profile.Http.Port);
        Assert.Equal(["alice", "bob"], profile.Http.Accounts!.Select(a => a.Name));
        Assert.Equal("example-team.cloudflareaccess.com", profile.Http.CloudflareAccess!.TeamDomain);
        Assert.Equal(["chargehand.example.internal"], profile.Http.ProxySignIn!.Hosts);
        Assert.Equal("fake", profile.Http.Conversations!.Runtime);

        Assert.True(profile.Driven!.Enabled);
        Assert.Equal(3, profile.Driven.MaxParallel);
        Assert.Equal(2, profile.Driven.Images!.Count);
        Assert.Equal(["pkg.chargehand.example.internal"], profile.Driven.Network!.Allow);
        Assert.Equal("chargehand-push-secret", profile.Driven.PushSecret);
        Assert.Equal("tracker", profile.Driven.TaskSource!.Server);
        Assert.Equal(45, profile.Driven.Limits!.MaxMinutes);
        Assert.Equal(GovernorMode.Observe, profile.Driven.Governor!.Mode);
        Assert.Equal(1024, profile.Driven.Admission!.ReserveMb);
        Assert.Equal(123456, profile.Driven.Integrator!.AppId);
        Assert.Equal(["npm"], profile.Driven.Services!.Caches);
        Assert.Equal("gitlab", profile.Driven.Forge!.Kind);
        Assert.True(profile.Driven.Planning!.Enabled);
        Assert.True(profile.Driven.Learning!.Retro);
        Assert.Equal(("default", WorkspaceMode.Shared), (profile.Driven.Runners![0].Name, profile.Driven.Runners[0].Workspace));
        Assert.Single(profile.Driven.ImageVariants!);
        Assert.Equal("image", profile.Driven.Environment);

        Assert.Equal(["notes", "tracker", "enhancer"], profile.McpServers!.Keys);
        Assert.Equal("enhancer", profile.PresetServices!["cheap"][0].Server);
        Assert.Equal(("enhancer", 1500), (profile.PromptEnhancer!.Server, profile.PromptEnhancer.DeadlineMs));

        var memory = profile.Memory!.Single();
        Assert.Equal(("notes", "notes", true), (memory.Name, memory.Server, memory.Retain));
        Assert.Equal(("search", "save", "forget"), (memory.Tools.Recall.Tool, memory.Tools.Retain!.Tool, memory.Tools.Invalidate!.Tool));
    }

    [Fact]
    public void Every_documented_profile_field_is_read_by_the_loader()
    {
        var documented = DocumentedProfileFields();
        var loaded = LoaderProfileFields();

        Assert.NotEmpty(documented);
        Assert.All(documented, field => Assert.Contains(field, loaded));
        // automation is a Profile field with no row in reference.md's table (it is documented on its own guide pages instead).
        Assert.Equal(documented.ToHashSet(), loaded.Where(field => field != "automation").ToHashSet());
    }

    [Fact]
    public void The_golden_mapping_uses_every_recall_retain_and_invalidate_placeholder()
    {
        var provider = Profile.Load(GoldenPath).Memory!.Single();

        Assert.Equal(MemoryMapping.RecallPlaceholders, PlaceholdersOf(provider.Tools.Recall.Arguments));
        Assert.Equal(MemoryMapping.RetainPlaceholders, PlaceholdersOf(provider.Tools.Retain!.Arguments));
        Assert.Equal(MemoryMapping.InvalidatePlaceholders, PlaceholdersOf(provider.Tools.Invalidate!.Arguments));
    }

    private static HashSet<string> PlaceholdersOf(IReadOnlyDictionary<string, System.Text.Json.JsonElement> arguments) =>
        [.. arguments.Values.Select(v => v.GetString()).Select(s => Regex.Match(s!, @"^\{(\w+)\}$")).Where(m => m.Success).Select(m => m.Groups[1].Value)];

    /// <summary>The field names of <c>docs/guide/reference.md</c>'s "Profile fields" table, in the order the table lists them.</summary>
    private static List<string> DocumentedProfileFields()
    {
        var text = File.ReadAllText(Repo.Path("docs", "guide", "reference.md"));
        var start = text.IndexOf("## Profile fields", StringComparison.Ordinal);
        var end = text.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        var section = text[start..(end < 0 ? text.Length : end)];
        return [.. Regex.Matches(section, @"^\| `([a-z_]+)` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];
    }

    /// <summary>The JSON keys <see cref="Profile.Load"/> reads, from the same source-generated metadata it deserializes with (ADR 0060): no
    /// reflection. <c>Properties</c> also lists get-only computed properties (<c>roots</c>, <c>runtime_blocks</c>, ...); <c>Set</c> is null for those,
    /// since they have no setter to bind a constructor argument to, so excluding them leaves exactly the fields a profile file can set.</summary>
    private static HashSet<string> LoaderProfileFields() =>
        [.. Profile.LoadJson.GetTypeInfo(typeof(Profile)).Properties.Where(p => p.Set is not null).Select(p => p.Name)];
}
