# Services and memory (goal 0.6) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A run recalls from several MCP memory servers at once, retains only claims whose citations resolved, and gives
its workers read-only tools from the MCP servers a preset names, on the profile's say-so and with nothing connected by
default.

**Architecture:** A new project `Chargehand.Mcp` holds the MCP client code: a connection pool over profile `mcp_servers`
(Streamable HTTP, legacy SSE or stdio),
a memory adapter driven by a declarative tool mapping, and a service resolver. The core (`Chargehand`) gains records and
interfaces only: a `MemoryStack` (fan-out recall with source labels, retain), the retain rule, `ServiceGrant`,
`IServiceResolver`, and an optional `services` list on a preset's node kind. The two runtime adapters read the grant from
`NodeSpec` and hand the servers to the worker. `HindsightMemory` goes once the MCP adapter reproduces it.

**Tech Stack:** .NET 10 / xUnit, `ModelContextProtocol.Core` 2.2.0 (client), JSON Schema (profile and preset), YAML
presets, bash and Python 3 (spike only).

**Spec:** `docs/specs/2026-09-29-services-and-memory-design.md`. **ADR:** `docs/adr/0034-memory-and-services-over-mcp.md`
(accepted, 2026-09-29).

## Global Constraints

- The repository is public: never commit IP addresses, hostnames, absolute home paths, employer or project names, key
  aliases, tracker URLs or prompts from real runs. Example hosts are `*.example.internal`; a loopback server such as Chronicle's is written `http://localhost:<port>/sse` in docs.
- Done for every task: `scripts/check.sh` exits 0 and its last test line reads `Passed!  - Failed:     0`.
- Conventional Commits; never `--no-verify`; never force-push. A PR title may end with the tracker key.
- Public text says "citations checked", not "claims verified". The retain rule reads "retains only claims whose citations
  resolved".
- Contracts change by addition only. This goal changes none of `request/v1`, `result/v1`, `run-status/v1`. `preset/v1`
  gains an optional `services` (Task 8); `.claude/settings.json` asks before edits under `schemas/preset/v1/`, and
  `SchemaCompatTests` must stay green.
- The MCP SDK stays out of `Chargehand` and `Chargehand.Contracts`: client types live in `src/Chargehand.Mcp` only.
- Presets are read-only (ADR 0006). No shipped preset lists `services`; the example is in the guide.
- Memory and services fail open; secrets fail closed (spec, "Failure behaviour"). Catch every exception except the
  caller's own cancellation. A record that holds a secret overrides `ToString`.
- Tests need no network and no MCP child process: `FakeMcpServer` (Task 2) is a real MCP server on in-process pipes.
- Sandbox: `dotnet test` and `dotnet build` may need `dangerouslyDisableSandbox: true` (MSBuild node pipes); so do
  network `git` and `gh`.
- A file is edited by one task at a time where the table says so; where two tasks touch `Orchestrator.cs`
  (Tasks 3, 6 and 8, different regions), the later merge rebases.

## Tasks and dependencies

| # | Task | Depends on | Can run alongside |
|---|---|---|---|
| 1 | Spike: MCP tools for workers on both runtimes | none | 2, 3 |
| 2 | `Chargehand.Mcp` project: connection pool, `mcp_servers` (HTTP, SSE, stdio), secret placeholders | none | 1, 3 |
| 3 | `MemoryStack`: fan-out recall, source labels, provenance, fail-open | none | 1, 2 |
| 4 | Memory list and tool mapping in the profile (Hindsight and Chronicle entries) | 2, 3 | 6, 8 |
| 5 | `McpMemoryProvider`: the mapping executor (Hindsight- and Chronicle-shaped fakes) | 2, 4 | 9, 10 |
| 6 | Retain only claims whose citations resolved | 3 | 4, 8 |
| 7 | Wire the stack into the CLI and server; `chargehand extensions check` | 3, 4, 5, 8 | 9, 10 |
| 8 | Services: preset `services`, resolver, grants on `NodeSpec` | 2 | 4, 6 |
| 9 | Claude Code gives workers the granted services | 1, 8 | 5, 7, 10 |
| 10 | OpenCode gives workers the granted services (may slip) | 1, 8 | 5, 7, 9 |
| 11 | Delete `HindsightMemory`; reject the object form of `memory` | 6, 7, and the maintainer's live parity run | none |
| 12 | Acceptance test (Chronicle-shaped second provider), guide with both mappings, changelog, roadmap | 6, 7, 9, 11 (10 if it ships) | none |

Order: `{1, 2, 3}` then `{4, 6, 8}` then `{5, 9, 10}` then `{7}` then `{11}` then `{12}`. (Tasks 7 and 8 both edit
`Program.cs`: Task 8 creates the connection pool there and Task 7 reuses it.) Task 10 can slip past the goal
if decided item 1 chooses Claude Code first; the done bar needs one runtime.

## Review Focus

1. A memory provider that throws any kind of exception, or hangs, never aborts or fails the run, and the others still
   contribute. Pinned by `MemoryStackTests.A_failing_source_is_skipped_for_every_exception_kind`,
   `MemoryStackTests.A_hanging_source_is_skipped_at_its_timeout` and
   `MemoryRunTests.A_run_survives_every_kind_of_provider_failure` (Task 3). The caller's own cancellation still
   propagates (`MemoryStackTests.The_runs_own_cancellation_propagates`).
2. A fact two providers return appears once, labelled with both (`MemoryStackTests.A_fact_two_sources_return_appears_once_with_both_names`).
   A fact with newlines cannot forge a second labelled line (`MemoryStackTests.A_fact_is_one_line`). Recalled text is
   untrusted prompt content; the defence is the "unverified" header and read-only tools.
3. A secret no source resolves means no connection, and the message names the item and never a value
   (`McpConnectionPoolTests.An_unresolved_secret_means_no_connection_and_names_the_item`). A secret in a URL is refused at
   load (`ProfileTests.Invalid_mcp_servers_fail_at_load`).
4. Retain writes only claims that cite a `file` or `commit` entry and survive the scrubber; never the request text or the
   summary; nothing without a commit (`RetainableClaimsTests`, `MemoryRunTests.A_run_retains_only_qualifying_claims`, Task 6).
5. With no memory and no services configured, the prompt, the chain and `tools_sha256` equal today's
   (`MemoryRunTests.A_run_without_memory_is_unchanged`, `ServiceRunTests.No_services_leave_the_tools_hash_alone`).
6. A preset names a server or tool that is missing or unreachable: that service is dropped, the run continues, the issue is
   in the run log (`ServiceResolverTests`, `ServiceRunTests.A_dropped_service_does_not_fail_the_run`, Task 8).
7. Service credentials never reach argv, logs or results: the Claude Code config is a 0600 file outside the checkout,
   removed after the session; `ToString` of a grant hides them (`ClaudeCodeServicesTests`, `ServiceGrantTests`).
8. A tool the preset did not grant is not callable by the worker on either runtime (Claude Code: `dontAsk` plus explicit
   `--allowedTools`; OpenCode: generated deny rules, per the spike). Pinned live by the maintainer in Task 12; the unit
   tests pin the arguments and rules generated.
9. After Task 11 a profile carrying the object form of `memory` fails at load with a migration message, not a skip
   (`ProfileTests.The_object_form_of_memory_fails_with_the_migration`).

---

### Task 1: Spike: MCP tools for workers on both runtimes

Settles every UNKNOWN in the spec's "Where it stands" 8 and 9 before adapter code exists. No production code; the output
is evidence in ADR 0034 and, if needed, a change to decided item 1. It calls a small model a few times on the
maintainer's own runtime credentials (cents); run it by hand, outside the sandbox.

**Files:**
- Create: `scripts/fake-mcp-server.py`
- Modify: `docs/adr/0034-memory-and-services-over-mcp.md` (an "Evidence (task 1 spike)" section)
- Modify: `docs/specs/2026-09-29-services-and-memory-design.md` (a dated note under decided item 1)

**Interfaces:**
- Consumes: `claude` 2.1.283 (`ClaudeCodeSettings.PinnedVersion`), OpenCode 2.0.18 started as ADR 0030 does
  (`scripts/opencode-serve.sh`, an isolated state directory), `chargehand serve` for an HTTP MCP endpoint with a bearer key.
- Produces: the answers below, recorded as a table (question, command, observation) in the ADR.

- [ ] **Step 1: Write the stand-in stdio server** (stdlib only, newline-delimited JSON-RPC)

```python
#!/usr/bin/env python3
"""Stdio MCP server for the goal 0.6 spike: two tools, no dependencies. Messages are newline-delimited JSON-RPC."""
import json
import sys

TOOLS = [
    {"name": "echo_fact", "description": "Returns a fixed fact.",
     "inputSchema": {"type": "object", "properties": {"topic": {"type": "string"}}},
     "annotations": {"readOnlyHint": True}},
    {"name": "write_note", "description": "Pretends to write a note.",
     "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}}},
]


def reply(id_, result=None, error=None):
    message = {"jsonrpc": "2.0", "id": id_}
    message["error" if error else "result"] = error or result
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


for line in sys.stdin:
    request = json.loads(line)
    method, id_ = request.get("method"), request.get("id")
    if method == "initialize":
        reply(id_, {"protocolVersion": request["params"]["protocolVersion"], "capabilities": {"tools": {}},
                    "serverInfo": {"name": "fake", "version": "0"}})
    elif method == "tools/list":
        reply(id_, {"tools": TOOLS})
    elif method == "tools/call":
        reply(id_, {"content": [{"type": "text", "text": request["params"]["name"] + " ok: spike fact"}]})
    elif id_ is not None:
        reply(id_, error={"code": -32601, "message": "method not found"})
```

- [ ] **Step 2: Claude Code 2.1.283.** In a scratch git repository, run
  `claude -p --output-format stream-json --verbose --strict-mcp-config --mcp-config <cfg> --permission-mode dontAsk …`
  with `<cfg>` = `{"mcpServers":{"fake":{"command":"python3","args":["<path>/fake-mcp-server.py"]}}}` on a small model, the
  prompt on stdin. Record, from the `system`/`init` event and the tool events:

| # | Question | How |
|---|---|---|
| C1 | Does `init.tools` list `mcp__fake__echo_fact` and `mcp__fake__write_note` with `--tools "Read"` and `--allowedTools mcp__fake__echo_fact`? What is the exact spelling for a server named `team-docs`? | read `init.tools`; repeat with the server renamed |
| C2 | Is a call to the allowed tool made and answered; is a call to `write_note` refused, and how does the refusal read? | prompt: "call echo_fact, then write_note, then say DONE" |
| C3 | Does `--disallowedTools mcp__fake__write_note` remove it from `init.tools`? | compare `init.tools` |
| C4 | Does `--bare` (API-key mode) accept `--mcp-config`? Does `--setting-sources ""` (subscription mode)? | run both if both credentials exist; else record "not run" |
| C5 | Does an `http` entry with `headers` connect? | `{"type":"http","url":"http://localhost:<port>/v1/mcp","headers":{"Authorization":"Bearer <key>"}}` against `chargehand serve`; read `init.mcp_servers[].status` with the right key and a wrong one |
| C6 | How many tokens do the two tool schemas add to the first call, granted or not? | `usage.input_tokens` with and without the config |

- [ ] **Step 3: OpenCode 2.0.18.** Start the server as `scripts/opencode-serve.sh` does, with a fresh state directory and a
  small model. With Basic auth (user `opencode`), for a scratch checkout `<dir>`:

| # | Question | How |
|---|---|---|
| O1 | Does `PUT /api/experimental/mcp/fake` with `{"config":{"type":"local","command":["python3","<path>"]}}` return 204, and when does `GET /api/mcp` say connected? | poll `GET /api/mcp` |
| O2 | How is the `location` given (`location[directory]` query, or the `x-opencode-directory` header), and is the server visible from another location? | `GET /api/mcp` for `<dir>` and `<dir2>` |
| O3 | What `action` and `resources` does a pending permission request carry for an MCP tool? | session with rule `* * ask`; prompt to call `echo_fact`; read `GET /api/session/{id}/permission` |
| O4 | With `[* * allow]` alone, is the MCP tool callable? (Spec finding 9.) | same prompt, rules `[* * allow]` |
| O5 | With `[* * allow, <action from O3> * deny]`, is the tool gone from the catalog or refused? | same prompt |
| O6 | Two sessions at one location, one denying and one allowing: do their rulesets stay separate? | two sessions, same prompt |
| O7 | Does `DELETE /api/experimental/mcp/fake` end availability in a running session? | delete, prompt again |
| O8 | Does a `remote` entry send `headers`? | `chargehand serve` as the remote, right and wrong key; read the status |

- [ ] **Step 4: Record and decide.** Fill the ADR's evidence section with the two tables, each row: the observation and
  the pinned version. Then update ADR 0034's "Delivery of services to OpenCode" line from provisional to the observed
  rule, and add to the spec, under decided item 1, one dated line: "Task 1 result: OpenCode can / cannot gate MCP tools
  per session (O3 to O6)". If O4 shows tools of foreign servers reach workers today, open a bug for the preset compile step
  (Task 10 generates the deny rules; the bug covers presets with no `services`).

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add scripts/fake-mcp-server.py docs/adr/0034-memory-and-services-over-mcp.md docs/specs/2026-09-29-services-and-memory-design.md
git commit -m "docs: record how Claude Code and OpenCode take MCP servers for workers (spike)"
```

---

### Task 2: `Chargehand.Mcp` project: connection pool, `mcp_servers` (HTTP, SSE, stdio), secret placeholders

**Files:**
- Create: `src/Chargehand.Mcp/Chargehand.Mcp.csproj`
- Create: `src/Chargehand.Mcp/McpConnectionPool.cs`, `src/Chargehand.Mcp/SecretTemplate.cs`, `src/Chargehand.Mcp/McpUnavailableException.cs`
- Modify: `src/Chargehand/Config/Profile.cs` (`McpServerSettings`, `Profile.McpServers`, load-time validation, command-source timeout)
- Modify: `profiles/profile.schema.json`, `profiles/example.json`
- Modify: `Chargehand.slnx`, `src/Chargehand.Cli/Chargehand.Cli.csproj`, `tests/Chargehand.Tests/Chargehand.Tests.csproj` (project references)
- Create: `tests/Chargehand.Tests/FakeMcpServer.cs`, `tests/Chargehand.Tests/FakeSseMcpServer.cs`, `tests/Chargehand.Tests/McpConnectionPoolTests.cs`, `tests/Chargehand.Tests/SecretTemplateTests.cs`
- Modify: `tests/Chargehand.Tests/ProfileTests.cs`, `tests/Chargehand.Tests/ConfigFileTests.cs`

**Interfaces:**
- Consumes: `Profile.Secret(item)` (`Profile.cs:70-80`), `ChargehandException`, `ModelContextProtocol.Core` 2.2.0.
- Produces:
  - `McpServerSettings(string? Url = null, IReadOnlyDictionary<string,string>? Headers = null, IReadOnlyList<string>? Command = null, IReadOnlyDictionary<string,string>? Env = null, string? Transport = null)` (`Transport`: `auto`, the default, `streamable-http` or `sse`, only with `Url`); `Profile.McpServers` (`IReadOnlyDictionary<string, McpServerSettings>?`, JSON `mcp_servers`).
  - `McpConnectionPool(IReadOnlyDictionary<string, McpServerSettings> servers, Func<string,string> secret, Func<string, McpServerSettings, CancellationToken, Task<IClientTransport>>? transports = null)` : `IAsyncDisposable`, with `Task<McpClient> GetAsync(string server, CancellationToken ct)`; `internal static StdioClientTransportOptions StdioOptions(string name, McpServerSettings s, Func<string,string> secret)` and `internal static HttpClientTransportOptions HttpOptions(...)`.
  - `SecretTemplate.Resolve(string template, Func<string,string> secret)`.
  - `McpUnavailableException(string code, string server, string detail)` (a plain `Exception`; `Code` is `unknown_server`, `secret_unresolved` or `unreachable`; `Message` is `"<server>: <detail>"`; the detail never holds a secret value).
  - `Profile.CommandTimeout` (internal static `TimeSpan`, default 15 s).
  - `FakeMcpServer` / `FakeTool` test helpers (used by Tasks 5, 7, 8) and `FakeSseMcpServer`, the SDK's own legacy SSE endpoint on a loopback port (used here and by Task 12).
  - `McpConnectionPool.HttpOptions` sets `HttpClientTransportOptions.TransportMode` from `Transport`: `auto` → `AutoDetect`, `streamable-http` → `StreamableHttp`, `sse` → `Sse`.

- [ ] **Step 1: Write the failing tests**

The helper is a real MCP server on two in-process pipes (the shape of `StdioMcpTests`; this exact file compiled and
passed a scratch test on 2026-09-29):

```csharp
// tests/Chargehand.Tests/FakeMcpServer.cs
using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chargehand.Tests;

/// <param name="Schema">The tool's input schema as JSON text; none: an empty object schema.</param>
/// <param name="ReadOnly">The tool's readOnlyHint; null leaves the hint out.</param>
internal sealed record FakeTool(string Name, Func<IDictionary<string, JsonElement>, CallToolResult> Handle, string? Description = null, string? Schema = null, bool? ReadOnly = null);

/// <summary>A real MCP server on two in-process pipes (the shape StdioMcpTests uses), serving scripted tools and recording calls.</summary>
internal sealed class FakeMcpServer : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();

    public FakeMcpServer(params FakeTool[] tools)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new());
        builder.Services.AddMcpServer()
            .WithStreamServerTransport(_toServer.Reader.AsStream(), _toClient.Writer.AsStream())
            .WithTools([.. tools.Select(Build)]);
        _host = builder.Build();
    }

    /// <summary>Every call the server received, in order.</summary>
    public List<(string Tool, IDictionary<string, JsonElement> Arguments)> Calls { get; } = [];

    public static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    public static CallToolResult Error(string text) => new() { IsError = true, Content = [new TextContentBlock { Text = text }] };

    /// <summary>Starts the server and returns the client end of its pipes.</summary>
    public async Task<IClientTransport> TransportAsync()
    {
        await _host.StartAsync();
        return new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream());
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private McpServerTool Build(FakeTool t)
    {
        var tool = McpServerTool.Create((RequestContext<CallToolRequestParams> ctx) =>
        {
            var arguments = ctx.Params!.Arguments ?? new Dictionary<string, JsonElement>();
            lock (Calls)
                Calls.Add((t.Name, arguments));
            return t.Handle(arguments);
        }, new McpServerToolCreateOptions { Name = t.Name, Description = t.Description ?? t.Name });
        if (t.Schema is not null)
            tool.ProtocolTool.InputSchema = JsonDocument.Parse(t.Schema).RootElement.Clone();
        if (t.ReadOnly is { } ro)
            tool.ProtocolTool.Annotations = new ToolAnnotations { ReadOnlyHint = ro };
        return tool;
    }
}
```

The legacy SSE helper is the SDK's own server with `EnableLegacySse` on, served by Kestrel on a free loopback port (that
option is obsolete, `MCP9004`, so the helper suppresses the warning; this server and the client in both `Sse` and
`AutoDetect` mode passed a scratch test on 2026-09-29):

```csharp
// tests/Chargehand.Tests/FakeSseMcpServer.cs
#pragma warning disable MCP9004
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chargehand.Tests;

/// <summary>An MCP server on the legacy SSE transport (/sse and /message), on a free loopback port, like Chronicle's.</summary>
internal sealed class FakeSseMcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeSseMcpServer(WebApplication app, Uri address) => (_app, Address) = (app, address);

    public Uri Address { get; }

    /// <summary>The URL a profile would give: the /sse endpoint.</summary>
    public Uri SseEndpoint => new(Address, "/sse");

    public static async Task<FakeSseMcpServer> StartAsync(string toolName, string reply)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => { o.Stateless = false; o.EnableLegacySse = true; })
            .WithTools([McpServerTool.Create((RequestContext<CallToolRequestParams> _) => new CallToolResult { Content = [new TextContentBlock { Text = reply }] },
                new McpServerToolCreateOptions { Name = toolName, Description = toolName })]);
        var app = builder.Build();
        app.MapMcp();
        await app.StartAsync();
        return new FakeSseMcpServer(app, new Uri(app.Urls.First()));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
```

```csharp
// tests/Chargehand.Tests/SecretTemplateTests.cs
using Chargehand.Mcp;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: {secret:item} in MCP server headers and environment values, resolved through the profile's chain.</summary>
public class SecretTemplateTests
{
    private static string Secret(string item) =>
        item == "tok" ? "s3cret" : throw new InvalidOperationException($"no secret source resolved '{item}'");

    [Fact]
    public void Every_placeholder_is_replaced() =>
        Assert.Equal("Bearer s3cret and s3cret", SecretTemplate.Resolve("Bearer {secret:tok} and {secret:tok}", Secret));

    [Fact]
    public void Text_without_a_placeholder_passes_through() =>
        Assert.Equal("plain {not:a-secret}", SecretTemplate.Resolve("plain {not:a-secret}", Secret));

    [Fact]
    public void An_unresolved_item_propagates_naming_the_item_only()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SecretTemplate.Resolve("Bearer {secret:missing}", Secret));
        Assert.Contains("missing", e.Message);
        Assert.DoesNotContain("s3cret", e.Message);
    }

    [Theory]
    [InlineData("{secret:tok}", true)]
    [InlineData("a {secret:tok} b", true)]
    [InlineData("{query}", false)]
    public void HasSecret_finds_placeholders(string text, bool expected) => Assert.Equal(expected, SecretTemplate.HasSecret(text));
}
```

```csharp
// tests/Chargehand.Tests/McpConnectionPoolTests.cs
using Chargehand.Config;
using Chargehand.Mcp;
using ModelContextProtocol.Client;

namespace Chargehand.Tests;

public class McpConnectionPoolTests
{
    private static string Secret(string item) =>
        item == "tok" ? "s3cret" : throw new InvalidOperationException($"no secret source resolved '{item}'");

    private static IReadOnlyDictionary<string, McpServerSettings> Servers(string name = "gw", string token = "tok") => new Dictionary<string, McpServerSettings>
    {
        [name] = new(Url: "https://mcp.example.internal/mcp", Headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {{secret:{token}}}" }),
    };

    [Fact]
    public async Task A_server_is_opened_once_and_reused()
    {
        var fakes = new List<FakeMcpServer>();
        await using var pool = new McpConnectionPool(Servers(), Secret, async (_, _, _) =>
        {
            var fake = new FakeMcpServer(new FakeTool("ping", _ => FakeMcpServer.Text("pong")));
            fakes.Add(fake);
            return await fake.TransportAsync();
        });

        var first = await pool.GetAsync("gw", CancellationToken.None);
        var second = await pool.GetAsync("gw", CancellationToken.None);

        Assert.Same(first, second);
        Assert.Single(fakes);
        await fakes[0].DisposeAsync();
    }

    [Fact]
    public async Task A_closed_connection_is_opened_again_on_the_next_request()
    {
        var fakes = new List<FakeMcpServer>();
        await using var pool = new McpConnectionPool(Servers(), Secret, async (_, _, _) =>
        {
            var fake = new FakeMcpServer(new FakeTool("ping", _ => FakeMcpServer.Text("pong")));
            fakes.Add(fake);
            return await fake.TransportAsync();
        });

        var first = await pool.GetAsync("gw", CancellationToken.None);
        await first.DisposeAsync();
        var second = await pool.GetAsync("gw", CancellationToken.None);

        Assert.NotSame(first, second);
        Assert.Equal(2, fakes.Count);
        foreach (var f in fakes)
            await f.DisposeAsync();
    }

    [Fact]
    public async Task An_unknown_server_is_unavailable_and_says_which()
    {
        await using var pool = new McpConnectionPool(Servers(), Secret);
        var e = await Assert.ThrowsAsync<McpUnavailableException>(() => pool.GetAsync("nope", CancellationToken.None));
        Assert.Equal("unknown_server", e.Code);
        Assert.Contains("nope", e.Message);
    }

    [Fact]
    public async Task An_unresolved_secret_means_no_connection_and_names_the_item()
    {
        await using var pool = new McpConnectionPool(Servers(token: "missing"), Secret,
            (_, _, _) => throw new Xunit.Sdk.XunitException("must not connect without the credential"));
        var e = await Assert.ThrowsAsync<McpUnavailableException>(() => pool.GetAsync("gw", CancellationToken.None));
        Assert.Equal("secret_unresolved", e.Code);
        Assert.Contains("missing", e.Message);
        Assert.DoesNotContain("s3cret", e.Message);
    }

    [Fact]
    public void Http_options_carry_the_resolved_headers()
    {
        var options = McpConnectionPool.HttpOptions("gw", Servers()["gw"], Secret);
        Assert.Equal(new Uri("https://mcp.example.internal/mcp"), options.Endpoint);
        Assert.Equal("Bearer s3cret", options.AdditionalHeaders!["Authorization"]);
    }

    [Theory]
    [InlineData(null, HttpTransportMode.AutoDetect)]
    [InlineData("auto", HttpTransportMode.AutoDetect)]
    [InlineData("streamable-http", HttpTransportMode.StreamableHttp)]
    [InlineData("sse", HttpTransportMode.Sse)]
    public void Http_options_follow_the_transport_setting(string? transport, HttpTransportMode expected)
    {
        var settings = new McpServerSettings(Url: "http://localhost:8031/sse", Transport: transport);

        Assert.Equal(expected, McpConnectionPool.HttpOptions("chronicle", settings, Secret).TransportMode);
    }

    [Theory]
    [InlineData("sse")]
    [InlineData("auto")]
    public async Task A_legacy_sse_server_is_reached_in_sse_mode_and_in_auto_detect(string transport)
    {
        await using var server = await FakeSseMcpServer.StartAsync("recall", """{"results":[]}""");
        var servers = new Dictionary<string, McpServerSettings> { ["chronicle"] = new(Url: server.SseEndpoint.ToString(), Transport: transport) };
        await using var pool = new McpConnectionPool(servers, Secret);   // the default transports: the real HTTP client

        var client = await pool.GetAsync("chronicle", CancellationToken.None);

        Assert.Equal("recall", Assert.Single(await client.ListToolsAsync()).Name);
    }

    [Fact]
    public void Stdio_options_carry_the_declared_env_and_do_not_inherit_ours()
    {
        var settings = new McpServerSettings(Command: ["npx", "-y", "example-notes-mcp"], Env: new Dictionary<string, string> { ["NOTES_TOKEN"] = "{secret:tok}" });
        var options = McpConnectionPool.StdioOptions("notes", settings, Secret);
        Assert.Equal("npx", options.Command);
        Assert.Equal(["-y", "example-notes-mcp"], options.Arguments);
        Assert.False(options.InheritEnvironmentVariables);
        Assert.Equal("s3cret", options.EnvironmentVariables!["NOTES_TOKEN"]);
    }
}
```

Additions to `ProfileTests` (the profile already loads through `Profile.Load`, `Profile.cs:56-59`):

```csharp
    [Fact]
    public void Mcp_servers_load_by_name()
    {
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("p.json", """
            {"schema":"profile/v1","mcp_servers":{
              "memory-gateway":{"url":"https://mcp.example.internal/mcp","headers":{"Authorization":"Bearer {secret:memory-gateway-token}"}},
              "team-notes":{"command":["npx","-y","example-notes-mcp"],"env":{"NOTES_TOKEN":"{secret:team-notes-token}"}}}}
            """));

        Assert.Equal("https://mcp.example.internal/mcp", profile.McpServers!["memory-gateway"].Url);
        Assert.Equal(["npx", "-y", "example-notes-mcp"], profile.McpServers["team-notes"].Command);
    }

    [Theory]
    [InlineData("""{"mcp_servers":{"gw":{"url":"https://a.example.internal/mcp","command":["x"]}}}""")]      // both transports
    [InlineData("""{"mcp_servers":{"gw":{}}}""")]                                                             // neither
    [InlineData("""{"mcp_servers":{"gw":{"url":"https://a.example.internal/mcp?key={secret:k}"}}}""")]      // a secret in a URL
    [InlineData("""{"mcp_servers":{"Bad_Name":{"url":"https://a.example.internal/mcp"}}}""")]               // name pattern
    [InlineData("""{"mcp_servers":{"gw":{"command":["x"],"headers":{"a":"b"}}}}""")]                        // headers on stdio
    public void Invalid_mcp_servers_fail_at_load(string body)
    {
        using var dir = new TempDir();
        var path = dir.Write("p.json", body.Replace("{\"mcp_servers\"", "{\"schema\":\"profile/v1\",\"mcp_servers\"", StringComparison.Ordinal));

        var e = Assert.Throws<ChargehandException>(() => Profile.Load(path));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.False(string.IsNullOrEmpty(e.Action));
    }

    [Fact]
    public void A_url_server_may_name_the_sse_transport()
    {
        using var dir = new TempDir();
        var profile = Profile.Load(dir.Write("p.json", """{"schema":"profile/v1","mcp_servers":{"chronicle":{"url":"http://localhost:8031/sse","transport":"sse"}}}"""));

        Assert.Equal("sse", profile.McpServers!["chronicle"].Transport);
    }

    [Theory]
    [InlineData("""{"mcp_servers":{"gw":{"url":"http://localhost:8031/sse","transport":"websocket"}}}""")]   // not a known transport
    [InlineData("""{"mcp_servers":{"gw":{"command":["x"],"transport":"sse"}}}""")]                          // transport belongs to url servers
    public void An_invalid_transport_fails_at_load(string body)
    {
        using var dir = new TempDir();
        var path = dir.Write("p.json", body.Replace("{\"mcp_servers\"", "{\"schema\":\"profile/v1\",\"mcp_servers\"", StringComparison.Ordinal));

        var e = Assert.Throws<ChargehandException>(() => Profile.Load(path));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
    }

    [Fact]
    public void A_command_source_that_hangs_falls_through_to_the_next_source()
    {
        var profile = new Profile("profile/v1", Secrets: [new SecretSource(Command: ["sleep", "30"]), new SecretSource(Command: ["echo", "from-second"])]);
        Profile.CommandTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            Assert.Equal("from-second", profile.Secret("anything"));
        }
        finally
        {
            Profile.CommandTimeout = TimeSpan.FromSeconds(15);
        }
    }
```

and in `ConfigFileTests`:

```csharp
    [Fact]
    public void The_schema_rejects_an_unknown_transport()
    {
        using var doc = JsonDocument.Parse("""{"schema":"profile/v1","mcp_servers":{"gw":{"url":"http://localhost:8031/sse","transport":"websocket"}}}""");
        Assert.False(ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Fact]
    public void The_schema_rejects_a_server_with_both_transports()
    {
        using var doc = JsonDocument.Parse("""{"schema":"profile/v1","mcp_servers":{"gw":{"url":"https://a.example.internal/mcp","command":["x"]}}}""");
        Assert.False(ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "SecretTemplateTests|McpConnectionPoolTests|ProfileTests|ConfigFileTests"`
Expected: FAIL to compile (`Chargehand.Mcp` does not exist).

- [ ] **Step 3: Implement**

- `Chargehand.Mcp.csproj`: `ProjectReference` to `..\Chargehand\Chargehand.csproj`; `PackageReference ModelContextProtocol.Core 2.2.0`;
  `InternalsVisibleTo Chargehand.Tests`. Add it to `Chargehand.slnx`, to the CLI and to the test project.
- `McpServerSettings` and `Profile.McpServers` in `Profile.cs`; `Profile.Load` calls a `Validate` that throws
  `ChargehandException(ErrorCode.InvalidRequest, message, action)` for: not exactly one of `url` and `command`; `headers`
  or `transport` on a `command` server, or `env` on a `url` server; a `transport` other than `auto`, `streamable-http` or
  `sse`; a `{secret:` in `url` or `command`; a name outside `^[a-z][a-z0-9-]*$`.
- `profile.schema.json`: `mcp_servers` (object, `propertyNames` pattern, each `oneOf` [`url`+optional `headers` and optional `transport` (enum `auto`, `streamable-http`, `sse`),
  `command`+optional `env`], `additionalProperties: false`); add a sample to `example.json` with `example.internal` hosts.
- `SecretTemplate`: regex `\{secret:([A-Za-z0-9._-]+)\}`; `Resolve` replaces each through the function and lets its
  `InvalidOperationException` (which names the item) propagate.
- `McpConnectionPool`: a `ConcurrentDictionary<string, Lazy<Task<McpClient>>>`; `GetAsync` returns the live client, drops
  an entry whose `Completion` task has finished, and wraps every failure (unknown name, unresolved secret, transport
  error, initialisation timeout) in `McpUnavailableException` (`unknown_server`; `secret_unresolved` carrying the item name;
  `unreachable` carrying the transport's message), the detail scrubbed with `ChargehandException.Scrub`. Default transports: `HttpClientTransport` for `url` (`TransportMode` from `transport`: `AutoDetect` when
  unset or `auto`, `StreamableHttp`, `Sse`), `StdioClientTransport` for `command`
  (`InheritEnvironmentVariables = false`, `StandardErrorLines` to stderr). `DisposeAsync` disposes every client.
- `Profile.RunCommand`: run with `CommandTimeout` (internal static, 15 s); on timeout kill the process tree and return
  null so the chain moves on. Its message never holds output.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "SecretTemplateTests|McpConnectionPoolTests|ProfileTests|ConfigFileTests"` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add src/Chargehand.Mcp src/Chargehand/Config/Profile.cs profiles Chargehand.slnx src/Chargehand.Cli/Chargehand.Cli.csproj tests/Chargehand.Tests
git commit -m "feat: add the MCP connection pool, mcp_servers and secret placeholders"
```

---

### Task 3: `MemoryStack`: fan-out recall, source labels, provenance, fail-open

**Files:**
- Create: `src/Chargehand/Memory/MemoryStack.cs` (`MemorySource`, `MemoryLimits`, `RecallOutcome`, `SourceRecall`, `MemoryStack`)
- Modify: `src/Chargehand/Orchestrator.cs` (constructor takes `MemoryStack?`; `Recall` and retain go through it; a collector for the run-log report)
- Modify: `src/Chargehand/RunLog/IRunLog.cs` (`RunRecord.Extensions`, `ExtensionsReport`)
- Modify: `src/Chargehand.Cli/Program.cs` (build a one-source stack from the object form of `memory`; `show` prints the report)
- Create: `tests/Chargehand.Tests/MemoryStackTests.cs`, `tests/Chargehand.Tests/MemoryRunTests.cs`
- Modify: `tests/Chargehand.Tests/RunLogTests.cs`
- Modify: `tests/Chargehand.Tests/MemoryFailOpenTests.cs` (#94's tests move onto the stack; `Failure` becomes `internal`)

**Interfaces:**
- Consumes: `IMemoryProvider`, `MemoryScope`, `RecalledMemory` (`IMemoryProvider.cs`), `ChainBlock`, `BlockSource`, `PromptBlock.Hash`,
  `ChargehandException.Scrub`.
- Produces:
  - `MemoryLimits(int MaxFacts = 10, int MaxChars = 4000, TimeSpan? Timeout = null, int MaxFactChars = 600)`.
  - `MemorySource(string Name, IMemoryProvider Provider, MemoryScope Scope, MemoryLimits Limits, bool Retain = false, IReadOnlyList<string>? RetainTags = null)`.
  - `MemoryStack(IReadOnlyList<MemorySource> sources)` with `IReadOnlyList<MemorySource> Sources`, `Task<RecallOutcome> RecallAsync(string query, CancellationToken ct)` and
    `Task<IReadOnlyList<RetainReport>> RetainAsync(MemoryItem item, CancellationToken ct)` (Task 6 changes the item's content, not this signature).
  - `RecallOutcome(IReadOnlyList<SourceRecall> Sources, string Prompt, IReadOnlyList<ChainBlock> Blocks)`; `SourceRecall(string Source, IReadOnlyList<RecalledMemory> Items, string? SkippedReason)`; `RetainReport(string Source, int Claims, string? SkippedReason)`.
  - `ExtensionsReport(IReadOnlyList<MemoryReport> Memory, IReadOnlyList<ServiceReport> Services)` and `IReadOnlyList<string> Lines()` (the text `chargehand show` prints: `memory <source>: recalled <n>, retained <n>`, with `recall skipped (<reason>)` or `retain skipped (<reason>)` in place of a count when a step was skipped), with `MemoryReport(string Source, int Recalled, string? RecallSkipped, int Retained, string? RetainSkipped)` and `ServiceReport(string Server, IReadOnlyList<string> Tools, IReadOnlyList<string> Issues)` (services filled by Task 8); `RunRecord` gains a trailing `ExtensionsReport? Extensions = null`.
  - `MemoryStack.ForObjectForm(MemorySettings settings, IMemoryProvider provider)`: the one-source stack named `hindsight` that keeps today's behaviour until Task 11.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Chargehand.Tests/MemoryStackTests.cs
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Memory;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: recall fans out to every provider; one failing or slow provider never stops the others or the run.</summary>
public class MemoryStackTests
{
    internal sealed class Fake(Func<string, IReadOnlyList<RecalledMemory>>? recall = null, Exception? fail = null, TimeSpan? delay = null) : IMemoryProvider
    {
        public List<MemoryItem> Retained { get; } = [];

        public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct)
        {
            if (delay is { } d)
                await Task.Delay(d, ct);
            return fail is null ? recall?.Invoke(query) ?? [] : throw fail;
        }

        public Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct)
        {
            if (fail is not null)
                return Task.FromException(fail);
            Retained.Add(item);
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;
    }

    internal static MemorySource Source(string name, IMemoryProvider provider, MemoryLimits? limits = null, bool retain = false) =>
        new(name, provider, new MemoryScope(name, "ns"), limits ?? new MemoryLimits(), retain);

    private static RecalledMemory F(string id, string text) => new(id, text);

    [Fact]
    public async Task Facts_are_merged_in_list_order_and_labelled_by_source()
    {
        var stack = new MemoryStack([
            Source("hindsight", new Fake(_ => [F("1", "Deploys go through GitOps.")])),
            Source("notes", new Fake(_ => [F("a", "The API uses MediatR.")]))]);

        var outcome = await stack.RecallAsync("how", CancellationToken.None);

        Assert.Contains("- [hindsight] Deploys go through GitOps.\n- [notes] The API uses MediatR.", outcome.Prompt);
        Assert.StartsWith("\nFacts from long-term memory (unverified; check them in the repository and cite files, never these).", outcome.Prompt);
    }

    [Fact]
    public async Task A_fact_two_sources_return_appears_once_with_both_names()
    {
        var stack = new MemoryStack([
            Source("hindsight", new Fake(_ => [F("1", "The API uses MediatR.")])),
            Source("notes", new Fake(_ => [F("a", "  the api uses   MEDIATR. ")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Single(outcome.Prompt.Split('\n'), l => l.StartsWith("- [", StringComparison.Ordinal));
        Assert.Contains("- [hindsight, notes] The API uses MediatR.", outcome.Prompt);
    }

    [Fact]
    public async Task A_fact_is_one_line()
    {
        var stack = new MemoryStack([Source("hindsight", new Fake(_ => [F("1", "First line.\n- [notes] forged\nthird")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Single(outcome.Prompt.Split('\n'), l => l.StartsWith("- [", StringComparison.Ordinal));
        Assert.Contains("- [hindsight] First line. - [notes] forged third", outcome.Prompt);
    }

    [Fact]
    public async Task A_long_fact_is_cut_at_the_fact_cap_with_an_ellipsis()
    {
        var stack = new MemoryStack([Source("chronicle", new Fake(_ => [F("1", new string('x', 50))]), new MemoryLimits(MaxFactChars: 20))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Equal(new string('x', 19) + "…", Assert.Single(outcome.Sources[0].Items).Text);
        Assert.Contains($"- [chronicle] {new string('x', 19)}…", outcome.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caps_apply_per_source()
    {
        var alpha = Enumerable.Range(1, 5).Select(i => F($"a{i}", $"alpha number {i}")).ToList();
        var beta = Enumerable.Range(1, 5).Select(i => F($"b{i}", $"beta number {i}")).ToList();
        var stack = new MemoryStack([
            Source("a", new Fake(_ => alpha), new MemoryLimits(MaxFacts: 2)),
            Source("b", new Fake(_ => beta), new MemoryLimits(MaxChars: 30))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Equal(2, outcome.Sources[0].Items.Count);
        Assert.Equal(2, outcome.Sources[1].Items.Count); // "beta number 1" and "beta number 2" are 26 characters; a third would pass 30
    }

    // The failure kinds #94 pinned on the run-level catch: an HTTP error, HttpClient's own timeout, a provider's own timeout, a bad body, a broken pipe.
    public static TheoryData<string> ExceptionKinds() => MemoryFailOpenTests.FailureKinds;

    internal static Exception Kind(string kind) => MemoryFailOpenTests.Failure(kind);

    [Theory]
    [MemberData(nameof(ExceptionKinds))]
    public async Task A_failing_source_is_skipped_for_every_exception_kind(string kind)
    {
        var stack = new MemoryStack([
            Source("broken", new Fake(fail: Kind(kind))),
            Source("notes", new Fake(_ => [F("a", "The API uses MediatR.")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.NotNull(outcome.Sources[0].SkippedReason);
        Assert.Empty(outcome.Sources[0].Items);
        Assert.Contains("- [notes] The API uses MediatR.", outcome.Prompt);
    }

    [Fact]
    public async Task A_hanging_source_is_skipped_at_its_timeout()
    {
        var stack = new MemoryStack([
            Source("slow", new Fake(_ => [F("1", "never")], delay: TimeSpan.FromSeconds(30)), new MemoryLimits(Timeout: TimeSpan.FromMilliseconds(100))),
            Source("notes", new Fake(_ => [F("a", "fast")]))]);

        var started = DateTimeOffset.UtcNow;
        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Contains("timed out", outcome.Sources[0].SkippedReason);
        Assert.Contains("- [notes] fast", outcome.Prompt);
    }

    [Fact]
    public async Task The_runs_own_cancellation_propagates()
    {
        var stack = new MemoryStack([Source("slow", new Fake(_ => [], delay: TimeSpan.FromSeconds(30)))]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stack.RecallAsync("q", cts.Token));
    }

    [Fact]
    public async Task Sources_are_asked_at_the_same_time()
    {
        // Each provider waits until the other has been called; sequential asking would deadlock until the test's timeout.
        var both = new TaskCompletionSource();
        var started = 0;
        Task Arrive()
        {
            if (Interlocked.Increment(ref started) == 2)
                both.SetResult();
            return both.Task;
        }

        var stack = new MemoryStack([Source("a", new BarrierProvider(Arrive)), Source("b", new BarrierProvider(Arrive))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(outcome.Sources, s => Assert.Null(s.SkippedReason));
    }

    private sealed class BarrierProvider(Func<Task> arrive) : IMemoryProvider
    {
        public async Task<IReadOnlyList<RecalledMemory>> RecallAsync(string query, MemoryScope scope, CancellationToken ct)
        {
            await arrive();
            return [];
        }

        public Task RetainAsync(MemoryItem item, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;

        public Task InvalidateAsync(string id, string reason, MemoryScope scope, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Provenance_is_one_runtime_block_per_contributing_source()
    {
        var stack = new MemoryStack([
            Source("hindsight", new Fake(_ => [F("1", "Deploys go through GitOps.")])),
            Source("empty", new Fake(_ => [])),
            Source("notes", new Fake(_ => [F("a", "The API uses MediatR.")]))]);

        var outcome = await stack.RecallAsync("q", CancellationToken.None);

        Assert.Equal(["memory/recall/hindsight", "memory/recall/notes"], outcome.Blocks.Select(b => b.Name));
        Assert.All(outcome.Blocks, b => Assert.Equal(BlockSource.Runtime, b.Source));
        Assert.Equal(PromptBlock.Hash("- [hindsight] Deploys go through GitOps."), outcome.Blocks[0].Sha256);
    }

    [Fact]
    public async Task No_facts_give_an_empty_prompt_and_no_blocks()
    {
        var outcome = await new MemoryStack([Source("a", new Fake(_ => []))]).RecallAsync("q", CancellationToken.None);

        Assert.Equal("", outcome.Prompt);
        Assert.Empty(outcome.Blocks);
    }

    [Fact]
    public async Task Retain_reaches_only_sources_that_retain_and_never_throws()
    {
        var keeps = new Fake();
        var skips = new Fake();
        var broken = new Fake(fail: new IOException("closed"));
        var stack = new MemoryStack([Source("keeps", keeps, retain: true), Source("skips", skips), Source("broken", broken, retain: true)]);

        var reports = await stack.RetainAsync(new MemoryItem("fact", DocumentId: "run-1"), CancellationToken.None);

        Assert.Single(keeps.Retained);
        Assert.Empty(skips.Retained);
        Assert.Equal(["keeps", "broken"], reports.Select(r => r.Source));
        Assert.Null(reports[0].SkippedReason);
        Assert.NotNull(reports[1].SkippedReason);
    }
}
```


```csharp
// tests/Chargehand.Tests/MemoryRunTests.cs
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Memory;
using Chargehand.RunLog;
using static Chargehand.Tests.MemoryStackTests;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: whole runs on a ScriptedRuntime with a memory stack; no test drove a memory run before.</summary>
public class MemoryRunTests
{
    private static Orchestrator Make(string root, ScriptedRuntime runtime, MemoryStack? stack, IRunLog log) =>
        new(Runs.Profile(root), runtime, "2.0.16", Repo.Root, log, new Dictionary<string, int>(), stack);

    [Fact]
    public async Task A_run_puts_labelled_facts_in_the_prompt_and_their_blocks_in_the_chain()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var stack = new MemoryStack([
            Source("hindsight", new Fake(_ => [new RecalledMemory("f1", "Deploys go through GitOps.")])),
            Source("notes", new Fake(_ => [new RecalledMemory("n1", "The API uses MediatR.")]))]);

        var result = await Make(root.Path, runtime, stack, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"))).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var prompt = Assert.Single(runtime.Prompts);
        Assert.Contains("- [hindsight] Deploys go through GitOps.", prompt);
        Assert.Contains("- [notes] The API uses MediatR.", prompt);
        Assert.Equal(["memory/recall/hindsight", "memory/recall/notes"], result.PromptChain.Blocks.Where(b => b.Source == BlockSource.Runtime).Select(b => b.Name));
    }

    [Theory]
    [MemberData(nameof(ExceptionKinds), MemberType = typeof(MemoryStackTests))]
    public async Task A_run_survives_every_kind_of_provider_failure(string kind)
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var stack = new MemoryStack([Source("broken", new Fake(fail: Kind(kind)), retain: true)]);

        var result = await Make(root.Path, new ScriptedRuntime(Runs.WorkerReply), stack, log).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var report = (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Memory.Single();
        Assert.Equal("broken", report.Source);
        Assert.NotNull(report.RecallSkipped);
        Assert.NotNull(report.RetainSkipped);
    }

    [Fact]
    public async Task A_run_without_memory_is_unchanged()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);

        var result = await Make(root.Path, runtime, null, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"))).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.DoesNotContain("long-term memory", Assert.Single(runtime.Prompts), StringComparison.Ordinal);
        Assert.DoesNotContain(result.PromptChain.Blocks, b => b.Source == BlockSource.Runtime);
    }

    [Fact]
    public void The_object_form_of_memory_becomes_one_source_named_hindsight()
    {
        var stack = MemoryStack.ForObjectForm(new MemorySettings("hindsight", "http://memory.example.internal:8888", "ns", Retain: true), new Fake());

        var source = Assert.Single(stack.Sources);
        Assert.Equal(("hindsight", true), (source.Name, source.Retain));
    }
}
```

`MemoryStackTests.Fake`, `Source`, `Kind` and `ExceptionKinds` are `internal` in the first file so the second shares them. Add to `RunLogTests`:

```csharp
    [Fact]
    public async Task An_extensions_report_round_trips_and_prints_one_line_per_source()
    {
        using var dir = new TempDir();
        var log = new JsonlRunLog(System.IO.Path.Combine(dir.Path, "log.jsonl"));
        var chain = new PromptChain([], new AsSent("v", "build", "m", "2026-09-29"));
        var result = new ResultContract("result/v1", "run-x", "n1", new string('0', 32), chain, ResultStatus.Completed, "s", [], [], [], [], 0.5, new Usage(0, 0, 0, 0, 0));
        var report = new ExtensionsReport([new MemoryReport("notes", 2, null, 1, null), new MemoryReport("broken", 0, "timed out after 10 s", 0, "not retained")], []);
        await log.AppendAsync(new RunRecord("run-x", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "cheap", "answer", null, result, "answer", report), CancellationToken.None);

        var stored = (await log.ReadAsync("run-x", CancellationToken.None)).Run!.Extensions!;

        Assert.Equal(2, stored.Memory[0].Recalled);
        Assert.Equal(["memory notes: recalled 2, retained 1", "memory broken: recall skipped (timed out after 10 s), retain skipped (not retained)"], stored.Lines());
    }

    [Fact]
    public async Task A_run_record_written_before_the_report_existed_still_reads()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Path, "log.jsonl");
        var chain = new PromptChain([], new AsSent("v", "build", "m", "2026-09-29"));
        var result = new ResultContract("result/v1", "run-y", "n1", new string('0', 32), chain, ResultStatus.Completed, "s", [], [], [], [], 0.5, new Usage(0, 0, 0, 0, 0));
        var log = new JsonlRunLog(path);
        await log.AppendAsync(new RunRecord("run-y", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "cheap", "answer", null, result), CancellationToken.None);

        Assert.Null((await log.ReadAsync("run-y", CancellationToken.None)).Run!.Extensions);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "MemoryStackTests|MemoryRunTests|RunLogTests"`
Expected: FAIL to compile (`MemoryStack`, `ExtensionsReport` do not exist).

- [ ] **Step 3: Implement**

- `MemoryStack.RecallAsync`: start every source's call with `Task.WhenAll`, each in `try { … } catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))`
  (the rule #94 put in `Orchestrator.MemoryFailedOpen`, which moves here) under
  `CancellationTokenSource.CreateLinkedTokenSource(ct)` with `CancelAfter(limits.Timeout ?? 10 s)`; when that timeout token
  fired the reason is `"timed out after 10 s"`, otherwise `ChargehandException.Scrub(e.Message)` cut to 200 characters (a
  provider's own `OperationCanceledException` is an ordinary failure). Then merge as in the spec: one line per fact (collapse
  whitespace, cut at `MaxFactChars` with `…` as the last of those characters), `MaxFacts` and `MaxChars` per source, dedupe on trimmed case-insensitive text, extra sources added to the label.
- `Prompt` is `"\n" + header + "\n" + lines + "\n"` and `""` when no line survives; the header is the text in the test.
  `Blocks`: per source that contributed a line first, `new ChainBlock($"memory/recall/{name}", "1", PromptBlock.Hash(thoseLines), BlockSource.Runtime)`;
  the hash covers the final rendered lines (labels included) that source contributed first; a source that only repeats
  another's facts gets no block of its own, and its name stays in the merged label.
- `SourceRecall.Items` are the items that passed the source's caps, before duplicates are dropped; the report's `Recalled` count
  is `Items.Count`.
- `RetainAsync(item, ct)`: for each source with `Retain`, call the provider under the same timeout and catch rule;
  `RetainReport(name, 1, null)` or `(name, 0, reason)`.
- `Orchestrator`: the last constructor parameter becomes `MemoryStack? memory = null`; `Recall` becomes one call to
  `memory.RecallAsync(request.Text, ct)`; the retain block calls `memory.RetainAsync(sameItemAsToday, ct)` (Task 6 replaces
  the item); both `catch (Exception e) when (MemoryFailedOpen(e, ct))` blocks and the helper go, because the stack owns the
  rule. A small `ExtensionsCollector` object created in `RunAsync` and passed to `Execute` gathers the recall and retain
  reports and sets the span tags `chargehand.memory.<name>.recalled` and `.error` (#94's `chargehand.memory.error` and
  `chargehand.memory.recalled` were per run; there is now one provider per name); `RunAsync` puts `collector.ToReport()` in
  the `RunRecord`. `profile.Memory` is no longer read here.
- `MemoryFailOpenTests` (#94) moves onto the stack. Its `Orchestrator` helper builds the run from a stack instead of a profile
  with a memory object, so the tests keep their five failure kinds, both steps and the two cancellation cases:

```csharp
    private static Orchestrator Orchestrator(FakeMemory memory, string workerRoot)
    {
        var stack = new MemoryStack([new MemorySource("hindsight", memory, new MemoryScope("hindsight", "ns"), new MemoryLimits(), Retain: true)]);
        return new Orchestrator(Runs.Profile(workerRoot), new ScriptedRuntime(Runs.DraftReply), "2.0.16", Repo.Root,
            new JsonlRunLog(Path.Combine(workerRoot, "log.jsonl")), new Dictionary<string, int>(), stack);
    }
```

  Its assertions change in three places: the span tags `chargehand.memory.hindsight.error` and
  `chargehand.memory.hindsight.recalled`, and `b.Name.StartsWith("memory/recall", StringComparison.Ordinal)` for the chain
  block. `Failure(kind)` becomes `internal`, and `MemoryStackTests` reuses it and `FailureKinds`. The retain cases keep
  running on a draft request until Task 6 (which retains nothing without a commit) moves them onto a checkout.
- `Program.cs`: `Memory()` returns `MemoryStack.ForObjectForm(m, new HindsightMemory(…))` for the object form, as today's
  construction; `Show` prints `run.Extensions?.Lines()`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "MemoryStackTests|MemoryRunTests|RunLogTests|OrchestratorActionTests|HindsightMemoryTests"` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add src/Chargehand src/Chargehand.Cli/Program.cs tests/Chargehand.Tests
git commit -m "feat: fan recall out to every memory provider and label facts by source"
```

---

### Task 4: Memory list and tool mapping in the profile

**Files:**
- Modify: `src/Chargehand/Config/Profile.cs` (`MemoryBlock`, `MemoryProviderSettings`, `MemoryTools`, `ToolCall`, `ResultMapping`, converter)
- Create: `src/Chargehand/Config/MemoryMapping.cs` (placeholder sets and `Validate`)
- Modify: `profiles/profile.schema.json`, `profiles/example.json`
- Modify: `src/Chargehand.Cli/Program.cs` (read `profile.Memory?.ObjectForm`)
- Create: `tests/Chargehand.Tests/MemoryConfigTests.cs`
- Modify: `tests/Chargehand.Tests/ConfigFileTests.cs`

**Interfaces:**
- Consumes: `McpServerSettings` and `Profile.McpServers` (Task 2), `MemorySettings` (the object form, kept until Task 11).
- Produces:
  - `Profile.Memory` becomes `MemoryBlock?` = `MemoryBlock(IReadOnlyList<MemoryProviderSettings> Providers, MemorySettings? ObjectForm)`; JSON `memory` is an array (Providers) or, until Task 11, the old object (ObjectForm).
  - `MemoryProviderSettings(string Name, string Server, MemoryTools Tools, string? Namespace = null, int? MaxFacts = null, int? MaxChars = null, int? MaxFactChars = null, int? TimeoutSeconds = null, bool Retain = false, IReadOnlyList<string>? RetainTags = null)` with `EffectiveNamespace => Namespace ?? Name` (a recall-only source such as Chronicle has no banks).
  - `MemoryTools(ToolCall Recall, ToolCall? Retain = null, ToolCall? Invalidate = null)`; `ToolCall(string Tool, IReadOnlyDictionary<string, JsonElement> Arguments, ResultMapping? Results = null)`; `ResultMapping(string? Path = null, string Id = "id", IReadOnlyList<string>? Text = null, string Format = "json")`, where JSON `text` is a field name, a template over the result's fields (`"{date}: {summary}"`) or an array of these, and a missing `Text` means `["text"]`.
  - `MemoryMapping.Validate(MemoryProviderSettings, IReadOnlyDictionary<string, McpServerSettings>) : IReadOnlyList<string>` (problems; empty when valid). `Profile.Load` throws `ChargehandException(InvalidRequest)` listing them, with an action pointing at the guide.
  - Allowed placeholders: recall `{query}` `{namespace}` `{max_facts}`; retain `{namespace}` `{text}` `{context}` `{document_id}` `{timestamp}` `{tags}`; invalidate `{namespace}` `{id}` `{reason}`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Chargehand.Tests/MemoryConfigTests.cs
using Chargehand.Config;
using Chargehand.Contracts;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: memory is an ordered list of providers, each a server plus a declarative tool mapping.</summary>
public class MemoryConfigTests
{
    internal const string Hindsight = """
        {"name":"hindsight","server":"gw","namespace":"chargehand","tools":{
          "recall":{"tool":"recall","arguments":{"query":"{query}","bank_id":"{namespace}","budget":"low","max_tokens":1024}},
          "retain":{"tool":"retain","arguments":{"content":"{text}","context":"{context}","document_id":"{document_id}","timestamp":"{timestamp}","tags":"{tags}","bank_id":"{namespace}"}},
          "invalidate":{"tool":"invalidate_memory","arguments":{"memory_id":"{id}","reason":"{reason}","bank_id":"{namespace}"}}},
          "retain":true}
        """;

    private const string Gateway = """{"gw":{"url":"https://mcp.example.internal/mcp"}}""";

    private static Profile Load(string memory, string servers = Gateway)
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("p.json", $$"""{"schema":"profile/v1","mcp_servers":{{servers}},"memory":{{memory}}}"""));
    }

    [Fact]
    public void A_memory_list_loads_with_its_mapping()
    {
        var provider = Load($"[{Hindsight}]").Memory!.Providers.Single();

        Assert.Equal(("hindsight", "gw", "chargehand", true), (provider.Name, provider.Server, provider.Namespace, provider.Retain));
        Assert.Equal("recall", provider.Tools.Recall.Tool);
        Assert.Equal("low", provider.Tools.Recall.Arguments["budget"].GetString());
        Assert.Equal(1024, provider.Tools.Recall.Arguments["max_tokens"].GetInt32());
        Assert.Equal("invalidate_memory", provider.Tools.Invalidate!.Tool);
        Assert.Null(provider.MaxFacts);
    }

    [Fact]
    public void A_results_mapping_names_the_array_and_the_fields()
    {
        var entry = """{"name":"notes","server":"gw","namespace":"n","tools":{"recall":{"tool":"search_notes","arguments":{"q":"{query}"},"results":{"path":"notes","id":"key","text":"body"}}}}""";

        var results = Load($"[{entry}]").Memory!.Providers.Single().Tools.Recall.Results!;

        Assert.Equal(("notes", "key", "json"), (results.Path, results.Id, results.Format));
        Assert.Equal(["body"], results.Text);
    }

    /// <summary>Chronicle's MCP server (github.com/Egoushka/chronicle): legacy SSE, recall only, no banks.</summary>
    internal const string Chronicle = """
        {"name":"chronicle","server":"chronicle","tools":{"recall":{"tool":"recall","arguments":{"query":"{query}","limit":"{max_facts}"},
          "results":{"path":"results","id":"segment_id","text":["{date}: {summary}","{date}: {text}"]}}}}
        """;

    private const string ChronicleServer = """{"chronicle":{"url":"http://localhost:8031/sse","transport":"sse"}}""";

    [Fact]
    public void A_recall_only_provider_loads_without_a_namespace_and_reads_a_list_of_text_templates()
    {
        var provider = Load($"[{Chronicle}]", ChronicleServer).Memory!.Providers.Single();

        Assert.Null(provider.Namespace);
        Assert.Equal("chronicle", provider.EffectiveNamespace);
        Assert.Null(provider.Tools.Retain);
        Assert.False(provider.Retain);
        Assert.Equal("{max_facts}", provider.Tools.Recall.Arguments["limit"].GetString());
        Assert.Equal(["{date}: {summary}", "{date}: {text}"], provider.Tools.Recall.Results!.Text);
    }

    [Fact]
    public void The_object_form_still_loads_until_the_hindsight_client_goes()
    {
        var memory = Load("""{"backend":"hindsight","url":"http://memory.example.internal:8888","namespace":"ns"}""", "{}").Memory!;

        Assert.Empty(memory.Providers);
        Assert.Equal("ns", memory.ObjectForm!.Namespace);
    }

    [Theory]
    [InlineData("""[{"name":"a","server":"nope","namespace":"n","tools":{"recall":{"tool":"r","arguments":{}}}}]""", "nope")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{"q":"{queryy}"}}}}]""", "{queryy}")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{"q":"{text}"}}}}]""", "{text}")]
    [InlineData("""[{"name":"a","server":"gw","tools":{"recall":{"tool":"r","arguments":{}},"retain":{"tool":"w","arguments":{"n":"{max_facts}"}}}}]""", "{max_facts}")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","retain":true,"tools":{"recall":{"tool":"r","arguments":{}}}}]""", "retain")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{},"results":{"format":"xml"}}}}]""", "format")]
    [InlineData("""[{"name":"Bad_Name","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{}}}}]""", "name")]
    [InlineData("""[{"name":"a","server":"gw","namespace":"n","tools":{"recall":{"tool":"r","arguments":{}}}},{"name":"a","server":"gw","namespace":"m","tools":{"recall":{"tool":"r","arguments":{}}}}]""", "duplicate")]
    public void An_invalid_memory_entry_fails_at_load_and_says_why(string memory, string fragment)
    {
        var e = Assert.Throws<ChargehandException>(() => Load(memory));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains(fragment, e.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(e.Action));
    }
}
```

Add to `ConfigFileTests` (the `ProfileSchema` field is already there):

```csharp
    [Fact]
    public void A_recall_only_provider_with_no_namespace_passes_the_schema()
    {
        using var doc = JsonDocument.Parse("""{"schema":"profile/v1","memory":[{"name":"chronicle","server":"chronicle","tools":{"recall":{"tool":"recall","arguments":{"query":"{query}","limit":"{max_facts}"},"results":{"path":"results","id":"segment_id","text":["{date}: {summary}","{date}: {text}"]}}}}]}""");
        Assert.True(ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }

    [Fact]
    public void A_memory_provider_without_a_recall_tool_fails_the_schema()
    {
        using var doc = JsonDocument.Parse("""{"schema":"profile/v1","memory":[{"name":"a","server":"gw","namespace":"n","tools":{}}]}""");
        Assert.False(ProfileSchema.Evaluate(doc.RootElement).IsValid);
    }
```

`Example_profile_validates` keeps passing once `example.json`'s `memory` is a list with the Hindsight entry and its
`mcp_servers` gateway (placeholders only).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "MemoryConfigTests|ConfigFileTests"`
Expected: FAIL to compile (`MemoryBlock`, `MemoryProviderSettings` do not exist).

- [ ] **Step 3: Implement**

- `ResultMapping.Text` has a `JsonConverter` that reads a string as a one-item list and an array as is.
- `MemoryBlock` with a `JsonConverter` on `Profile.Memory`: an array deserialises to `Providers`, an object to
  `ObjectForm` (the existing `MemorySettings`), anything else throws `JsonException`.
- `MemoryMapping.Validate` returns one problem per fault: unknown `server` (message names the key and lists the defined
  ones), name outside `^[a-z][a-z0-9-]*$`, duplicate name, `recall` missing, `retain` true with no `retain` tool, a
  placeholder outside the tool's allowed set (message quotes it and names the tool), an unbalanced brace, `results.format`
  not `json` or `text`. It walks argument values recursively, string leaves only.
- `Profile.Load`: after the existing deserialise and the Task 2 server checks, validate every provider and throw one
  `ChargehandException(ErrorCode.InvalidRequest, problems joined, "Fix memory in the profile; docs/guide/reference.md lists the fields.")`.
- Schema: `memory` is `oneOf` [array of provider objects (required `name`, `server`, `tools.recall.tool`,
  `tools.recall.arguments`; `namespace` optional; `results.text` a string or an array of strings), the old object marked
  `deprecated: true`]. Update `example.json`: an `mcp_servers.memory-gateway` entry and the Hindsight entry of the spec with
  `retain: false`, and an `mcp_servers.chronicle` entry (`"url": "http://localhost:<port>/sse"`, `"transport": "sse"`) with the
  Chronicle entry of the spec; the placeholders stay placeholders, no host or address.
- `Program.cs`: `Memory()` reads `profile.Memory?.ObjectForm` (Task 3's stack construction); providers are wired in Task 7.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "MemoryConfigTests|ConfigFileTests|ProfileTests"` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add src/Chargehand/Config src/Chargehand.Cli/Program.cs profiles tests/Chargehand.Tests
git commit -m "feat: make memory a list of providers with a declarative MCP tool mapping"
```

---

### Task 5: `McpMemoryProvider`: the mapping executor

**Files:**
- Create: `src/Chargehand.Mcp/McpMemoryProvider.cs`, `src/Chargehand.Mcp/ToolArguments.cs`, `src/Chargehand.Mcp/RecallResults.cs`, `src/Chargehand.Mcp/McpMemoryException.cs`
- Create: `tests/Chargehand.Tests/McpMemoryProviderTests.cs`, `tests/Chargehand.Tests/ToolArgumentsTests.cs`

**Interfaces:**
- Consumes: `MemoryProviderSettings`, `ToolCall`, `ResultMapping` (Task 4), `McpConnectionPool` and `FakeMcpServer` (Task 2), `IMemoryProvider`, `HindsightMemory` (for the parity test only).
- Produces: `McpMemoryProvider(MemoryProviderSettings settings, McpConnectionPool pool) : IMemoryProvider`;
  `ToolArguments.Expand(IReadOnlyDictionary<string, JsonElement> template, IReadOnlyDictionary<string, object?> values) : Dictionary<string, object?>`;
  `RecallResults.Read(CallToolResult result, ResultMapping? mapping) : IReadOnlyList<RecalledMemory>` (`text` entries that are field names or `{field}` templates; an entry is used only when every field it names is present and non-empty; the first that qualifies wins; an item with none is skipped);
  `McpMemoryException` (thrown for `isError`, a missing path, unparsable JSON; the message is scrubbed).

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Chargehand.Tests/ToolArgumentsTests.cs
using System.Text.Json;
using Chargehand.Mcp;

namespace Chargehand.Tests;

public class ToolArgumentsTests
{
    private static IReadOnlyDictionary<string, JsonElement> Template(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void A_whole_placeholder_keeps_the_values_type()
    {
        var args = ToolArguments.Expand(Template("""{"tags":"{tags}","n":"{count}","max_tokens":1024,"flag":true}"""),
            new Dictionary<string, object?> { ["tags"] = new[] { "chargehand", "x" }, ["count"] = 3 });

        Assert.Equal(new[] { "chargehand", "x" }, args["tags"]);
        Assert.Equal(3, args["n"]);
        Assert.Equal(1024, ((JsonElement)args["max_tokens"]!).GetInt32());
        Assert.True(((JsonElement)args["flag"]!).GetBoolean());
    }

    [Fact]
    public void A_placeholder_inside_text_is_replaced_as_text()
    {
        var args = ToolArguments.Expand(Template("""{"q":"repo:{namespace} {query}"}"""),
            new Dictionary<string, object?> { ["namespace"] = "chargehand", ["query"] = "deploys" });

        Assert.Equal("repo:chargehand deploys", args["q"]);
    }

    [Fact]
    public void A_null_value_omits_the_argument()
    {
        var args = ToolArguments.Expand(Template("""{"timestamp":"{timestamp}","content":"{text}"}"""),
            new Dictionary<string, object?> { ["timestamp"] = null, ["text"] = "fact" });

        Assert.False(args.ContainsKey("timestamp"));
        Assert.Equal("fact", args["content"]);
    }

    [Fact]
    public void Nested_objects_and_arrays_are_expanded()
    {
        var args = ToolArguments.Expand(Template("""{"filter":{"tags":["a","{tag}"]}}"""), new Dictionary<string, object?> { ["tag"] = "b" });

        Assert.Equal("""{"tags":["a","b"]}""", JsonSerializer.Serialize(args["filter"]));
    }
}
```

```csharp
// tests/Chargehand.Tests/McpMemoryProviderTests.cs
using System.Net;
using System.Text;
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Mcp;
using Chargehand.Memory;
using static Chargehand.Tests.MemoryConfigTests;

namespace Chargehand.Tests;

public class McpMemoryProviderTests
{
    private static readonly MemoryScope Scope = new("hindsight", "chargehand");

    private static MemoryProviderSettings Settings(string json) => JsonSerializer.Deserialize<MemoryProviderSettings>(json, Profile.Json)!;

    private static McpConnectionPool PoolFor(FakeMcpServer server, string name = "gw") =>
        new(new Dictionary<string, McpServerSettings> { [name] = new(Url: "https://mcp.example.internal/mcp") }, _ => "", async (_, _, _) => await server.TransportAsync());

    private const string Found = """{"results":[{"id":"f1","text":"Deploys go through GitOps.","fact_type":"world"}]}""";

    [Fact]
    public async Task Recall_calls_the_mapped_tool_with_templated_arguments_and_reads_the_results()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(Found)));
        await using var pool = PoolFor(server);

        var facts = await new McpMemoryProvider(Settings(Hindsight), pool).RecallAsync("how are deploys done", Scope, CancellationToken.None);

        Assert.Equal([new RecalledMemory("f1", "Deploys go through GitOps.")], facts);
        var (tool, args) = Assert.Single(server.Calls);
        Assert.Equal("recall", tool);
        Assert.Equal("how are deploys done", args["query"].GetString());
        Assert.Equal("chargehand", args["bank_id"].GetString());
        Assert.Equal("low", args["budget"].GetString());
        Assert.Equal(1024, args["max_tokens"].GetInt32());
    }

    [Fact]
    public async Task The_results_mapping_names_the_array_and_the_fields()
    {
        var entry = """{"name":"notes","server":"gw","namespace":"n","tools":{"recall":{"tool":"search_notes","arguments":{"q":"{query}","limit":10},"results":{"path":"notes","id":"key","text":"body"}}}}""";
        await using var server = new FakeMcpServer(new FakeTool("search_notes", _ => FakeMcpServer.Text("""{"notes":[{"key":"k1","body":"The API uses MediatR."}]}""")));
        await using var pool = PoolFor(server);

        var facts = await new McpMemoryProvider(Settings(entry), pool).RecallAsync("api", Scope, CancellationToken.None);

        Assert.Equal([new RecalledMemory("k1", "The API uses MediatR.")], facts);
    }

    [Fact]
    public async Task A_text_format_takes_the_whole_block_as_one_fact_with_a_hash_id()
    {
        var entry = """{"name":"wiki","server":"gw","namespace":"n","tools":{"recall":{"tool":"ask","arguments":{"q":"{query}"},"results":{"format":"text"}}}}""";
        await using var server = new FakeMcpServer(new FakeTool("ask", _ => FakeMcpServer.Text("Deploys go through GitOps.")));
        await using var pool = PoolFor(server);

        var fact = Assert.Single(await new McpMemoryProvider(Settings(entry), pool).RecallAsync("deploys", Scope, CancellationToken.None));

        Assert.Equal("Deploys go through GitOps.", fact.Text);
        Assert.Equal(12, fact.Id.Length);
    }

    [Fact]
    public async Task A_missing_id_becomes_a_hash_of_the_text()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text("""{"results":[{"text":"no id here"}]}""")));
        await using var pool = PoolFor(server);

        var fact = Assert.Single(await new McpMemoryProvider(Settings(Hindsight), pool).RecallAsync("q", Scope, CancellationToken.None));

        Assert.Equal(12, fact.Id.Length);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("not json")]
    [InlineData("""{"other":[]}""")]
    public async Task A_tool_error_or_a_result_the_mapping_cannot_read_is_a_failure(string reply)
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => reply == "error" ? FakeMcpServer.Error("boom") : FakeMcpServer.Text(reply)));
        await using var pool = PoolFor(server);

        var e = await Assert.ThrowsAsync<McpMemoryException>(() => new McpMemoryProvider(Settings(Hindsight), pool).RecallAsync("q", Scope, CancellationToken.None));

        Assert.DoesNotContain("s3cret", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retain_omits_null_arguments_and_keeps_tags_an_array()
    {
        await using var server = new FakeMcpServer(new FakeTool("retain", _ => FakeMcpServer.Text("queued")));
        await using var pool = PoolFor(server);

        await new McpMemoryProvider(Settings(Hindsight), pool).RetainAsync(new MemoryItem("fact", "ctx", null, "run-1", ["chargehand", "x"]), Scope, CancellationToken.None);

        var args = Assert.Single(server.Calls).Arguments;
        Assert.Equal("fact", args["content"].GetString());
        Assert.Equal("run-1", args["document_id"].GetString());
        Assert.False(args.ContainsKey("timestamp"));
        Assert.Equal(["chargehand", "x"], args["tags"].EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Invalidate_sends_the_id_and_the_reason()
    {
        await using var server = new FakeMcpServer(new FakeTool("invalidate_memory", _ => FakeMcpServer.Text("ok")));
        await using var pool = PoolFor(server);

        await new McpMemoryProvider(Settings(Hindsight), pool).InvalidateAsync("f1", "wrong", Scope, CancellationToken.None);

        var args = Assert.Single(server.Calls).Arguments;
        Assert.Equal(("f1", "wrong", "chargehand"), (args["memory_id"].GetString(), args["reason"].GetString(), args["bank_id"].GetString()));
    }

    /// <summary>The body of Chronicle's /recall (chronicle/api.py, results built at lines 168-176): one JSON text block through MCP.</summary>
    private const string ChronicleReply = """
        {"intent":"open","routed_because":null,"window_from_query":null,"results":[
          {"segment_id":"seg-1","score":0.031,"date":"2024-05-03T10:12:00","thread":"chat-1","text":"raw conversation one","evidence":["ev-1","ev-2"],"summary":"Agreed to repaint the flat in June."},
          {"segment_id":"seg-2","score":0.020,"date":"2023-11-20T18:40:00","thread":"chat-2","text":"raw conversation two","evidence":["ev-3"],"summary":null},
          {"segment_id":"seg-3","score":0.010,"date":"2023-01-02T09:00:00","thread":"chat-3","text":"","evidence":[],"summary":null}]}
        """;

    [Fact]
    public async Task A_chronicle_shaped_recall_reads_the_summary_falls_back_to_the_text_and_skips_an_empty_segment()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(ChronicleReply)));
        await using var pool = PoolFor(server, "chronicle");

        var facts = await new McpMemoryProvider(Settings(Chronicle), pool).RecallAsync("what did we decide about the flat", new MemoryScope("chronicle", "chronicle"), CancellationToken.None);

        Assert.Equal([new RecalledMemory("seg-1", "2024-05-03T10:12:00: Agreed to repaint the flat in June."),
                      new RecalledMemory("seg-2", "2023-11-20T18:40:00: raw conversation two")], facts);
        var (tool, args) = Assert.Single(server.Calls);
        Assert.Equal("recall", tool);
        Assert.Equal(["limit", "query"], args.Keys.Order());   // date_from, date_to and source are left to Chronicle's own routing
        Assert.Equal(("what did we decide about the flat", 10), (args["query"].GetString(), args["limit"].GetInt32()));
    }

    [Fact]
    public async Task The_limit_is_the_entrys_fact_cap()
    {
        var entry = Chronicle.Replace("\"name\":\"chronicle\"", "\"name\":\"chronicle\",\"max_facts\":5", StringComparison.Ordinal);
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(ChronicleReply)));
        await using var pool = PoolFor(server, "chronicle");

        await new McpMemoryProvider(Settings(entry), pool).RecallAsync("q", new MemoryScope("chronicle", "chronicle"), CancellationToken.None);

        Assert.Equal(5, Assert.Single(server.Calls).Arguments["limit"].GetInt32());
    }

    [Fact]
    public async Task A_recall_only_provider_refuses_to_retain_and_calls_nothing()
    {
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(ChronicleReply)));
        await using var pool = PoolFor(server, "chronicle");
        var provider = new McpMemoryProvider(Settings(Chronicle), pool);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.RetainAsync(new MemoryItem("fact"), new MemoryScope("chronicle", "chronicle"), CancellationToken.None));

        Assert.Empty(server.Calls);
    }

    private sealed class Recorder(string recallReply) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, JsonElement Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.Method, request.RequestUri!.AbsolutePath, JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone()));
            var body = request.RequestUri.AbsolutePath.EndsWith("/recall", StringComparison.Ordinal) ? recallReply : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>The bar for deleting HindsightMemory (spec, decision 14): the mapping sends what the HTTP client sends.</summary>
    [Fact]
    public async Task Hindsight_mapping_sends_what_HindsightMemory_sends()
    {
        var item = new MemoryItem("fact", "ctx", new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), "run-1", ["chargehand", "x"]);
        var http = new Recorder(Found);
        var hindsight = new HindsightMemory(new HttpClient(http) { BaseAddress = new Uri("http://memory.example.internal:8888/") }, apiKey: null, recallMaxTokens: 1024);
        var overHttp = await hindsight.RecallAsync("how are deploys done", Scope, CancellationToken.None);
        await hindsight.RetainAsync(item, Scope, CancellationToken.None);
        await hindsight.InvalidateAsync("f1", "wrong", Scope, CancellationToken.None);

        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(Found)), new FakeTool("retain", _ => FakeMcpServer.Text("queued")),
            new FakeTool("invalidate_memory", _ => FakeMcpServer.Text("ok")));
        await using var pool = PoolFor(server);
        var mcp = new McpMemoryProvider(Settings(Hindsight), pool);
        var overMcp = await mcp.RecallAsync("how are deploys done", Scope, CancellationToken.None);
        await mcp.RetainAsync(item, Scope, CancellationToken.None);
        await mcp.InvalidateAsync("f1", "wrong", Scope, CancellationToken.None);

        Assert.Equal(overHttp, overMcp);
        var (recall, retain, invalidate) = (server.Calls[0].Arguments, server.Calls[1].Arguments, server.Calls[2].Arguments);
        Assert.Contains("/banks/chargehand/", http.Seen[0].Path, StringComparison.Ordinal);
        Assert.Equal("chargehand", recall["bank_id"].GetString());
        foreach (var field in new[] { "query", "budget" })
            Assert.Equal(http.Seen[0].Body.GetProperty(field).GetString(), recall[field].GetString());
        Assert.Equal(http.Seen[0].Body.GetProperty("max_tokens").GetInt32(), recall["max_tokens"].GetInt32());

        var sent = http.Seen[1].Body.GetProperty("items")[0];
        Assert.True(http.Seen[1].Body.GetProperty("async").GetBoolean()); // the MCP tool is the asynchronous one, not sync_retain
        foreach (var field in new[] { "content", "context", "document_id", "timestamp" })
            Assert.Equal(sent.GetProperty(field).GetString(), retain[field].GetString());
        Assert.Equal(sent.GetProperty("tags").EnumerateArray().Select(t => t.GetString()), retain["tags"].EnumerateArray().Select(t => t.GetString()));

        Assert.EndsWith("/memories/f1", http.Seen[2].Path, StringComparison.Ordinal);
        Assert.Equal(http.Seen[2].Body.GetProperty("reason").GetString(), invalidate["reason"].GetString());
        Assert.Equal("f1", invalidate["memory_id"].GetString());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "ToolArgumentsTests|McpMemoryProviderTests"`
Expected: FAIL to compile (`McpMemoryProvider`, `ToolArguments` do not exist).

- [ ] **Step 3: Implement**

- `ToolArguments.Expand`: walk each `JsonElement`; a string equal to `{name}` becomes `values[name]` (a null value drops the
  key; arrays and numbers keep their type; a `DateTimeOffset` serialises as `HindsightMemory` does, through
  `System.Text.Json`); other strings replace each `{name}` with `values[name]?.ToString()`; numbers, booleans and nulls are
  copied; objects and arrays recurse.
- `McpMemoryProvider`: `RecallAsync` expands `query` and `namespace`, calls `client.CallToolAsync(tool, args, cancellationToken: ct)`,
  throws `McpMemoryException` when `IsError`, then `RecallResults.Read`. `RetainAsync` supplies `text`, `context`,
  `document_id`, `timestamp`, `tags`, `namespace`. `InvalidateAsync` supplies `id`, `reason`, `namespace`. The recall
  values also carry `max_facts`: the entry's `MaxFacts`, else 10, as an integer. `namespace` is `scope.Namespace`. A provider with no
  `retain` tool throws `InvalidOperationException` (config validation already refused `retain: true` without it).
- `RecallResults.Read`: `StructuredContent` if present, else the first text block parsed as JSON; `Format: "text"` returns one
  fact; `Path` walks dotted property names; non-array or missing path throws `McpMemoryException`. For each element:
  `id` is the named property's text (a missing one becomes the hash of the fact text); the fact text is the first `text`
  entry that qualifies, where a bare field name qualifies when the property is a non-empty string or a number, and a
  template qualifies when every `{field}` it names is present and non-empty (strings as is, numbers with the invariant
  culture, arrays and objects as compact JSON); an element with no qualifying entry is skipped.
- Every message passes through `ChargehandException.Scrub`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "ToolArgumentsTests|McpMemoryProviderTests|HindsightMemoryTests"` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add src/Chargehand.Mcp tests/Chargehand.Tests
git commit -m "feat: add the MCP memory provider driven by a declarative tool mapping"
```

---

### Task 6: Retain only claims whose citations resolved

**Files:**
- Create: `src/Chargehand/Memory/RetainableClaims.cs` (`RetainableClaim`, `RetainSelection`, `RetainableClaims`, `RetainItems`, `RepositoryLabel`)
- Modify: `src/Chargehand/Orchestrator.cs` (`Checkout` returns the label; the retain block uses the selection)
- Create: `tests/Chargehand.Tests/RetainableClaimsTests.cs`
- Modify: `tests/Chargehand.Tests/MemoryRunTests.cs`
- Modify: `tests/Chargehand.Tests/MemoryFailOpenTests.cs` (its retain cases run on a checkout)

**Interfaces:**
- Consumes: `ResultContract`, `Claim`, `Evidence`, `EvidenceKind`, `ChargehandException.Scrub`, `MemoryStack.RetainAsync` (Task 3), `MemoryItem`.
- Produces:
  - `RetainableClaims.From(ResultContract result) : RetainSelection`; `RetainSelection(IReadOnlyList<RetainableClaim> Claims, IReadOnlyList<string> Skipped)`; `RetainableClaim(string Text, IReadOnlyList<string> Locators, double Confidence)`.
  - `RetainItems.Build(RetainSelection selection, string repositoryLabel, string commit, string runId, DateTimeOffset finished) : MemoryItem` (context `chargehand run result`, document id the run id; tags are set per source by the stack).
  - `RepositoryLabel.From(string? originUrl, string directoryName) : string`.
  - Item text: first line `Repository: <label>, commit <12 hex> (citations checked at this commit)`, then `- <text> [<locators joined "; ">] (confidence 0.90)` per claim.
  - Rule (spec decision 8): status `Completed`; at least one `file` or `commit` evidence entry; text and locators unchanged by `ChargehandException.Scrub`; request text and summary never used.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Chargehand.Tests/RetainableClaimsTests.cs
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

        var item = RetainItems.Build(selection, "github.com/example/proj", "0123456789abcdef0123456789abcdef01234567", "run-1", new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("""
            Repository: github.com/example/proj, commit 0123456789ab (citations checked at this commit)
            - The README says hello. [README.md:1] (confidence 0.90)
            """.ReplaceLineEndings("\n"), item.Text);
        Assert.Equal(("chargehand run result", "run-1"), (item.Context, item.DocumentId));
        Assert.DoesNotContain("SUMMARY-MARKER", item.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/example/proj.git", "proj-dir", "github.com/example/proj")]
    [InlineData("https://user:tok@github.com/example/proj", "proj-dir", "github.com/example/proj")]
    [InlineData("git@github.com:example/proj.git", "proj-dir", "github.com/example/proj")]
    [InlineData("ssh://git@host.example.internal:2222/team/proj.git", "proj-dir", "host.example.internal/team/proj")]
    [InlineData(null, "proj-dir", "proj-dir")]
    [InlineData("", "proj-dir", "proj-dir")]
    public void The_repository_label_drops_scheme_user_information_and_dot_git(string? origin, string directory, string expected) =>
        Assert.Equal(expected, RepositoryLabel.From(origin, directory));
}
```

Additions to `MemoryRunTests` (same file as Task 3):

```csharp
    private const string ThreeClaims = """
        ```json
        {"status":"completed","summary":"SUMMARY-MARKER","claims":[
           {"text":"The README says hello.","evidence":["e1"],"confidence":0.9},
           {"text":"The README has ninety-nine lines.","evidence":["e2"],"confidence":0.8},
           {"text":"The caller says v1 shipped.","evidence":["e3"],"confidence":0.7}],
         "evidence":[{"id":"e1","kind":"file","locator":"README.md:1"},{"id":"e2","kind":"file","locator":"README.md:99"},{"id":"e3","kind":"input","locator":"rel-v1"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    [Fact]
    public async Task A_run_retains_only_qualifying_claims()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var provider = new Fake();
        var stack = new MemoryStack([Source("notes", provider, retain: true)]);
        var request = Runs.CheapRequest(repo) with { Inputs = [new CallerInput("rel-v1", "signal", "Released v1.")] };

        var result = await Make(root.Path, new ScriptedRuntime(ThreeClaims), stack, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"))).RunAsync(request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Contains(result.OpenQuestions, q => q.StartsWith("Unverified: The README has ninety-nine lines.", StringComparison.Ordinal)); // README.md:99 does not resolve
        var item = Assert.Single(provider.Retained);
        Assert.Contains("- The README says hello. [README.md:1]", item.Text, StringComparison.Ordinal);
        Assert.Contains($"commit {repo.Commit[..12]}", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ninety-nine", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("caller says", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("SUMMARY-MARKER", item.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("What does the README say?", item.Text, StringComparison.Ordinal);
        Assert.Equal(result.TaskId, item.DocumentId);
        Assert.Equal(["chargehand"], item.Tags);
    }

    [Fact]
    public async Task Nothing_is_retained_when_no_claim_qualifies_or_there_is_no_commit()
    {
        using var root = new TempDir();
        var provider = new Fake();
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var stack = new MemoryStack([Source("notes", provider, retain: true)]);

        var result = await Make(root.Path, new ScriptedRuntime(Runs.DraftReply), stack, log).RunAsync(Runs.DraftRequest(), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Empty(provider.Retained);
        Assert.Equal("no commit", (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Memory.Single().RetainSkipped);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "RetainableClaimsTests|MemoryRunTests"`
Expected: FAIL to compile (`RetainableClaims`, `RepositoryLabel` do not exist).

- [ ] **Step 3: Implement**

- `RetainableClaims.From`: not `Completed` → empty. Per claim: the evidence entries it cites, looked up in `result.Evidence`
  by id; qualifies with at least one `File` or `Commit`; locators are the `File` locators as cited (deduplicated, in
  citation order) and `commit <first 7 of locator>` for commits; text and each locator must equal their
  `ChargehandException.Scrub` output, else `Skipped` gets `redacted: <first 60 characters>`; a claim with no such entry gets
  `not repository-anchored: <first 60 characters>`.
- `RetainItems.Build`: the two-line format in Interfaces, `confidence` with `CultureInfo.InvariantCulture` and format `0.00`.
- `RepositoryLabel.From`: strip `scheme://`, `user[:password]@`, a trailing `.git`; turn `host:path` (scp form) into
  `host/path`; drop a port; empty or null gives the directory name.
- `Orchestrator.Checkout` returns a third value, the label, from `git config --get remote.origin.url` in the source
  checkout (`Orchestrator.cs:283`) and the source's directory name. In `Execute`, the retain block becomes: if
  `commit.Length == 0` record `RetainSkipped = "no commit"`; else build the selection and, when it has claims, call
  `memory.RetainAsync(RetainItems.Build(...), ct)`, else record `"no claim qualified"`. Request text and summary are no
  longer written.
- `MemoryStack.RetainAsync` sets `Tags` on the item per source (`RetainTags` or `["chargehand"]`).
- `MemoryFailOpenTests.A_failed_retain_leaves_the_completed_result_unchanged` (and the retain-cancellation case) used a draft
  request, which has no commit and so retains nothing from this task on. Both move to `Runs.GitRepo(root)` with
  `Runs.CheapRequest(repo)` and a `ScriptedRuntime(Runs.WorkerReply)`, whose claim cites `README.md:1`, so a retain call still
  happens; the summary assertion becomes `"The README greets."`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "RetainableClaimsTests|MemoryRunTests|MemoryStackTests"` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add src/Chargehand tests/Chargehand.Tests
git commit -m "feat: retain only claims whose citations resolved, with locators, repository and commit"
```

---

### Task 7: Wire the stack into the CLI and server; `chargehand extensions check`

**Files:**
- Create: `src/Chargehand.Mcp/MemoryStacks.cs`, `src/Chargehand.Mcp/ExtensionsCheck.cs`
- Modify: `src/Chargehand.Cli/Program.cs` (build the stack from the list through the pool; `extensions check [--probe <query>]`; usage text; dispose the pool on exit)
- Create: `tests/Chargehand.Tests/MemoryStacksTests.cs`, `tests/Chargehand.Tests/ExtensionsCheckTests.cs`

**Interfaces:**
- Consumes: `Profile.Memory` and `MemoryProviderSettings` (Task 4), `McpMemoryProvider` (Task 5), `McpConnectionPool` (Tasks 2 and 8, which created the pool in `Program.cs`), `MemoryStack` (Task 3), `ServiceUse` and `Preset.NodeKinds[..].Services` (Task 8), `FakeMcpServer` (Task 2).
- Produces:
  - `MemoryStacks.From(Profile profile, McpConnectionPool pool, Func<MemorySettings, IMemoryProvider> objectForm) : MemoryStack?`: null with no memory; one `McpMemoryProvider` source per list entry (limits and retain flags from the entry); the object form through the supplied factory (removed in Task 11).
  - `ExtensionsCheck.RunAsync(Profile profile, IReadOnlyList<Preset> presets, McpConnectionPool pool, string? probeQuery, CancellationToken ct) : Task<ExtensionsCheckResult>`; `ExtensionsCheckResult(IReadOnlyList<string> Lines, bool Ok)`. Exit code of the command: 0 when `Ok`, 1 otherwise.
  - Line formats: `server <name>: connected, <n> tools` or `server <name>: <reason>`; `memory <name>: <tool> -> <tool> ok` or `-> <problem>`; `memory <name>: probe returned <n> fact(s)`; `preset <name>: service <server>: <tool> ok` or `<problem>`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Chargehand.Tests/MemoryStacksTests.cs
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Mcp;
using Chargehand.Memory;
using static Chargehand.Tests.MemoryConfigTests;

namespace Chargehand.Tests;

public class MemoryStacksTests
{
    private static Profile ProfileWith(string memory, string servers = """{"gw":{"url":"https://mcp.example.internal/mcp"}}""")
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("p.json", $$"""{"schema":"profile/v1","mcp_servers":{{servers}},"memory":{{memory}}}"""));
    }

    private static readonly Func<MemorySettings, IMemoryProvider> NoObjectForm = _ => throw new InvalidOperationException("the object form is not expected here");

    [Fact]
    public async Task The_list_becomes_one_source_per_entry_in_order_with_the_entrys_limits()
    {
        var notes = """{"name":"notes","server":"gw","namespace":"n","max_facts":3,"max_chars":900,"max_fact_chars":200,"timeout_seconds":4,"retain_tags":["team"],"tools":{"recall":{"tool":"search_notes","arguments":{"q":"{query}"}}}}""";
        var profile = ProfileWith($"[{Hindsight},{notes}]");
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", (_, _, _) => throw new InvalidOperationException("not connected in this test"));

        var stack = MemoryStacks.From(profile, pool, NoObjectForm)!;

        Assert.Equal(["hindsight", "notes"], stack.Sources.Select(s => s.Name));
        Assert.Equal([true, false], stack.Sources.Select(s => s.Retain));
        Assert.Equal(new MemoryLimits(3, 900, TimeSpan.FromSeconds(4), 200), stack.Sources[1].Limits);
        Assert.Equal(["team"], stack.Sources[1].RetainTags);
        Assert.Equal(new MemoryScope("notes", "n"), stack.Sources[1].Scope);
    }

    [Fact]
    public async Task A_recall_only_entry_without_a_namespace_scopes_to_its_name_and_never_retains()
    {
        var profile = ProfileWith($"[{Chronicle}]", """{"chronicle":{"url":"http://localhost:8031/sse","transport":"sse"}}""");
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", (_, _, _) => throw new InvalidOperationException("not connected in this test"));

        var source = Assert.Single(MemoryStacks.From(profile, pool, NoObjectForm)!.Sources);

        Assert.Equal(("chronicle", false), (source.Name, source.Retain));
        Assert.Equal(new MemoryScope("chronicle", "chronicle"), source.Scope);
    }

    [Fact]
    public async Task No_memory_gives_no_stack()
    {
        await using var pool = new McpConnectionPool(new Dictionary<string, McpServerSettings>(), _ => "");

        Assert.Null(MemoryStacks.From(new Profile("profile/v1"), pool, NoObjectForm));
    }
}
```

```csharp
// tests/Chargehand.Tests/ExtensionsCheckTests.cs
using System.Text.Json;
using Chargehand.Config;
using Chargehand.Mcp;
using static Chargehand.Tests.MemoryConfigTests;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: setup mistakes show up before a run does.</summary>
public class ExtensionsCheckTests
{
    private const string Recall = """{"type":"object","properties":{"query":{"type":"string"},"bank_id":{"type":"string"},"budget":{"type":"string"},"max_tokens":{"type":"number"}}}""";
    private const string Retain = """{"type":"object","properties":{"content":{"type":"string"},"context":{"type":"string"},"document_id":{"type":"string"},"timestamp":{"type":"string"},"tags":{"type":"array"},"bank_id":{"type":"string"}}}""";
    private const string Invalidate = """{"type":"object","properties":{"memory_id":{"type":"string"},"reason":{"type":"string"},"bank_id":{"type":"string"}}}""";

    private static Profile ProfileWith(string memory, string servers = """{"gw":{"url":"https://mcp.example.internal/mcp"}}""")
    {
        using var dir = new TempDir();
        return Profile.Load(dir.Write("p.json", $$"""{"schema":"profile/v1","mcp_servers":{{servers}},"memory":{{memory}}}"""));
    }

    private static FakeMcpServer HindsightServer(bool withRetain = true) => new(
        new FakeTool("recall", _ => FakeMcpServer.Text("""{"results":[{"id":"f1","text":"Deploys go through GitOps."}]}"""), Schema: Recall),
        withRetain ? new FakeTool("retain", _ => FakeMcpServer.Text("queued"), Schema: Retain) : new FakeTool("other", _ => FakeMcpServer.Text("x")),
        new FakeTool("invalidate_memory", _ => FakeMcpServer.Text("ok"), Schema: Invalidate));

    private static McpConnectionPool PoolFor(Profile profile, FakeMcpServer server) =>
        new(profile.McpServers!, _ => "", async (_, _, _) => await server.TransportAsync());

    [Fact]
    public async Task A_valid_setup_is_ok_and_lists_each_server_and_mapping()
    {
        var profile = ProfileWith($"[{Hindsight}]");
        await using var server = HindsightServer();
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("server gw: connected, 3 tools", result.Lines);
        Assert.Contains("memory hindsight: recall -> recall ok", result.Lines);
        Assert.Contains("memory hindsight: retain -> retain ok", result.Lines);
    }

    [Fact]
    public async Task A_mapped_tool_the_server_does_not_list_is_a_problem()
    {
        var profile = ProfileWith($"[{Hindsight}]");
        await using var server = HindsightServer(withRetain: false);
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("memory hindsight: retain -> tool 'retain' is not listed by server 'gw'", result.Lines);
    }

    [Fact]
    public async Task An_argument_the_tools_schema_lacks_is_a_problem()
    {
        var entry = """{"name":"hs","server":"gw","namespace":"n","tools":{"recall":{"tool":"recall","arguments":{"q":"{query}"}}}}""";
        var profile = ProfileWith($"[{entry}]");
        await using var server = HindsightServer();
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("memory hs: recall -> argument 'q' is not a property of recall (", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreachable_server_is_a_problem_and_the_others_are_still_checked()
    {
        var servers = """{"down":{"url":"https://a.example.internal/mcp"},"gw":{"url":"https://mcp.example.internal/mcp"}}""";
        var profile = ProfileWith($"[{Hindsight}]", servers);
        await using var server = HindsightServer();
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", async (name, _, _) =>
            name == "down" ? throw new IOException("connection refused") : await server.TransportAsync());

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains(result.Lines, l => l.StartsWith("server down:", StringComparison.Ordinal) && l.Contains("connection refused", StringComparison.Ordinal));
        Assert.Contains("server gw: connected, 3 tools", result.Lines);
    }

    [Fact]
    public async Task A_probe_runs_recall_and_prints_the_count()
    {
        var profile = ProfileWith($"[{Hindsight}]");
        await using var server = HindsightServer();
        await using var pool = PoolFor(profile, server);

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, "deploys", CancellationToken.None);

        Assert.Contains("memory hindsight: probe returned 1 fact(s)", result.Lines);
    }

    [Fact]
    public async Task A_recall_only_provider_is_checked_for_recall_alone()
    {
        var servers = """{"chronicle":{"url":"http://localhost:8031/sse","transport":"sse"}}""";
        var profile = ProfileWith($"[{Chronicle}]", servers);
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text("""{"results":[]}"""),
            Schema: """{"type":"object","properties":{"query":{"type":"string"},"date_from":{},"date_to":{},"source":{},"limit":{"type":"integer"}}}"""));
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", async (_, _, _) => await server.TransportAsync());

        var result = await ExtensionsCheck.RunAsync(profile, [], pool, null, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Contains("memory chronicle: recall -> recall ok", result.Lines);
        Assert.DoesNotContain(result.Lines, l => l.Contains("retain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_presets_service_is_checked_against_the_servers_tools()
    {
        var servers = """{"team-docs":{"url":"https://mcp.example.internal/docs"}}""";
        var profile = ProfileWith("[]", servers);
        await using var docs = new FakeMcpServer(new FakeTool("search_docs", _ => FakeMcpServer.Text("x")));
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", async (_, _, _) => await docs.TransportAsync());
        var preset = PresetRoot.Load("docs", "    services:\n      - server: team-docs\n        tools: [search_docs, missing_tool]\n");

        var result = await ExtensionsCheck.RunAsync(profile, [preset], pool, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("preset docs: service team-docs: search_docs ok", result.Lines);
        Assert.Contains("preset docs: service team-docs: tool 'missing_tool' is not listed", result.Lines);
    }
}
```

`PresetRoot.Load(name, servicesYaml)` is the static helper Task 8 adds (`PresetRoot` builds a temp root; `Load` returns the loaded `Preset`).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "MemoryStacksTests|ExtensionsCheckTests"`
Expected: FAIL to compile (`MemoryStacks`, `ExtensionsCheck` do not exist).

- [ ] **Step 3: Implement**

- `MemoryStacks.From`: list entries become `new MemorySource(name, new McpMemoryProvider(entry, pool), new MemoryScope(name, entry.Namespace),
  new MemoryLimits(entry.MaxFacts ?? 10, entry.MaxChars ?? 4000, TimeSpan.FromSeconds(entry.TimeoutSeconds ?? 10)), entry.Retain, entry.RetainTags)`.
- `ExtensionsCheck.RunAsync`: for each `mcp_servers` entry, `pool.GetAsync` and `ListToolsAsync`, catching every exception into
  the `server <name>: <scrubbed reason>` line (`Ok = false`); for each memory entry and each mapped tool, check the tool is
  listed and every top-level argument name is in `inputSchema.properties` when the schema declares any (the line lists the
  schema's property names); with `probeQuery`, call the provider's `RecallAsync` and print the count; for each preset node
  kind's `services`, check each requested name or glob against the server's list.
- `Program.cs`: `extensions check` loads every `presets/*.yaml` under the install root, calls `ExtensionsCheck.RunAsync`, prints
  the lines, exits 0 or 1; `Memory()` becomes `MemoryStacks.From(profile, mcpPool, m => new HindsightMemory(…))`; the pool is
  disposed on exit (`serve`, `mcp`, `run`). Add the command to the usage text.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "MemoryStacksTests|ExtensionsCheckTests|MemoryRunTests"` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add src/Chargehand.Mcp src/Chargehand.Cli/Program.cs tests/Chargehand.Tests
git commit -m "feat: build the memory stack from the profile and add the extensions check command"
```

---

### Task 8: Services: preset `services`, resolver, grants on `NodeSpec`

**Files:**
- Modify: `src/Chargehand/Config/Preset.cs` (`ServiceUse`, `NodeKind.Services`)
- Modify: `schemas/preset/v1/preset.schema.json` (optional `services` on `node_kind`; additive)
- Modify: `src/Chargehand/Runtime/IWorkerRuntime.cs` (`NodeSpec.Services`, `ServiceGrant`, `ServiceTransport`, `HttpServiceTransport`, `StdioServiceTransport`)
- Create: `src/Chargehand/Runtime/IServiceResolver.cs` (`IServiceResolver`, `ResolvedServices`)
- Modify: `src/Chargehand/Prompts/PromptChains.cs` (`ToolsSha256` takes the grants)
- Modify: `src/Chargehand/Orchestrator.cs` (resolve before the node's spec; report; tools hash)
- Create: `src/Chargehand.Mcp/ServiceResolver.cs`
- Modify: `src/Chargehand.Mcp/McpConnectionPool.cs` (`Transport(server)`)
- Modify: `src/Chargehand.Cli/Program.cs` (create the pool and the resolver; pass the resolver to `Orchestrator`)
- Create: `tests/Chargehand.Tests/PresetRoot.cs`, `PresetServicesTests.cs`, `ServiceResolverTests.cs`, `ServiceGrantTests.cs`, `ServiceRunTests.cs`
- Modify: `tests/Chargehand.Tests/ConfigFileTests.cs`

**Interfaces:**
- Consumes: `McpConnectionPool` and `SecretTemplate` (Task 2), `ServiceReport`/`ExtensionsReport` (Task 3), `FakeMcpServer` (Task 2).
- Produces:
  - `ServiceUse(string Server, IReadOnlyList<string> Tools)`; `NodeKind` gains a trailing `IReadOnlyList<ServiceUse>? Services = null`.
  - `ServiceGrant(string Server, ServiceTransport Transport, IReadOnlyList<string> Tools, string Sha256)`; `HttpServiceTransport(Uri Url, IReadOnlyDictionary<string,string> Headers)`; `StdioServiceTransport(IReadOnlyList<string> Command, IReadOnlyDictionary<string,string> Env)`; `ToString` on both hides header and environment values.
  - `NodeSpec(..., IReadOnlyList<ServiceGrant>? Services = null)`.
  - `IServiceResolver.ResolveAsync(IReadOnlyList<ServiceUse> uses, CancellationToken ct) : Task<ResolvedServices>`; `ResolvedServices(IReadOnlyList<ServiceGrant> Grants, IReadOnlyList<ServiceReport> Report)`. Issue strings: `unknown_server: <name>`, `secret_unresolved: <item>`, `unreachable: <reason>`, `tool_missing: <name or glob>`.
  - `Orchestrator(..., MemoryStack? memory = null, IServiceResolver? services = null)`.
  - `PromptChains.ToolsSha256(version, agent, rules, IReadOnlyList<ServiceGrant>? grants = null)`: identical output to today's when `grants` is null or empty.
  - Test helper `PresetRoot(string presetName, string servicesYaml)` (a temp root: `prompts/` copied, `presets/<name>.yaml` = `cheap.yaml` renamed with the services block inserted before `budget:`, `prompts/preset/<name>.md` copied from `cheap.md`) with `Path`, and static `Load(name, servicesYaml) : Preset`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Chargehand.Tests/PresetRoot.cs
using Chargehand.Config;

namespace Chargehand.Tests;

/// <summary>A temporary install root: the repository's prompts, and one preset derived from cheap with a services block.</summary>
internal sealed class PresetRoot : IDisposable
{
    private readonly TempDir _dir = new();

    public PresetRoot(string presetName, string servicesYaml)
    {
        Copy(Repo.Path("prompts"), System.IO.Path.Combine(_dir.Path, "prompts"));
        Directory.CreateDirectory(System.IO.Path.Combine(_dir.Path, "presets"));
        var yaml = File.ReadAllText(Repo.Path("presets", "cheap.yaml"))
            .Replace("name: cheap", $"name: {presetName}", StringComparison.Ordinal)
            .Replace("    budget:", servicesYaml + "    budget:", StringComparison.Ordinal);
        File.WriteAllText(System.IO.Path.Combine(_dir.Path, "presets", presetName + ".yaml"), yaml);
        File.Copy(Repo.Path("prompts", "preset", "cheap.md"), System.IO.Path.Combine(_dir.Path, "prompts", "preset", presetName + ".md"));
    }

    public string Path => _dir.Path;

    public static Preset Load(string presetName, string servicesYaml)
    {
        using var root = new PresetRoot(presetName, servicesYaml);
        return Preset.Load(System.IO.Path.Combine(root.Path, "presets"), presetName);
    }

    public void Dispose() => _dir.Dispose();

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, System.IO.Path.Combine(to, System.IO.Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
            Copy(dir, System.IO.Path.Combine(to, System.IO.Path.GetFileName(dir)));
    }
}
```

```csharp
// tests/Chargehand.Tests/PresetServicesTests.cs
using Chargehand.Config;

namespace Chargehand.Tests;

public class PresetServicesTests
{
    private const string Docs = "    services:\n      - server: team-docs\n        tools: [search_docs, read_doc]\n";

    [Fact]
    public void A_node_kind_may_list_services_by_server_and_tool()
    {
        var services = PresetRoot.Load("docs", Docs).NodeKinds["worker"].Services!;

        Assert.Equal([new ServiceUse("team-docs", ["search_docs", "read_doc"])], services);
    }

    [Theory]
    [InlineData("    services:\n      - server: team-docs\n        tools: ['*']\n")]     // no whole-server grant
    [InlineData("    services:\n      - server: team-docs\n        tools: []\n")]        // at least one tool
    [InlineData("    services:\n      - tools: [search_docs]\n")]                        // a server is required
    [InlineData("    services:\n      - server: team-docs\n        tools: [a]\n        writes: true\n")] // no other keys
    public void An_invalid_services_block_fails_the_schema(string services) =>
        Assert.Throws<InvalidDataException>(() => PresetRoot.Load("docs", services));

    [Fact]
    public void A_preset_without_services_has_none() => Assert.Null(Preset.Load(Repo.Path("presets"), "cheap").NodeKinds["worker"].Services);
}
```

Add to `ConfigFileTests`:

```csharp
    /// <summary>Spec decision 2: naked by default. Services belong to a user's own preset; the guide shows one.</summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void Shipped_presets_list_no_services(string file)
    {
        var preset = Chargehand.Config.Preset.Load(Repo.Path("presets"), file[..^5]);
        Assert.All(preset.NodeKinds.Values, k => Assert.True(k.Services is null or { Count: 0 }));
    }
```

```csharp
// tests/Chargehand.Tests/ServiceGrantTests.cs
using Chargehand.Runtime;

namespace Chargehand.Tests;

public class ServiceGrantTests
{
    [Fact]
    public void A_grant_never_prints_a_header_or_an_environment_value()
    {
        var http = new ServiceGrant("team-docs", new HttpServiceTransport(new Uri("https://mcp.example.internal/mcp"), new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }), ["search_docs"], new string('a', 64));
        var stdio = new ServiceGrant("team-notes", new StdioServiceTransport(["npx", "-y", "example-notes-mcp"], new Dictionary<string, string> { ["NOTES_TOKEN"] = "s3cret" }), ["search_notes"], new string('b', 64));

        Assert.DoesNotContain("s3cret", http.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", http.Transport.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", stdio.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", stdio.Transport.ToString(), StringComparison.Ordinal);
    }
}
```

```csharp
// tests/Chargehand.Tests/ServiceResolverTests.cs
using Chargehand.Config;
using Chargehand.Mcp;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: a preset's services become grants; whatever does not resolve is dropped and reported, never fatal.</summary>
public class ServiceResolverTests
{
    private static string Secret(string item) => item == "docs-token" ? "s3cret" : throw new InvalidOperationException($"no secret source resolved '{item}'");

    private static readonly Dictionary<string, McpServerSettings> Servers = new()
    {
        ["team-docs"] = new(Url: "https://mcp.example.internal/docs", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {secret:docs-token}" }),
        ["no-token"] = new(Url: "https://mcp.example.internal/other", Headers: new Dictionary<string, string> { ["Authorization"] = "Bearer {secret:absent}" }),
    };

    private static FakeMcpServer Docs(string searchDescription = "Search the docs") => new(
        new FakeTool("search_docs", _ => FakeMcpServer.Text("x"), searchDescription, ReadOnly: true),
        new FakeTool("read_doc", _ => FakeMcpServer.Text("x")),
        new FakeTool("write_note", _ => FakeMcpServer.Text("x")));

    private static McpConnectionPool PoolFor(FakeMcpServer docs) =>
        new(Servers, Secret, async (name, _, _) => name == "team-docs" ? await docs.TransportAsync() : throw new IOException("connection refused"));

    [Fact]
    public async Task Names_and_globs_expand_to_the_servers_tools()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("team-docs", ["search_*", "read_doc"])], CancellationToken.None);

        var grant = Assert.Single(resolved.Grants);
        Assert.Equal(["read_doc", "search_docs"], grant.Tools);
        Assert.Equal("Bearer s3cret", ((HttpServiceTransport)grant.Transport).Headers["Authorization"]);
        Assert.Empty(Assert.Single(resolved.Report).Issues);
    }

    [Fact]
    public async Task A_requested_tool_the_server_lacks_is_reported_and_the_rest_granted()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("team-docs", ["search_docs", "missing_tool"])], CancellationToken.None);

        Assert.Equal(["search_docs"], Assert.Single(resolved.Grants).Tools);
        Assert.Equal(["tool_missing: missing_tool"], Assert.Single(resolved.Report).Issues);
    }

    [Fact]
    public async Task A_glob_that_matches_nothing_grants_nothing()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("team-docs", ["nope_*"])], CancellationToken.None);

        Assert.Empty(resolved.Grants);
        Assert.Equal(["tool_missing: nope_*"], Assert.Single(resolved.Report).Issues);
    }

    [Fact]
    public async Task An_unknown_server_is_dropped_with_an_issue()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("nowhere", ["a"])], CancellationToken.None);

        Assert.Empty(resolved.Grants);
        Assert.Equal(["unknown_server: nowhere"], Assert.Single(resolved.Report).Issues);
    }

    [Fact]
    public async Task An_unresolved_secret_drops_the_service_and_names_the_item_only()
    {
        await using var docs = Docs();
        await using var pool = PoolFor(docs);

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("no-token", ["a"])], CancellationToken.None);

        Assert.Empty(resolved.Grants);
        var issue = Assert.Single(Assert.Single(resolved.Report).Issues);
        Assert.StartsWith("secret_unresolved:", issue, StringComparison.Ordinal);
        Assert.Contains("absent", issue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreachable_server_is_dropped_and_the_others_still_resolve()
    {
        var servers = new Dictionary<string, McpServerSettings>(Servers) { ["down"] = new(Url: "https://mcp.example.internal/down") };
        await using var docs = Docs();
        await using var pool = new McpConnectionPool(servers, Secret, async (name, _, _) => name == "team-docs" ? await docs.TransportAsync() : throw new IOException("connection refused"));

        var resolved = await new ServiceResolver(pool).ResolveAsync([new ServiceUse("down", ["a"]), new ServiceUse("team-docs", ["read_doc"])], CancellationToken.None);

        Assert.Equal(["read_doc"], Assert.Single(resolved.Grants).Tools);
        Assert.StartsWith("unreachable:", Assert.Single(resolved.Report[0].Issues), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_grant_hash_follows_the_tool_descriptions()
    {
        await using var a = Docs("Search the docs");
        await using var b = Docs("Search the docs");
        await using var c = Docs("Search the docs, and the wiki");
        var use = new[] { new ServiceUse("team-docs", ["search_docs"]) };

        var (ha, hb, hc) = (await Hash(a, use), await Hash(b, use), await Hash(c, use));

        Assert.Equal(ha, hb);
        Assert.NotEqual(ha, hc);
    }

    private static async Task<string> Hash(FakeMcpServer docs, ServiceUse[] use)
    {
        await using var pool = PoolFor(docs);
        return Assert.Single((await new ServiceResolver(pool).ResolveAsync(use, CancellationToken.None)).Grants).Sha256;
    }
}
```

```csharp
// tests/Chargehand.Tests/ServiceRunTests.cs
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Prompts;
using Chargehand.RunLog;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: the orchestrator resolves a preset's services once per run and hands the grants to the node.</summary>
public class ServiceRunTests
{
    private const string Docs = "    services:\n      - server: team-docs\n        tools: [search_docs, read_doc]\n";

    private static readonly ServiceGrant Grant = new("team-docs",
        new HttpServiceTransport(new Uri("https://mcp.example.internal/docs"), new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }),
        ["search_docs", "read_doc"], new string('a', 64));

    private sealed class FakeResolver(ResolvedServices? resolved = null, Exception? fail = null) : IServiceResolver
    {
        public List<IReadOnlyList<ServiceUse>> Asked { get; } = [];

        public Task<ResolvedServices> ResolveAsync(IReadOnlyList<ServiceUse> uses, CancellationToken ct)
        {
            Asked.Add(uses);
            return fail is null ? Task.FromResult(resolved!) : Task.FromException<ResolvedServices>(fail);
        }
    }

    private static RunRequest Request(RepositoryRef repo, string preset) => new("request/v1", "What does the README say?", new RequestContext(false, preset, Repository: repo));

    private static Orchestrator Make(string workerRoot, string installRoot, ScriptedRuntime runtime, IRunLog log, IServiceResolver resolver) =>
        new(Runs.Profile(workerRoot), runtime, "2.0.16", installRoot, log, new Dictionary<string, int>(), null, resolver);

    private static string PlainToolsHash() =>
        PromptChains.ToolsSha256("2.0.16", "build", Preset.Load(Repo.Path("presets"), "cheap").NodeKinds["worker"].Rules);

    [Fact]
    public async Task A_granted_service_reaches_the_node_spec_and_changes_the_tools_hash()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var resolver = new FakeResolver(new ResolvedServices([Grant], [new ServiceReport("team-docs", ["search_docs", "read_doc"], [])]));

        var result = await Make(root.Path, presets.Path, runtime, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), resolver).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        Assert.Equal([new ServiceUse("team-docs", ["search_docs", "read_doc"])], Assert.Single(resolver.Asked));
        Assert.Same(Grant, Assert.Single(runtime.Created.Single().Services!));
        Assert.NotEqual(PlainToolsHash(), result.PromptChain.AsSent.ToolsSha256);
    }

    [Fact]
    public async Task No_services_leave_the_tools_hash_alone()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var resolver = new FakeResolver(new ResolvedServices([], []));

        var result = await Make(root.Path, Repo.Root, runtime, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), resolver).RunAsync(Runs.CheapRequest(repo), CancellationToken.None);

        Assert.Empty(resolver.Asked);
        Assert.Equal(PlainToolsHash(), result.PromptChain.AsSent.ToolsSha256);
        Assert.True(runtime.Created.Single().Services is null or { Count: 0 });
    }

    [Fact]
    public async Task A_dropped_service_does_not_fail_the_run_and_is_in_the_run_log()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var resolver = new FakeResolver(new ResolvedServices([], [new ServiceReport("team-docs", [], ["unreachable: connection refused"])]));

        var result = await Make(root.Path, presets.Path, new ScriptedRuntime(Runs.WorkerReply), log, resolver).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        var service = Assert.Single((await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!.Services);
        Assert.Equal(["unreachable: connection refused"], service.Issues);
    }

    [Fact]
    public async Task A_resolver_that_throws_does_not_fail_the_run()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", Docs);
        var repo = Runs.GitRepo(root.Path);

        var result = await Make(root.Path, presets.Path, new ScriptedRuntime(Runs.WorkerReply), new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")),
            new FakeResolver(fail: new IOException("closed"))).RunAsync(Request(repo, "docs"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "PresetServicesTests|ServiceGrantTests|ServiceResolverTests|ServiceRunTests|ConfigFileTests"`
Expected: FAIL to compile (`ServiceUse`, `ServiceGrant`, `IServiceResolver`, `ServiceResolver` do not exist).

- [ ] **Step 3: Implement**

- `preset.schema.json` `node_kind.properties.services`: array of `{server: string pattern ^[a-z][a-z0-9-]*$, tools: array minItems 1 of
  strings not equal to "*" (pattern ^(?!\*$).+$)}`, `additionalProperties: false`. Ask the maintainer's schema guard (a prompt
  appears); it is additive.
- `McpConnectionPool.Transport(string server) : ServiceTransport` resolves a server's `{secret:item}` values into an
  `HttpServiceTransport` or `StdioServiceTransport`, throwing `McpUnavailableException` (`unknown_server`, `secret_unresolved`)
  before any connection. `ServiceResolver(McpConnectionPool pool)` per use: `Transport`, then `GetAsync` and `ListToolsAsync`
  (any failure → `unreachable`); each `McpUnavailableException` becomes the issue `"<Code>: <Detail>"`. Expand names and globs
  (`*` = any run of characters) against the listed names, sort, report each pattern with no match as `tool_missing: <pattern>`,
  and grant when any tool remains. `Sha256` = SHA-256 of the JSON of `[name, description, inputSchema]` for the granted tools
  sorted by name.
- `Orchestrator.Execute`: `var granted = kind.Services is { Count: > 0 } && services is not null ? await Resolve(kind.Services) : empty;`
  where `Resolve` catches every exception except the caller's cancellation and records a `ServiceReport(server, [], ["unreachable: …"])`.
  `NodeSpec` gets `Services = granted.Grants`; the chain's `AsSent.ToolsSha256` is recomputed with the grants; the reports go to
  the collector's `Services`.
- `PromptChains.ToolsSha256`: serialise exactly as before when there are no grants; with grants, add `services = grants.Select(g => new[] { g.Server, string.Join(",", g.Tools), g.Sha256 })`.
- `Program.cs`: `var mcpPool = new McpConnectionPool(profile.McpServers ?? new Dictionary<string, McpServerSettings>(), profile.Secret);`
  and `new ServiceResolver(mcpPool)` passed as the orchestrator's last argument in `run`, `serve` and `mcp`; dispose the pool on exit.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "PresetServicesTests|ServiceGrantTests|ServiceResolverTests|ServiceRunTests|ConfigFileTests|SchemaCompatTests"` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add schemas/preset src/Chargehand src/Chargehand.Mcp src/Chargehand.Cli/Program.cs tests/Chargehand.Tests
git commit -m "feat: let a preset name MCP services and resolve them into grants per run"
```

---

### Task 9: Claude Code gives workers the granted services

**Files:**
- Modify: `src/Chargehand.ClaudeCode/ClaudeCodeWorkerRuntime.cs` (`ServiceConfig`, a per-session private directory, `--mcp-config`, `--allowedTools`; cleanup)
- Create: `tests/Chargehand.Tests/ClaudeCodeServicesTests.cs`
- Modify: `docs/adr/0020-claude-code-runtime-adapter.md` (a dated addendum: MCP servers for services; what the spike observed)

**Interfaces:**
- Consumes: `NodeSpec.Services`, `ServiceGrant`, `HttpServiceTransport`, `StdioServiceTransport` (Task 8); Task 1's observations (tool naming, `--tools` and `--bare` behaviour).
- Produces: `ClaudeCodeWorkerRuntime.ServiceConfig(IReadOnlyList<ServiceGrant> grants) : (string Json, IReadOnlyList<string> Allowed)` (internal); with grants, each turn's argv gains `--mcp-config <file>` and `--allowedTools mcp__<server>__<tool>` per granted tool; the file is created with mode 0600 in a fresh 0700 directory under the system temp directory, never under the session's checkout, and removed when the turn's process has exited.
- Adjust `ServiceConfig` and the argument names below to what Task 1 recorded (C1, C3, C4) before writing the tests.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Chargehand.Tests/ClaudeCodeServicesTests.cs
using System.Text.Json;
using Chargehand.ClaudeCode;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>Goal 0.6: granted services reach a Claude Code worker as a private --mcp-config file and explicit allowed tools.</summary>
public sealed class ClaudeCodeServicesTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly ServiceGrant Docs = new("team-docs",
        new HttpServiceTransport(new Uri("https://mcp.example.internal/docs"), new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }),
        ["search_docs", "read_doc"], new string('a', 64));

    private static readonly ServiceGrant Notes = new("team-notes",
        new StdioServiceTransport(["npx", "-y", "example-notes-mcp"], new Dictionary<string, string> { ["NOTES_TOKEN"] = "s3cret" }),
        ["search_notes"], new string('b', 64));

    private NodeSpec Spec(params ServiceGrant[] grants) =>
        new(_dir.Path, "build", new ModelRef("anthropic", "claude-sonnet-5"), [], new Dictionary<string, string>(), grants);

    [Fact]
    public void The_config_lists_each_server_in_claude_codes_format()
    {
        var (json, allowed) = ClaudeCodeWorkerRuntime.ServiceConfig([Docs, Notes]);

        using var doc = JsonDocument.Parse(json);
        var servers = doc.RootElement.GetProperty("mcpServers");
        var docs = servers.GetProperty("team-docs");
        Assert.Equal(("http", "https://mcp.example.internal/docs", "Bearer s3cret"),
            (docs.GetProperty("type").GetString(), docs.GetProperty("url").GetString(), docs.GetProperty("headers").GetProperty("Authorization").GetString()));
        var notes = servers.GetProperty("team-notes");
        Assert.Equal("npx", notes.GetProperty("command").GetString());
        Assert.Equal(["-y", "example-notes-mcp"], notes.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("s3cret", notes.GetProperty("env").GetProperty("NOTES_TOKEN").GetString());
        Assert.Equal(["mcp__team-docs__search_docs", "mcp__team-docs__read_doc", "mcp__team-notes__search_notes"], allowed);
    }

    [Fact]
    public async Task A_session_with_grants_passes_a_private_config_file_and_only_the_granted_tools()
    {
        var rt = await ConnectAsync(FakeClaude());
        var session = await rt.CreateAsync(Spec(Docs), CancellationToken.None);

        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        Assert.Equal(IdleOutcome.Succeeded, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));

        var args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        var config = args[Array.IndexOf(args, "--mcp-config") + 1];
        Assert.Contains("--strict-mcp-config", args);
        Assert.Contains("mcp__team-docs__search_docs", args);
        Assert.Contains("mcp__team-docs__read_doc", args);
        Assert.DoesNotContain("s3cret", string.Join(' ', args), StringComparison.Ordinal);              // no credential on the command line
        Assert.False(config.StartsWith(_dir.Path, StringComparison.Ordinal));                             // outside the checkout
        Assert.Equal("-rw-------", File.ReadAllText(Path.Combine(_dir.Path, "mcp.mode")).Trim());       // the file the CLI read was private
        Assert.Contains("Bearer s3cret", File.ReadAllText(Path.Combine(_dir.Path, "mcp.json")), StringComparison.Ordinal);
        Assert.False(File.Exists(config));                                                                 // gone once the turn ended
    }

    [Fact]
    public async Task A_session_without_grants_passes_no_mcp_config()
    {
        var rt = await ConnectAsync(FakeClaude());
        var session = await rt.CreateAsync(Spec(), CancellationToken.None);

        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        await rt.AwaitIdleAsync(session.Id, CancellationToken.None);

        var args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        Assert.DoesNotContain("--mcp-config", args);
        Assert.Contains("--strict-mcp-config", args);
    }

    [Fact]
    public async Task A_fork_keeps_the_grants_of_its_source()
    {
        var rt = await ConnectAsync(FakeClaude());
        var source = await rt.CreateAsync(Spec(Docs), CancellationToken.None);
        await rt.SubmitAsync(source.Id, "hi", CancellationToken.None);
        await rt.AwaitIdleAsync(source.Id, CancellationToken.None);
        var firstUser = (await rt.ReadMessagesAsync(source.Id, CancellationToken.None)).Last(m => m.Kind == WorkerMessageKind.User);
        File.Delete(Path.Combine(_dir.Path, "args.txt"));

        var fork = await rt.ForkAsync(source.Id, firstUser.Id, CancellationToken.None);
        await rt.SubmitAsync(fork.Id, "again", CancellationToken.None);
        await rt.AwaitIdleAsync(fork.Id, CancellationToken.None);

        Assert.Contains("--mcp-config", File.ReadAllLines(Path.Combine(_dir.Path, "args.txt")));
    }

    private static Task<ClaudeCodeWorkerRuntime> ConnectAsync(string claude) =>
        ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("k", false), CancellationToken.None);

    /// <summary>A stand-in CLI that records argv and copies the --mcp-config file (and its mode) while it still exists.</summary>
    private string FakeClaude()
    {
        _dir.Write("events.jsonl", """{"type":"result","subtype":"success","is_error":false,"result":"hi"}""" + "\n");
        var script = _dir.Write("claude", $"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "2.1.195 (Claude Code)"; exit 0; fi
            printf '%s\n' "$@" > "{_dir.Path}/args.txt"
            prev=""
            for a in "$@"; do
              if [ "$prev" = "--mcp-config" ]; then
                cp "$a" "{_dir.Path}/mcp.json"
                ls -l "$a" | cut -c1-10 > "{_dir.Path}/mcp.mode"
              fi
              prev="$a"
            done
            cat > "{_dir.Path}/stdin.txt"
            cat "{_dir.Path}/events.jsonl"
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter ClaudeCodeServicesTests`
Expected: FAIL to compile (`ServiceConfig` does not exist).

- [ ] **Step 3: Implement**

- `ServiceConfig`: `{"mcpServers": {name: entry}}` with `{"type":"http","url","headers"}` for `HttpServiceTransport` and
  `{"command","args","env"}` for `StdioServiceTransport`; `Allowed` is `mcp__<server>__<tool>` for each tool in grant order.
- `Start`: when `s.Spec.Services` is non-empty, create `Directory.CreateTempSubdirectory("chargehand-mcp-")` with mode 0700
  (`UnixFileMode` on the created directory), write `mcp.json` through a `FileStream` opened with
  `UnixCreateMode = UserRead | UserWrite`, and add `--mcp-config <path>` after the existing flags plus each allowed tool to the
  `--allowedTools` list. Delete the directory in the `Task.Run` after the process exits (also on failure). `ConnectAsync` sweeps
  `chargehand-mcp-*` directories older than a day, best effort, for a crash between create and delete.
- `Fork` already copies `source.Spec` (`ClaudeCodeWorkerRuntime.cs:142-156`), so the grants follow.
- If Task 1 shows a non-granted tool of a connected server stays in the model's catalog, add `--disallowedTools` entries for the
  server's other tools (the resolver already knows the list; carry it as `ServiceGrant.Hidden`) and pin them here.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "ClaudeCodeServicesTests|ClaudeCodeRuntimeTests|ClaudeCodeDefaultsTests"` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add src/Chargehand.ClaudeCode docs/adr/0020-claude-code-runtime-adapter.md tests/Chargehand.Tests
git commit -m "feat: give Claude Code workers the services a preset grants"
```

---

### Task 10: OpenCode gives workers the granted services (may slip)

Ships only if Task 1 shows OpenCode can gate MCP tools per session (decided item 1). Otherwise this task becomes: refuse,
with a clear `invalid_request` error and action, a preset that lists services when the runtime is OpenCode on a server
chargehand did not start; and open the follow-up.

**Files:**
- Modify: `src/Chargehand.OpenCode/IOpenCodeClient.cs`, `src/Chargehand.OpenCode/OpenCodeClient.cs` (`PutMcpServerAsync`, `RemoveMcpServerAsync`, `McpServersAsync`)
- Modify: `src/Chargehand.OpenCode/OpenCodeWorkerRuntime.cs` (add servers before the session, generated rules, reference-counted removal)
- Create: `src/Chargehand/Runtime/ISessionCleanup.cs`; Modify: `src/Chargehand/Nodes/WorkerNode.cs` (tell a runtime that implements it when the node ends)
- Modify: `tests/Chargehand.Tests/WorkerNodeTests.cs` (its private `FakeRuntime` implements `ISessionCleanup`, recording released ids)
- Modify: `docs/opencode-adapter-ops.json` (the three operations, with their request properties)
- Create: `tests/Chargehand.Tests/OpenCodeServicesTests.cs`
- Modify: `docs/adr/0004-opencode-major-and-runtime-adapter.md` (an addendum: the MCP routes are used; `/api/config*` is still not)

**Interfaces:**
- Consumes: `NodeSpec.Services` and the grants (Task 8), Task 1's O1 to O8 (location encoding, permission action for an MCP tool, whether `* * allow` reaches it).
- Produces:
  - `IOpenCodeClient.PutMcpServerAsync(string name, string directory, McpConfigBody config, CancellationToken ct)`, `RemoveMcpServerAsync(string name, string directory, CancellationToken ct)`, `McpServersAsync(string directory, CancellationToken ct) : IReadOnlyList<McpServerStatus>`; `McpConfigBody(string Type, string? Url, IReadOnlyDictionary<string,string>? Headers, IReadOnlyList<string>? Command, IReadOnlyDictionary<string,string>? Environment)`; `McpServerStatus(string Name, string Status)`.
  - In `CreateAsync`: for each grant `PUT` (remote for `HttpServiceTransport`, local for `StdioServiceTransport`), `GET` status, keep only grants whose server is `connected`; append rules generated from the kept grants after the preset's rules; a reference count per `(directory, name)` so a shared server is `DELETE`d by the last node that used it.
  - `ISessionCleanup.ReleaseAsync(string sessionId, CancellationToken ct)`: an optional interface a runtime implements; `WorkerNode.RunAsync` calls it in a `finally` (exceptions swallowed) once the node's result is built.
  - A skipped grant (not connected) sets the activity tag `chargehand.service.skipped` on the current span and is otherwise silent; the run goes on without it.
- Fill the markers `TASK-1` below from the ADR's evidence section before writing the tests.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Chargehand.Tests/OpenCodeServicesTests.cs
using System.Net;
using System.Text;
using System.Text.Json;
using Chargehand.OpenCode;
using Chargehand.Runtime;

namespace Chargehand.Tests;

public class OpenCodeServicesTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, string?, (HttpStatusCode, string)> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Seen)
                Seen.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            var (status, json) = respond(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private const string Session = """{"data":{"id":"ses_1","agent":"build","model":{"id":"m","providerID":"p","variant":"default"},"location":{"directory":"/w/repo"},"outcome":null,"projectID":"x"}}""";

    private static readonly ServiceGrant Docs = new("team-docs",
        new HttpServiceTransport(new Uri("https://mcp.example.internal/docs"), new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }),
        ["search_docs", "read_doc"], new string('a', 64));

    private static (OpenCodeWorkerRuntime Runtime, FakeHandler Handler) Make(bool connected = true)
    {
        var status = connected ? """{"status":"connected"}""" : """{"status":"failed","error":"connection refused"}""";
        var h = new FakeHandler((req, _) => req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath == "/api/mcp"
            ? (HttpStatusCode.OK, $$"""{"data":[{"name":"team-docs","status":{{status}}}],"location":{"directory":"/w/repo"}}""")
            : req.Method == HttpMethod.Post && req.RequestUri!.AbsolutePath == "/api/session" ? (HttpStatusCode.OK, Session)
            : (HttpStatusCode.NoContent, ""));
        var client = new OpenCodeClient(new HttpClient(h) { BaseAddress = new Uri("http://127.0.0.1:4096") }, "pw");
        return (new OpenCodeWorkerRuntime(client, "2.0.18"), h);
    }

    private static NodeSpec Spec(params ServiceGrant[] grants) =>
        new("/w/repo", "build", new ModelRef("p", "m"), [new PermissionRule("*", "*", PermissionEffect.Allow)], new Dictionary<string, string>(), grants);

    [Fact]
    public async Task A_remote_grant_is_added_for_the_location_before_the_session_is_created()
    {
        var (rt, h) = Make();

        await ((IWorkerRuntime)rt).CreateAsync(Spec(Docs), CancellationToken.None);

        var put = h.Seen[0];
        Assert.Equal(HttpMethod.Put, put.Method);
        Assert.StartsWith("/api/experimental/mcp/team-docs", put.Path, StringComparison.Ordinal);
        Assert.Contains("/w/repo", Uri.UnescapeDataString(put.Path), StringComparison.Ordinal); // TASK-1: the location as O2 found it takes (query or header)
        using var body = JsonDocument.Parse(put.Body!);
        var config = body.RootElement.GetProperty("config");
        Assert.Equal(("remote", "https://mcp.example.internal/docs", "Bearer s3cret"),
            (config.GetProperty("type").GetString(), config.GetProperty("url").GetString(), config.GetProperty("headers").GetProperty("Authorization").GetString()));
        Assert.Equal(HttpMethod.Post, h.Seen.First(s => s.Path.StartsWith("/api/session", StringComparison.Ordinal)).Method);
        Assert.True(h.Seen.FindIndex(s => s.Method == HttpMethod.Put) < h.Seen.FindIndex(s => s.Path == "/api/session"));
    }

    [Fact]
    public async Task A_stdio_grant_is_added_as_a_local_server_with_its_environment()
    {
        var notes = new ServiceGrant("team-notes", new StdioServiceTransport(["npx", "-y", "example-notes-mcp"], new Dictionary<string, string> { ["NOTES_TOKEN"] = "s3cret" }), ["search_notes"], new string('b', 64));
        var (rt, h) = Make();

        await ((IWorkerRuntime)rt).CreateAsync(Spec(notes), CancellationToken.None);

        using var body = JsonDocument.Parse(h.Seen.First(s => s.Method == HttpMethod.Put).Body!);
        var config = body.RootElement.GetProperty("config");
        Assert.Equal("local", config.GetProperty("type").GetString());
        Assert.Equal(["npx", "-y", "example-notes-mcp"], config.GetProperty("command").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal("s3cret", config.GetProperty("environment").GetProperty("NOTES_TOKEN").GetString());
    }

    [Fact]
    public async Task The_session_rules_allow_only_the_granted_tools()
    {
        var (rt, h) = Make();

        await ((IWorkerRuntime)rt).CreateAsync(Spec(Docs), CancellationToken.None);

        using var body = JsonDocument.Parse(h.Seen.First(s => s.Path == "/api/session").Body!);
        var rules = body.RootElement.GetProperty("permissions").EnumerateArray().Select(r => $"{r.GetProperty("action").GetString()} {r.GetProperty("resource").GetString()} {r.GetProperty("effect").GetString()}").ToList();
        Assert.Equal("* * allow", rules[0]);                                                     // the preset's own rules come first
        // TASK-1: replace with the generated rules O3 and O5 justify, e.g. a deny for the server's tools then an allow per granted tool:
        Assert.Contains("<mcp-action-for-search_docs> * allow", rules);
        Assert.Contains("<mcp-action-for-read_doc> * allow", rules);
    }

    [Fact]
    public async Task A_server_that_did_not_connect_is_dropped_and_the_run_goes_on()
    {
        var (rt, h) = Make(connected: false);

        var session = await ((IWorkerRuntime)rt).CreateAsync(Spec(Docs), CancellationToken.None);

        Assert.Equal("ses_1", session.Id);
        using var body = JsonDocument.Parse(h.Seen.First(s => s.Path == "/api/session").Body!);
        Assert.DoesNotContain("search_docs", body.RootElement.GetProperty("permissions").GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_nodes_share_a_server_and_the_last_one_removes_it()
    {
        var (rt, h) = Make();
        IWorkerRuntime runtime = rt;
        var first = await runtime.CreateAsync(Spec(Docs), CancellationToken.None);
        var second = await runtime.CreateAsync(Spec(Docs), CancellationToken.None);

        await ((ISessionCleanup)rt).ReleaseAsync(first.Id, CancellationToken.None);
        Assert.DoesNotContain(h.Seen, s => s.Method == HttpMethod.Delete);
        await ((ISessionCleanup)rt).ReleaseAsync(second.Id, CancellationToken.None);

        Assert.Single(h.Seen, s => s.Method == HttpMethod.Delete && s.Path.StartsWith("/api/experimental/mcp/team-docs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_grants_call_no_mcp_route()
    {
        var (rt, h) = Make();

        await ((IWorkerRuntime)rt).CreateAsync(Spec(), CancellationToken.None);

        Assert.DoesNotContain(h.Seen, s => s.Path.Contains("/mcp", StringComparison.Ordinal));
    }
}
```

and in `WorkerNodeTests` (add `ISessionCleanup` to its `FakeRuntime` with `public List<string> Released { get; } = [];`; the node tells a
runtime with session cleanup that it is done, even when the node fails):

```csharp
    [Fact]
    public async Task A_runtime_with_session_cleanup_is_told_when_the_node_ends()
    {
        var rt = new FakeRuntime("no block", "still no block"); // no valid contract after the repair: the node fails, cleanup still runs
        var r = await Node(rt).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Contract.Status);
        Assert.Equal([r.SessionId], rt.Released);
    }
```

The adapter-ops contract (`OpenCodeSpecContractTests`) gains the three operations once they are in
`docs/opencode-adapter-ops.json`:

```json
{ "op": "PUT /api/experimental/mcp/{server}", "port": "AddService", "body": ["config"], "mode": "blocking; connects at once", "errors_seen": [] },
{ "op": "DELETE /api/experimental/mcp/{server}", "port": "RemoveService", "mode": "blocking; until restart", "errors_seen": [404] },
{ "op": "GET /api/mcp", "port": "ServiceStatus", "mode": "blocking; name and status", "errors_seen": [] }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "OpenCodeServicesTests|OpenCodeSpecContractTests"`
Expected: FAIL to compile (`PutMcpServerAsync`, `ReleaseServicesAsync` do not exist).

- [ ] **Step 3: Implement**

- The client methods follow the existing `Send` and `Data` helpers; `location` is given the way O2 found it works. Bodies and
  errors are never logged (ADR 0004's rule about bodies that can carry keys covers header values here too).
- `OpenCodeWorkerRuntime.CreateAsync`: for each grant, `PutMcpServerAsync`; one `McpServersAsync`; keep grants whose server
  status is connected (poll up to 5 s if O1 showed the connection is not immediate); append the generated rules; record
  `sessionId → (directory, server names)` and a `ConcurrentDictionary<(string, string), int>` count.
  `ReleaseAsync(sessionId, ct)` (`ISessionCleanup`, called by `WorkerNode` when the node ends) decrements and `DELETE`s at zero.
- Rules: exactly what Task 1's O3 to O6 justify, and the tests above carry that shape. If O4 showed foreign servers reach
  workers under `* * allow`, also append, for every server `GET /api/mcp` lists that is not in the grants, a deny for that
  server's tools, and add a test for it.
- `docs/opencode-adapter-ops.json`: add the three operations; regenerate `docs/opencode-api.md` with `scripts/gen-opencode-api.py`
  if it lists operations by use.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "OpenCodeServicesTests|OpenCodeClientTests|OpenCodeSpecContractTests|OpenCodeServerProcessTests"` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add src/Chargehand.OpenCode docs/opencode-adapter-ops.json docs/adr/0004-opencode-major-and-runtime-adapter.md tests/Chargehand.Tests
git commit -m "feat: give OpenCode workers the services a preset grants"
```

---

### Task 11: Delete `HindsightMemory`; reject the object form of `memory`

Precondition, run by the maintainer and recorded in the pull request: with a running Hindsight service, `chargehand extensions
check --probe "<a real query>"` is green for the Hindsight entry of the spec, and one `chargehand run` recalls the same facts
through the list form as through the object form (the last time the object form works). Nothing here starts without that.

**Files:**
- Delete: `src/Chargehand/Memory/HindsightMemory.cs`, `tests/Chargehand.Tests/HindsightMemoryTests.cs`
- Modify: `src/Chargehand/Config/Profile.cs` (drop `MemorySettings`, `MemoryBlock.ObjectForm` and its converter branch; the object form throws the migration)
- Modify: `src/Chargehand/Memory/MemoryStack.cs` (drop `ForObjectForm`), `src/Chargehand.Mcp/MemoryStacks.cs` (drop the factory parameter), `src/Chargehand.Cli/Program.cs`
- Modify: `profiles/profile.schema.json` (only the array form), `tests/Chargehand.Tests/McpMemoryProviderTests.cs` (the parity test keeps its expectations as literals)
- Modify: `tests/Chargehand.Tests/MemoryConfigTests.cs`, `MemoryStacksTests.cs`, `MemoryRunTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 3 to 7.
- Produces: `Profile.Memory` as `IReadOnlyList<MemoryProviderSettings>?` again (no `MemoryBlock`); a `memory` object fails `Profile.Load` with `ChargehandException(InvalidRequest, "memory is a list now: …", action naming mcp_servers and the guide page)`.

- [ ] **Step 1: Write the failing tests**

```csharp
// added to tests/Chargehand.Tests/MemoryConfigTests.cs
    [Fact]
    public void The_object_form_of_memory_fails_with_the_migration()
    {
        using var dir = new TempDir();
        var path = dir.Write("p.json", """{"schema":"profile/v1","memory":{"backend":"hindsight","url":"http://memory.example.internal:8888","namespace":"ns"}}""");

        var e = Assert.Throws<ChargehandException>(() => Profile.Load(path));

        Assert.Equal(ErrorCode.InvalidRequest, e.Code);
        Assert.Contains("memory is a list now", e.Message, StringComparison.Ordinal);
        Assert.Contains("mcp_servers", e.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_profile_holds_no_object_form()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Repo.Path("profiles", "example.json")));
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("memory").ValueKind);
    }
```

```csharp
// tests/Chargehand.Tests/McpMemoryProviderTests.cs: the parity test, with the values HindsightMemory sent written down
    [Fact]
    public async Task The_hindsight_mapping_sends_the_fields_the_http_client_sent()
    {
        var item = new MemoryItem("fact", "ctx", new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), "run-1", ["chargehand", "x"]);
        await using var server = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(Found)), new FakeTool("retain", _ => FakeMcpServer.Text("queued")),
            new FakeTool("invalidate_memory", _ => FakeMcpServer.Text("ok")));
        await using var pool = PoolFor(server);
        var mcp = new McpMemoryProvider(Settings(Hindsight), pool);

        await mcp.RecallAsync("how are deploys done", Scope, CancellationToken.None);
        await mcp.RetainAsync(item, Scope, CancellationToken.None);
        await mcp.InvalidateAsync("f1", "wrong", Scope, CancellationToken.None);

        // What HindsightMemory sent (0.4.0): recall {query, budget "low", max_tokens 1024} on the namespace's bank; retain
        // {content, context, timestamp, document_id, tags} asynchronously; invalidate {state, reason} on the id.
        var (recall, retain, invalidate) = (server.Calls[0].Arguments, server.Calls[1].Arguments, server.Calls[2].Arguments);
        Assert.Equal(("how are deploys done", "low", 1024, "chargehand"), (recall["query"].GetString(), recall["budget"].GetString(), recall["max_tokens"].GetInt32(), recall["bank_id"].GetString()));
        Assert.Equal(("fact", "ctx", "run-1", "2026-09-29T12:00:00+00:00"), (retain["content"].GetString(), retain["context"].GetString(), retain["document_id"].GetString(), retain["timestamp"].GetString()));
        Assert.Equal(["chargehand", "x"], retain["tags"].EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(("f1", "wrong"), (invalidate["memory_id"].GetString(), invalidate["reason"].GetString()));
    }
```

`MemoryStacksTests` and `MemoryRunTests` lose their object-form tests (`The_object_form_becomes_one_source_named_hindsight`,
`The_object_form_uses_the_supplied_factory`); the `NoObjectForm` factory argument disappears from the calls.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "MemoryConfigTests|McpMemoryProviderTests"`
Expected: FAIL (`memory` object still loads; the old parity test still compiles against `HindsightMemory`).

- [ ] **Step 3: Implement**

- Remove the deleted types; `git grep -n "HindsightMemory\|MemorySettings\|ObjectForm"` returns hits in `CHANGELOG.md` and ADRs only.
- The converter throws `ChargehandException(ErrorCode.InvalidRequest, "memory is a list now: the object form (backend, url, namespace) was removed",
  "Move url and api_key_secret into mcp_servers, and list the provider under memory with its tools mapping. docs/guide/memory-and-services.md has the Hindsight entry.")`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test` — expected PASS.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add -A src tests profiles
git commit -m "feat!: remove the Hindsight HTTP client; memory is a list of MCP providers"
```

---

### Task 12: Acceptance test (Chronicle-shaped second provider), guide with both mappings, changelog, roadmap

**Files:**
- Create: `tests/Chargehand.Tests/GoalSixTests.cs`
- Create: `docs/guide/memory-and-services.md` (front matter like the other guide pages: `title`, `description`, `order`, `section`)
- Modify: `docs/guide/reference.md` (`mcp_servers` and `memory` rows; the failure table), `docs/guide/capabilities.md` (the memory sections and their tests; services), `docs/guide/decisions.md` (rows for 0032, 0033 and 0034, and the status cells of 0008 and 0026, whose files changed with ADR 0034's acceptance), `docs/guide/index.md`
- Modify: `README.md` (one paragraph), `ROADMAP.md` (0.6 done), `CHANGELOG.md` (`Unreleased`: Added, Changed, Removed, with the `memory` migration)
- Modify: `docs/specs/2026-09-29-services-and-memory-design.md` (status: implemented)

**Interfaces:**
- Consumes: all of the above, in their state after Task 11 (`Profile.Memory` is a plain list; `MemoryStacks.From(profile, pool)` takes no object-form factory). Produces: the done bar as a scripted test in CI, and the maintainer's live checklist.

- [ ] **Step 1: Write the failing test** (the done bar, in process)

```csharp
// tests/Chargehand.Tests/GoalSixTests.cs
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.Mcp;
using Chargehand.Memory;
using Chargehand.RunLog;
using Chargehand.Runtime;
using static Chargehand.Tests.MemoryConfigTests;

namespace Chargehand.Tests;

/// <summary>
/// Goal 0.6 done bar: a run recalls from two stacked memory providers, retains only claims whose citations resolved, and a preset
/// gives workers a service through MCP. Real MCP servers on in-process pipes, the real adapter, stack, resolver and orchestrator.
/// </summary>
public class GoalSixTests
{
    private const string TwoClaims = """
        ```json
        {"status":"completed","summary":"SUMMARY-MARKER","claims":[
           {"text":"The README says hello.","evidence":["e1"],"confidence":0.9},
           {"text":"The caller says v1 shipped.","evidence":["e2"],"confidence":0.7}],
         "evidence":[{"id":"e1","kind":"file","locator":"README.md:1"},{"id":"e2","kind":"input","locator":"rel-v1"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    [Fact]
    public async Task A_run_recalls_from_hindsight_and_chronicle_shaped_providers_retains_checked_claims_and_gives_a_worker_a_service()
    {
        using var root = new TempDir();
        using var presets = new PresetRoot("docs", "    services:\n      - server: team-docs\n        tools: [search_docs, read_doc]\n");
        var repo = Runs.GitRepo(root.Path);
        await using var hindsight = new FakeMcpServer(
            new FakeTool("recall", _ => FakeMcpServer.Text("""{"results":[{"id":"f1","text":"Deploys go through GitOps."}]}""")),
            new FakeTool("retain", _ => FakeMcpServer.Text("queued")));
        await using var chronicle = new FakeMcpServer(new FakeTool("recall", _ => FakeMcpServer.Text(
            """{"intent":"open","routed_because":null,"window_from_query":null,"results":[{"segment_id":"seg-1","score":0.031,"date":"2024-05-03T10:12:00","thread":"chat-1","text":"raw","evidence":["ev-1"],"summary":"The API uses MediatR."}]}""")));
        await using var docs = new FakeMcpServer(new FakeTool("search_docs", _ => FakeMcpServer.Text("x"), ReadOnly: true), new FakeTool("read_doc", _ => FakeMcpServer.Text("x")), new FakeTool("write_note", _ => FakeMcpServer.Text("x")));
        var fakes = new Dictionary<string, FakeMcpServer> { ["memory-gateway"] = hindsight, ["chronicle"] = chronicle, ["team-docs"] = docs };

        var profile = Runs.Profile(root.Path) with
        {
            McpServers = new Dictionary<string, McpServerSettings>
            {
                ["memory-gateway"] = new(Url: "https://mcp.example.internal/mcp"),
                ["chronicle"] = new(Url: "http://localhost:8031/sse", Transport: "sse"),
                ["team-docs"] = new(Url: "https://mcp.example.internal/docs"),
            },
            Memory =
            [
                JsonSerializer.Deserialize<MemoryProviderSettings>(Hindsight.Replace("\"server\":\"gw\"", "\"server\":\"memory-gateway\""), Profile.Json)!,   // retain: true
                JsonSerializer.Deserialize<MemoryProviderSettings>(Chronicle, Profile.Json)!,                                                              // recall only
            ],
        };
        await using var pool = new McpConnectionPool(profile.McpServers!, _ => "", async (name, _, _) => await fakes[name].TransportAsync());
        var stack = MemoryStacks.From(profile, pool)!;
        var runtime = new ScriptedRuntime(TwoClaims);
        var log = new JsonlRunLog(Path.Combine(root.Path, "log.jsonl"));
        var request = new RunRequest("request/v1", "What does the README say?", new RequestContext(false, "docs", Repository: repo), [new CallerInput("rel-v1", "signal", "Released v1.")]);

        var result = await new Orchestrator(profile, runtime, "2.0.16", presets.Path, log, new Dictionary<string, int>(), stack, new ServiceResolver(pool)).RunAsync(request, CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, result.Status);
        // 1. two stacked providers, both labelled in the prompt; Chronicle's fact reads date and summary, and its limit is the fact cap
        var prompt = Assert.Single(runtime.Prompts);
        Assert.Contains("- [hindsight] Deploys go through GitOps.", prompt, StringComparison.Ordinal);
        Assert.Contains("- [chronicle] 2024-05-03T10:12:00: The API uses MediatR.", prompt, StringComparison.Ordinal);
        var asked = Assert.Single(chronicle.Calls);
        Assert.Equal(("recall", 10), (asked.Tool, asked.Arguments["limit"].GetInt32()));
        // 2. only the claim whose citation resolved to the repository is retained, with locator, repository and commit; Chronicle is never written to
        var kept = Assert.Single(hindsight.Calls, c => c.Tool == "retain").Arguments["content"].GetString()!;
        Assert.Contains($"commit {repo.Commit[..12]}", kept, StringComparison.Ordinal);
        Assert.Contains("- The README says hello. [README.md:1]", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("caller says", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("SUMMARY-MARKER", kept, StringComparison.Ordinal);
        // 3. the preset's service reaches the worker's node spec, and only the tools it names
        var grant = Assert.Single(runtime.Created.Single().Services!);
        Assert.Equal("team-docs", grant.Server);
        Assert.Equal(["read_doc", "search_docs"], grant.Tools);
        // and the run log says what happened
        var report = (await log.ReadAsync(result.TaskId, CancellationToken.None)).Run!.Extensions!;
        Assert.Equal(["hindsight", "chronicle"], report.Memory.Select(m => m.Source));
        Assert.Equal("team-docs", Assert.Single(report.Services).Server);
    }
}
```

(Add `using System.Text.Json;`.) The profile record `with` expression relies on `Runs.Profile` returning a `Profile`.

- [ ] **Step 2: Run the test to verify it fails, then passes**

Run: `dotnet test --filter GoalSixTests` — expected FAIL before Tasks 3 to 8 are on the branch; PASS after.

- [ ] **Step 3: Write the docs** (existing style: short paragraphs, one table at most; public wording rule)

- `docs/guide/memory-and-services.md`: what memory and services are and that both are off by default; the profile blocks with
  the Hindsight entry (a bank, retain) and the Chronicle entry (a legacy SSE server, `"transport": "sse"`, recall only,
  `{max_facts}` as the limit, a `text` list with a fallback) side by side, with `http://localhost:<port>/sse` as the placeholder
  URL and a line saying Chronicle is a personal archive whose recalled text reaches the model provider; the `transport` values;
  placeholders and result mapping in a table; how stacking labels facts and
  what a slow or failing provider does; the retain rule in one paragraph ("retains only claims whose citations resolved") and
  what the item looks like; a services example (a user's own preset, not a shipped one) and what a worker gets on each
  runtime; secrets with `{secret:item}`; `chargehand extensions check`; the failure table from the spec.
- `reference.md`: replace the `memory` row; add `mcp_servers`. `capabilities.md`: rewrite the two memory sections around
  `MemoryStackTests`, `MemoryRunTests`, `McpMemoryProviderTests`, `GoalSixTests`; state what is not tested (a real Hindsight, a
  real service on a real runtime) until the live run below is recorded.
- `CHANGELOG.md` `Unreleased`: **Added** MCP memory providers and stacking (any MCP memory server, including recall-only
  ones such as Chronicle), `mcp_servers` over Streamable HTTP, legacy SSE or stdio, services in presets,
  `chargehand extensions check`; **Changed** retain writes claims with locators, repository and commit, and no request text or
  summary; recall labels facts by source and each provider's failure is skipped on its own; **Removed** the Hindsight
  HTTP client, with the migration:

```json
"mcp_servers": { "memory-gateway": { "url": "https://<your MCP endpoint for the memory service>", "headers": { "Authorization": "Bearer {secret:<api-key-item>}" } } },
"memory": [ { "name": "hindsight", "server": "memory-gateway", "namespace": "<your bank>", "tools": { "recall": { … }, "retain": { … } } } ]
```

  (the full entry is in the guide page).
- `ROADMAP.md`: `0.6` line to done, with the wording "Runs use your MCP services and memory". `README.md`: one paragraph and a link.

- [ ] **Step 4: Full check**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

- [ ] **Step 5: The maintainer's live run** (record the result in the pull request; nothing here is automated)

1. `chargehand extensions check --probe "<a real query>"` against Hindsight, Chronicle (`transport: sse` on its loopback `/sse`
   URL) and one real service: all lines `ok`, and the Chronicle probe returns facts that read `<date>: <summary>`.
2. Hindsight and Chronicle as the two providers in the profile (the done bar's "two stacked memory providers"); `chargehand run` a
   question about a real repository: the run log (`chargehand show <run-id>`) lists both sources with counts, and the prompt chain
   shows two `memory/recall/<name>` blocks. Chronicle holds a personal archive, so use a profile meant for this check.
3. With `retain: true` on the Hindsight entry (Chronicle has no write tool), the same run: read the stored item in Hindsight; it
   names the repository, the commit and locators, holds no request text.
4. A preset of your own with `services`, on Claude Code and, if Task 10 shipped, on OpenCode: `chargehand show` lists the granted
   tools, and a question that needs the service is answered with a claim citing a `url` the service returned.
5. Stop one memory server: the run still completes, and the report says it was skipped.

- [ ] **Step 6: Commit**

```bash
git add tests/Chargehand.Tests/GoalSixTests.cs docs README.md ROADMAP.md CHANGELOG.md
git commit -m "docs: document memory and services in runs; add the goal 0.6 acceptance test"
```
