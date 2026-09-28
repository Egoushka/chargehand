# 0027. A dnx package and an MCP Registry listing

- Status: accepted
- Date: 2026-09-28

## Context

ROADMAP 0.4 ends with "listed in the MCP Registry"; ADR 0026 left the packaging as a separate spike. Today the only
MCP surface is `chargehand serve`: Streamable HTTP at `/v1/mcp` behind a bearer key (ADR 0018), shipped as a container
image (ADR 0024). Nothing is on nuget.org yet: `chargehand`, `chargehand.cli` and `chargehand.contracts` all return 404
from the flat-container index (checked 2026-09-28).

## Evidence

- A NuGet MCP server is a .NET tool package run by `dnx` (SDK 10+) as a local process speaking **stdio**; the package
  type `McpServer` is always paired with `DotnetTool`, and an embedded `.mcp/server.json` declares the inputs
  ([NuGet MCP](https://learn.microsoft.com/en-us/nuget/concepts/nuget-mcp),
  [package types](https://learn.microsoft.com/en-us/nuget/create-packages/set-package-type),
  [quickstart](https://learn.microsoft.com/en-us/dotnet/ai/quickstarts/build-mcp-server)). Microsoft recommends a
  self-contained package; framework-dependent is "reasonable" when a runtime is guaranteed, which `dnx` implies.
- Registry ([package types](https://github.com/modelcontextprotocol/registry/blob/main/docs/modelcontextprotocol-io/package-types.mdx),
  [server.json](https://github.com/modelcontextprotocol/registry/blob/main/docs/reference/server-json/generic-server-json.md),
  [GitHub Actions](https://github.com/modelcontextprotocol/registry/blob/main/docs/modelcontextprotocol-io/github-actions.mdx)):
  NuGet entries must be on nuget.org; ownership is an `mcp-name: <server name>` line in the package README; schema
  `2025-12-11` requires `name`, `description` (at most 100 characters) and `version`; `packageArguments` turn into
  `dnx <id>@<version> -- <args>`; `mcp-publisher login github-oidc` grants `io.github.<owner>/*` with `id-token: write`;
  the package must be published before the server entry. The registry is in preview: "breaking changes or data resets
  may occur". `mcp-publisher` is v1.8.1 (2026-08-06); its release carries a checksums file and a sigstore bundle per
  archive.
- nuget.org trusted publishing ([docs](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)): a
  policy covers all packages of its owner, and its scopes decide whether *new* package ids may be pushed.
- MCP C# SDK 2.2.0 has `WithStdioServerTransport` in `ModelContextProtocol`, which `ModelContextProtocol.AspNetCore`
  already pulls in.
- Spike (local, not committed): `Chargehand.Cli` packed with `PackAsTool`, `ToolCommandName=chargehand` and
  `PackageType=McpServer` gives a 2.0 MB package whose nuspec lists `DotnetTool` and `McpServer`, and whose
  runtimeconfig names `Microsoft.AspNetCore.App` next to `Microsoft.NETCore.App`. `dnx <id>@<version> --yes` from an
  empty directory runs it: usage on stdout, `dnx`'s own notices on stderr. `serve` there fails with "profile has no
  http settings". `prompts/` and `presets/` are not in the package.
- Code that assumes a checkout as the working directory: `Program.cs` sets `root = Directory.GetCurrentDirectory()`
  and reads `prompts/` and `presets/` under it (`Orchestrator.cs:53`, `:70`); `run_log` defaults to the relative
  `runs/run-log.jsonl`. A `dnx` launch runs in the client's working directory.
- Zero-config gaps on main: `Profile.Roots` is `[worker_root]` when the profile names no `repository_roots`, so a
  repository under the user's home is `repository_not_allowed` (`Orchestrator.cs:286`); `Connect()` still requires a
  `claude_code` block (pinned version, one credential secret) or an `opencode` block even when the runtime is detected.
- `WebApplication.CreateSlimBuilder` logs to the console, which is stdout; under stdio that corrupts the protocol.

## Options

**Transport of the package**
1. Package `serve` as is: the client launches the process, then connects to a loopback URL with a bearer key. No
   client config maps to that; a port per client, and a key to generate.
2. **Chosen.** A new `chargehand mcp` command: stdio, the same `orchestrate` tool, tasks store and instructions as
   `serve`, no listener and so no key, Host check or CORS surface. `serve` stays for HTTP and the private-network
   image. The registry entry lists the package only, no `remotes`: the ADR 0024 server is private.

**Runtime packaging**
1. Self-contained per RID (Microsoft's recommendation): one package per platform, a larger release, nothing gained
   while `dnx` itself needs the SDK.
2. **Chosen.** Framework-dependent, `net10.0`. Reopen when `dnx` ships in the runtime alone.

**Where the version lives**
1. Commit `.mcp/server.json` with the version and check it against `Directory.Build.props`: two files to bump.
2. **Chosen.** The committed file carries `0.0.0`; the release job writes `Version` from `Directory.Build.props` into
   both `version` fields (as the registry's own workflow does with `jq`) before `dotnet pack`, so the packed file and
   the published entry agree. `Directory.Build.props` stays the one source.

**Repository roots under `mcp` with no profile**
1. Keep `[worker_root]`: every first call fails with `repository_not_allowed`.
2. **Chosen.** When the profile names no `repository_roots`, `mcp` allows the roots the client declares (MCP
   `roots/list`) and, if it declares none, the launch directory. The client that starts the process is the user's own,
   so its roots are the user's consent. `serve` keeps today's rule.

## Decision

- **Package** `Chargehand` from `src/Chargehand.Cli`: `PackAsTool`, `ToolCommandName` `chargehand`, `PackageType`
  `McpServer`, `PackageReadmeFile` pointing at a package README with `<!-- mcp-name: io.github.egoushka/chargehand -->`,
  and `.mcp/server.json` packed at `/.mcp/`. `prompts/` and `presets/` ship in the tool; `mcp` reads them from
  `AppContext.BaseDirectory` unless the working directory has its own. The run log defaults to a per-user state
  directory under `mcp`, never the client's workspace.
- **server.json**: `name` `io.github.egoushka/chargehand`, `registryType` `nuget`, `runtimeHint` `dnx`,
  `transport` stdio, `packageArguments` one positional `mcp`, and optional `environmentVariables`
  `CHARGEHAND_PROFILE` and `CHARGEHAND_RUNTIME`. No required input: the goal is a client config of one line.
- **What the user brings**: the .NET 10 SDK, one agent CLI (`claude` or `opencode`) installed and signed in, and
  nothing else once goal 0.4's zero-config work lands. A profile stays optional for budgets, prices, memory and a pinned
  runtime version.
- **Release pipeline** (`release.yml`, same job and `release` environment): after the image, stamp the version, `dotnet
  pack src/Chargehand.Cli`, push with the key from the existing `NuGet/login` step, poll the flat-container index until
  the version is served, then install `mcp-publisher` at a pinned version with a checked SHA-256, `login github-oidc`,
  and `publish`. A registry failure fails the job after the NuGet push; rerunning publishes the entry only.
- **Checks**: CI packs the tool and runs `dnx` against the local package with an `initialize` over stdin, and validates
  `.mcp/server.json` against the pinned schema, so a broken package never reaches a tag.

## Consequences

- Blocked on goal 0.4: a detected Claude Code or OpenCode must run without a profile block (today `Connect()` throws
  without one), or the listing needs a required profile input and the one-line config is lost.
- Two MCP hosts share one tool definition; `ChargehandServer`'s MCP registration moves to a helper both call.
- The package carries the prompt and preset text; a release changes them for every `dnx` user who does not pin.
  Registry entries and client configs pin an exact version, like the image (ADR 0024).
- The nuget.org owner must allow new package ids in the trusted-publishing policy's scope before the first release.
- A registry data reset during preview means republishing; the pipeline step is idempotent per version.

**Tasks, sized** (S under a day, M one to three days):

| task | size | needs |
|---|---|---|
| `chargehand mcp`: stdio host, shared MCP registration, logs to stderr, tasks and elicitation checked over stdio | M | — |
| Tool content: pack `prompts/` and `presets/`, resolve from `AppContext.BaseDirectory`, per-user run log default | S | — |
| `mcp` repository roots from `roots/list` or the launch directory, with tests | S | first task |
| Package metadata: `PackAsTool`, `McpServer`, package README with `mcp-name`, `.mcp/server.json`, CI pack + `dnx` smoke + schema check | S | first two |
| Release: stamp, pack, push, wait for index, pinned `mcp-publisher`, OIDC publish | S | previous; owner widens the NuGet policy |
| Docs: README install line for Claude Code, VS Code and Claude Desktop | S | first release |

Unknowns: whether the tasks extension and elicitation behave over stdio as over HTTP (ADR 0018 checked HTTP only);
whether the registry treats the `io.github.<owner>` segment case-insensitively; how long nuget.org takes to serve a
new version's README to the registry's check.

Note, 2026-09-28: with SDK 2.2.0 both behave over stdio as over HTTP (`StdioMcpTests`, the SDK client on a pair of
in-process pipes). The server advertises the tasks extension on `initialize`; a task-augmented call returns a task and
polls to `result/v1`; intake's questions reach the client as an elicitation, inside a task through `input_required`
and outside one as a plain `elicitation/create`, since stdio always has a session. The one difference: no HTTP
request, so no `Prefer: wait=N` (ADR 0029); the tool reads the header only when an `IHttpContextAccessor` exists.

## Reopen if

`dnx` ships with the runtime alone (then self-contained packages pay off); the registry leaves preview with a changed
`server.json` schema or ownership rule; a user needs the private-network server listed as a remote; or goal 0.4 keeps
a required profile block for the detected runtime.
