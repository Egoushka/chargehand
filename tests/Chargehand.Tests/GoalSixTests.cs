using System.Text.Json;
using System.Text.RegularExpressions;
using Chargehand.ClaudeCode;
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Mcp;
using Chargehand.RunLog;
using ModelContextProtocol.Client;
using static Chargehand.Tests.McpMemoryProviderTests;
using static Chargehand.Tests.MemoryConfigTests;

namespace Chargehand.Tests;

/// <summary>
/// Goal 0.6 done bar (ADR 0034, spec decision 1): a run recalls from two stacked memory providers, retains only claims whose
/// citations resolved, and a preset gives workers a service through MCP. Real MCP servers, the real adapter, stack, resolver
/// and orchestrator; the second memory server speaks legacy SSE over a loopback port and answers as Chronicle does.
/// </summary>
public class GoalSixTests
{
    private const string DocsService = "    services:\n      - server: team-docs\n        tools: [search_docs, read_doc]\n";

    private const string TwoClaims = """
        ```json
        {"status":"completed","summary":"SUMMARY-MARKER","claims":[
           {"text":"The README says hello.","evidence":["e1"],"confidence":0.9},
           {"text":"The caller says v1 shipped.","evidence":["e2"],"confidence":0.7}],
         "evidence":[{"id":"e1","kind":"file","locator":"README.md:1"},{"id":"e2","kind":"input","locator":"rel-v1"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    /// <summary>Hindsight seen through a gateway that prefixes each backend's tools with its name.</summary>
    private const string GatewayHindsight = """
        {"name":"hindsight","server":"gateway","namespace":"chargehand","retain":true,"tools":{
          "recall":{"tool":"hindsight_recall","arguments":{"query":"{query}","bank_id":"{namespace}","budget":"low","max_tokens":1024}},
          "retain":{"tool":"hindsight_retain","arguments":{"content":"{text}","context":"{context}","document_id":"{document_id}","timestamp":"{timestamp}","tags":"{tags}","bank_id":"{namespace}"}}}}
        """;

    private static string Secret(string item) => item switch
    {
        "gateway-token" => "gateway-value",
        "docs-token" => "docs-value",
        _ => throw new InvalidOperationException($"no secret source resolved '{item}'"),
    };

    /// <summary>The profile as a user writes it and <see cref="Profile.Load"/> validates it: a gateway, Chronicle over SSE, a docs service.</summary>
    private static Profile Loaded(string workerRoot, Uri chronicleUrl)
    {
        using var dir = new TempDir();
        var loaded = Profile.Load(dir.Write("p.json", $$$$"""
            {"schema":"profile/v1",
             "mcp_servers":{
               "gateway":{"url":"https://mcp.example.internal/mcp","headers":{"Authorization":"Bearer {secret:gateway-token}"}},
               "chronicle":{"url":"{{{{chronicleUrl}}}}","transport":"sse"},
               "team-docs":{"url":"https://mcp.example.internal/docs","headers":{"Authorization":"Bearer {secret:docs-token}"}}},
             "memory":[{{{{GatewayHindsight}}}},{{{{Chronicle}}}}]}
            """));
        return Runs.Profile(workerRoot) with { McpServers = loaded.McpServers, Memory = loaded.Memory };
    }

    /// <summary>In-process servers by name; any other is reached over the network with the pool's own transport, and a refused one is down.</summary>
    private static McpConnectionPool Pool(Profile profile, Dictionary<string, FakeMcpServer> inProcess, params string[] refused) =>
        new(profile.McpServers!, Secret, async (name, settings, _) =>
        {
            if (refused.Contains(name))
                throw new IOException("connection refused");
            return inProcess.TryGetValue(name, out var fake) ? await fake.TransportAsync() : new HttpClientTransport(McpConnectionPool.HttpOptions(name, settings, Secret));
        });

    private static FakeMcpServer Gateway() => new(
        new FakeTool("hindsight_recall", _ => FakeMcpServer.Text("""{"results":[{"id":"f1","text":"Deploys go through GitOps."}]}""")),
        new FakeTool("hindsight_retain", _ => FakeMcpServer.Text("queued")));

    private static FakeMcpServer Docs() => new(
        new FakeTool("search_docs", _ => FakeMcpServer.Text("x"), ReadOnly: true),
        new FakeTool("read_doc", _ => FakeMcpServer.Text("x")),
        new FakeTool("write_note", _ => FakeMcpServer.Text("x")));

    private static Task<FakeSseMcpServer> ChronicleServer() => FakeSseMcpServer.StartAsync(new FakeTool("recall", _ => ChronicleAnswer()));

    private static RunRequest Request(RepositoryRef repo) =>
        new("request/v1", "What does the README say?", new RequestContext(false, "docs", Repository: repo), [new CallerInput("rel-v1", "signal", "Released v1.")]);

    [Fact]
    public async Task A_run_recalls_from_two_stacked_providers_retains_only_checked_claims_and_gives_a_worker_a_service()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", DocsService);
        var repo = Runs.GitRepo(root.Path);
        await using var hindsight = Gateway();
        await using var docs = Docs();
        await using var chronicle = await ChronicleServer();
        var profile = Loaded(root.Path, chronicle.SseEndpoint);
        await using var pool = Pool(profile, new() { ["gateway"] = hindsight, ["team-docs"] = docs });
        var runtime = new ScriptedRuntime(TwoClaims);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));

        var result = await new Orchestrator(profile, runtime, "2.0.16", presets.Path, log, new Dictionary<string, int>(), MemoryStacks.From(profile, pool), new ServiceResolver(pool))
            .RunAsync(Request(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);

        // 1. Two stacked providers, in profile order, each fact labelled with its source. Chronicle's facts read date and summary, fall
        //    back to the raw text where the summary is null, and skip an empty segment; its limit is the entry's fact cap.
        var prompt = Assert.Single(runtime.Prompts);
        Assert.Equal(["- [hindsight] Deploys go through GitOps.",
                      "- [chronicle] 2024-05-03T10:12:00: Agreed to repaint the flat in June.",
                      "- [chronicle] 2023-11-20T18:40:00: raw conversation two"],
            prompt.Split('\n').Where(l => l.StartsWith("- [", StringComparison.Ordinal)));
        Assert.Equal(["memory/recall/hindsight", "memory/recall/chronicle"], result.PromptChain.Blocks.Where(b => b.Source == BlockSource.Runtime).Select(b => b.Name));
        var asked = Assert.Single(chronicle.Calls);
        Assert.Equal(("recall", 10), (asked.Tool, asked.Arguments["limit"].GetInt32()));
        Assert.NotEmpty(chronicle.Authorizations);                                                                // reached over the real SSE transport ...
        Assert.All(chronicle.Authorizations, a => Assert.Equal("", a));                                          // ... with no credential, as it asks for none

        // 2. Only the claim whose citation resolved to the repository is retained, with its locator, the repository and the commit.
        //    Chronicle is recall-only and is never written to.
        var kept = Assert.Single(hindsight.Calls, c => c.Tool == "hindsight_retain").Arguments["content"].GetString()!;
        Assert.Contains($"commit {repo.Commit[..12]}", kept, StringComparison.Ordinal);
        Assert.Contains("(citations checked at this commit)", kept, StringComparison.Ordinal);
        Assert.Contains("- The README says hello. [README.md:1]", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("caller says", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("SUMMARY-MARKER", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("What does the README say?", kept, StringComparison.Ordinal);

        // 3. The preset's service reaches the worker's node spec, and only the tools it names.
        var grant = Assert.Single(runtime.Created.Single().Services!);
        Assert.Equal("team-docs", grant.Server);
        Assert.Equal(["read_doc", "search_docs"], grant.Tools);
        Assert.Equal(["write_note"], grant.Hidden);

        // The run log says what happened, and holds no credential.
        var extensions = (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!;
        Assert.Equal(["memory hindsight: recalled 1, retained 1", "memory chronicle: recalled 2, retained 0", "service team-docs: granted read_doc, search_docs"], extensions.Lines());
        Assert.DoesNotContain("-value", await File.ReadAllTextAsync(Path.Combine(root.Path, "log.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stopped_memory_server_is_skipped_and_the_run_completes_with_the_other()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", DocsService);
        var repo = Runs.GitRepo(root.Path);
        await using var hindsight = Gateway();
        await using var chronicle = await ChronicleServer();
        var profile = Loaded(root.Path, chronicle.SseEndpoint);
        await using var pool = Pool(profile, new() { ["gateway"] = hindsight }, refused: "chronicle");
        var runtime = new ScriptedRuntime(TwoClaims);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));

        var result = await new Orchestrator(profile, runtime, "2.0.16", presets.Path, log, new Dictionary<string, int>(), MemoryStacks.From(profile, pool))
            .RunAsync(Request(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var prompt = Assert.Single(runtime.Prompts);
        Assert.Contains("- [hindsight] Deploys go through GitOps.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("[chronicle]", prompt, StringComparison.Ordinal);
        Assert.Equal(["memory/recall/hindsight"], result.PromptChain.Blocks.Where(b => b.Source == BlockSource.Runtime).Select(b => b.Name));
        var report = (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Memory;
        Assert.Equal((1, null), (report[0].Recalled, report[0].RecallSkipped));
        Assert.Equal("chronicle", report[1].Source);
        Assert.NotNull(report[1].RecallSkipped);
        Assert.Empty(chronicle.Calls);
    }

    [Fact]
    public async Task A_preset_gives_a_claude_code_worker_its_service_in_a_private_config_and_names_only_the_granted_tools()
    {
        using var root = new TempDir();
        using var cli = new TempDir();
        using var configs = new TempDir();
        using var presets = new PresetRoot("docs", DocsService);
        var repo = Runs.GitRepo(root.Path);
        await using var docs = Docs();
        var profile = Runs.Profile(root.Path) with
        {
            McpServers = new Dictionary<string, McpServerSettings>
            {
                ["team-docs"] = new(Url: "https://mcp.example.internal/docs", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {secret:docs-token}" }),
            },
        };
        await using var pool = Pool(profile, new() { ["team-docs"] = docs });
        var runtime = await ClaudeCodeWorkerRuntime.ConnectAsync(FakeClaude(cli), "2.1.195", new ClaudeCodeCredential("k", false), CancellationToken.None);
        runtime.ConfigRoot = configs.Path;
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));

        var result = await new Orchestrator(profile, runtime, "2.1.195", presets.Path, log, new Dictionary<string, int>(), null, new ServiceResolver(pool))
            .RunAsync(Request(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var args = File.ReadAllLines(Path.Combine(cli.Path, "args.txt"));
        Assert.Contains("--strict-mcp-config", args);
        var allowed = Values(args, "--allowedTools");
        Assert.Contains("mcp__team-docs__search_docs", allowed);
        Assert.Contains("mcp__team-docs__read_doc", allowed);
        Assert.DoesNotContain("mcp__team-docs__write_note", allowed);
        Assert.Contains("mcp__team-docs__write_note", Values(args, "--disallowedTools"));                         // the server's other tool is kept out
        Assert.DoesNotContain("docs-value", string.Join(' ', args), StringComparison.Ordinal);                  // no credential on the command line
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(cli.Path, "mcp.json")));
        var server = config.RootElement.GetProperty("mcpServers").GetProperty("team-docs");
        Assert.Equal(("http", "https://mcp.example.internal/docs", "Bearer docs-value"),
            (server.GetProperty("type").GetString(), server.GetProperty("url").GetString(), server.GetProperty("headers").GetProperty("Authorization").GetString()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(configs.Path));                                        // the file went with the turn
        Assert.Equal(["service team-docs: granted read_doc, search_docs"], (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Lines());
    }

    /// <summary>The guide's profile example is what a user copies: it must load, with both providers and its mappings validated.</summary>
    [Fact]
    public void The_guides_profile_example_loads_with_both_providers()
    {
        var guide = File.ReadAllText(Repo.Path("docs", "guide", "memory-and-services.md"));
        var example = Regex.Matches(guide, "```json\n(.*?)```", RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).Single(block => block.Contains("\"profile/v1\"", StringComparison.Ordinal));
        using var dir = new TempDir();

        var profile = Profile.Load(dir.Write("p.json", example.Replace("<port>", "8080", StringComparison.Ordinal)));

        var servers = profile.McpServers!;
        Assert.Equal(["gateway", "chronicle"], servers.Keys);
        Assert.Equal("sse", servers["chronicle"].Transport);
        var memory = profile.Memory!;
        Assert.Equal(["hindsight", "chronicle"], memory.Select(m => m.Name));
        Assert.False(memory[0].Retain);
        Assert.NotNull(memory[0].Tools.Retain);
        Assert.Null(memory[1].Tools.Retain);
    }

    /// <summary>The values that follow a variadic flag, up to the next flag.</summary>
    private static string[] Values(string[] args, string flag) => [.. args.Skip(Array.IndexOf(args, flag) + 1).TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal))];

    /// <summary>A stand-in <c>claude</c>: intake (the call with <c>--no-session-persistence</c>) gets a Task Spec, a worker turn gets a
    /// stream that connects the granted server and ends with the README claim; argv and the <c>--mcp-config</c> file are kept while it exists.</summary>
    private static string FakeClaude(TempDir dir)
    {
        var final = Runs.WorkerReply;
        dir.Write("intake.json", JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = ScriptedRuntime.Spec() }));
        dir.Write("events.jsonl", string.Join('\n',
            """{"type":"system","subtype":"init","mcp_servers":[{"name":"team-docs","status":"connected"}]}""",
            JsonSerializer.Serialize(new
            {
                type = "assistant",
                message = new { id = "msg_1", content = new[] { new { type = "text", text = final } }, usage = new { input_tokens = 10, output_tokens = 20, cache_read_input_tokens = 0, cache_creation_input_tokens = 0 } },
            }),
            JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result = final })) + "\n");
        var script = dir.Write("claude", $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "2.1.195 (Claude Code)"; exit 0; fi
            cat > /dev/null
            case " $* " in
              *" --no-session-persistence "*) cat "{dir.Path}/intake.json"; exit 0;;
            esac
            printf '%s\n' "$@" > "{dir.Path}/args.txt"
            prev=""
            for a in "$@"; do
              if [ "$prev" = "--mcp-config" ]; then cp "$a" "{dir.Path}/mcp.json"; fi
              prev="$a"
            done
            cat "{dir.Path}/events.jsonl"
            """);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
