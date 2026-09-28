# The change command (goal 0.5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Claude Code plugin shipped from this repository whose `/chargehand:change <goal>` takes one prompt to a
reviewed change on a local branch.

**Architecture:** A plugin directory `plugins/chargehand/` (manifest, MCP server entry, one skill) listed by a
marketplace file at the repository root. The skill is instructions to the user's Claude Code session: it calls the
existing `orchestrate` MCP tool twice (research, then review with the new read-only `review` preset), writes the code
itself, loops fix-and-review at most twice, and commits a report. No new chargehand endpoint.

**Tech Stack:** .NET 10 / xUnit (tests), YAML presets, Markdown prompt blocks and skill, Claude Code plugin format
(`.claude-plugin/plugin.json`, `.claude-plugin/marketplace.json`, `.mcp.json`, `skills/<name>/SKILL.md`), bash, GitHub
Actions.

**Spec:** `docs/specs/2026-09-29-change-command-design.md`

## Global Constraints

- The repository is public: never commit IP addresses, hostnames, absolute home paths, employer or project names, keys,
  tracker URLs or prompts from real runs.
- Done for every task: `scripts/check.sh` exits 0 and its last test line reads `Passed!  - Failed:     0`.
- Conventional Commits; never `--no-verify`; never force-push.
- One version source: `Directory.Build.props` (`<Version>`). Anything else that carries a version is checked against it by a test.
- Names (spec decision 14 and flow): command `/chargehand:change <goal>`; skill directory `plugins/chargehand/skills/change/`; branch `change/<short-slug>`; report `.chargehand/reports/<slug>.md`, committed separately as `docs: add /change report`.
- At most 2 fix rounds (3 reviews in all) per run (spec decision 3).
- Cost cap: the preset's `budget.max_usd`; `--budget <usd>` overrides it (spec decision 7).
- Presets are read-only: no edit, no shell (ADR 0006); every preset denies `*.env` and `*.env.*` after its broad allow.
- Public text says "citations checked", not "claims verified".

## Review Focus

1. The working tree has uncommitted changes or is not a git repository: the command refuses before creating a branch
   and says why. Pinned by `scripts/change-e2e.sh` case `dirty`.
2. chargehand's MCP server is missing or fails to start: the command stops in preflight with the fix, no branch.
   Pinned by `scripts/change-e2e.sh` case `no-server`.
3. The branch `change/<slug>` already exists: the command picks `change/<slug>-2` (then `-3`, …) instead of failing or
   reusing it. Pinned by `scripts/change-e2e.sh` case `branch-exists`.
4. The diff is large (thousands of lines): the review input stays under a fixed size, says it was truncated, and the
   review still runs. Pinned by `ReviewPresetTests.A_truncated_diff_input_still_resolves` (Task 1) and the skill's
   `diff-input` rule (Task 3).
5. The review run itself fails (runtime error, budget reached): the branch with the change is kept and the report names
   the failed step and its `error.action`. Pinned by `ReviewPresetTests.A_failed_review_returns_its_error_with_an_action`
   (Task 1) and the skill's report rules (Task 3).

---

### Task 1: The `review` preset

**Files:**
- Create: `presets/review.yaml`
- Create: `prompts/preset/review.md`
- Create: `tests/Chargehand.Tests/ReviewPresetTests.cs`
- Modify: `evals/cells.json` (one synthetic review cell; read the file's existing cells for the shape)

**Interfaces:**
- Consumes: `Preset.Load(dir, name)`, `ContractSchemas.Preset`, `Runs.Orchestrator`, `ScriptedRuntime`,
  `RunRequest`/`RequestContext`/`CallerInput` (all existing).
- Produces: preset name `review` (node kind `worker`, `allowed_actions: [answer]`); caller-input contract the skill
  relies on: input ids `goal` (kind `goal`), `diff` (kind `diff`), `tests` (kind `test-output`); findings are `claims`
  citing `input` evidence with locator `diff` or `file` evidence at the reviewed commit.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Chargehand.Tests/ReviewPresetTests.cs
using Chargehand.Config;
using Chargehand.Contracts;
using Chargehand.RunLog;

namespace Chargehand.Tests;

/// <summary>Goal 0.5: the read-only preset the change command's review step runs under.</summary>
public class ReviewPresetTests
{
    private const string FindingReply = """
        ```json
        {"status":"completed","summary":"One finding.",
         "claims":[{"text":"The new branch in greet() never returns a value.","evidence":["e1","e2"],"confidence":0.8}],
         "evidence":[{"id":"e1","kind":"input","locator":"diff"},{"id":"e2","kind":"file","locator":"README.md:1"}],
         "artifacts":[],"open_questions":[],"confidence":0.8}
        ```
        """;

    private static RunRequest Review(RepositoryRef repo, string diff) => new("request/v1",
        "Review the change against its goal.", new RequestContext(false, "review", Repository: repo),
        [new CallerInput("goal", "goal", "Make the README greet."), new CallerInput("diff", "diff", diff),
         new CallerInput("tests", "test-output", "exit 0")], []);

    [Fact]
    public void Is_read_only_and_answers_only()
    {
        var preset = Preset.Load(Repo.Path("presets"), "review");
        Assert.Equal(["answer"], preset.AllowedActions);
        var rules = preset.NodeKinds["worker"].Permissions.Select(p => $"{p.Action} {p.Resource} {p.Effect}").ToList();
        Assert.Contains("edit * deny", rules);
        Assert.Contains("shell * deny", rules);
    }

    [Fact]
    public async Task Findings_cite_the_diff_input_and_resolve()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var r = await Runs.Orchestrator(new ScriptedRuntime(FindingReply), root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Review(repo, "--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-hello\n+hello world\n"), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Single(r.Claims);
        Assert.Contains(r.Evidence, e => (e.Kind, e.Locator) == (EvidenceKind.Input, "diff"));
        Assert.DoesNotContain(r.OpenQuestions, q => q.StartsWith("Unverified:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_truncated_diff_input_still_resolves()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var diff = string.Concat(Enumerable.Repeat("+x\n", 20_000)) + "\n[diff truncated at 60000 characters]\n";
        var r = await Runs.Orchestrator(new ScriptedRuntime(FindingReply), root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Review(repo, diff), CancellationToken.None);

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.Contains(r.Evidence, e => (e.Kind, e.Locator) == (EvidenceKind.Input, "diff"));
    }

    [Fact]
    public async Task A_failed_review_returns_its_error_with_an_action()
    {
        using var root = new TempDir();
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(FindingReply);
        runtime.CreateFailures.Enqueue(new ChargehandException(ErrorCode.RuntimeUnavailable, "down", "Start it."));
        var r = await Runs.Orchestrator(runtime, root.Path, new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")))
            .RunAsync(Review(repo, "+x\n"), CancellationToken.None);

        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.RuntimeUnavailable, r.Error?.Code);
        Assert.False(string.IsNullOrEmpty(r.Error!.Action));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter ReviewPresetTests`
Expected: FAIL — `unknown preset 'review'` (InvalidRequest) in all four.

- [ ] **Step 3: Write the preset**

```yaml
# presets/review.yaml
# Goal 0.5 (spec docs/specs/2026-09-29-change-command-design.md): reviews a change the caller made. The caller sends
# the goal, the diff and the test output as inputs; findings are claims citing the diff input or files at the commit.
# Read-only like every preset (ADR 0006).
schema: preset/v1
name: review
version: 0.1.0
allowed_actions: [answer]
node_kinds:
  worker:
    model: provider/worker-model
    opencode_agent: build
    permissions:
      - { action: "*", resource: "*", effect: allow }
      - { action: edit, resource: "*", effect: deny }
      - { action: read, resource: "*.env", effect: deny }
      - { action: read, resource: "*.env.*", effect: deny }
      - { action: read, resource: "*.env.example", effect: allow }
      - { action: shell, resource: "*", effect: deny }
      - { action: webfetch, resource: "*", effect: deny }
      - { action: external_directory, resource: "*", effect: deny }
      - { action: question, resource: "*", effect: deny }
      - { action: subagent, resource: "*", effect: deny }
    budget: { max_input_tokens: 400000, max_usd: 1.00 }
    compaction: { auto: true, keep_tokens: 15000, buffer: 120000 }
critic: { enabled: false }
approval: {}
```

```markdown
---
version: 0.1.0
---
Preset "review": read-only. Edits, shell commands, web fetches and subagents are unavailable; do not ask for them. You review a change someone else made. The inputs hold the goal (id "goal"), the diff against the base commit (id "diff", possibly truncated; say so if it matters) and the test output (id "tests"). Read the changed files at this commit with the read, grep and glob tools. Report each problem as one claim: what is wrong, where, and why it matters for the goal; cite the diff input and the file lines. Report missing tests, broken behaviour and goal mismatches; skip style that tooling would catch. If nothing is wrong, return no claims and say so in the summary.
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter "ReviewPresetTests|ConfigFileTests"`
Expected: PASS (the shipped-preset theories now include `review.yaml`).

- [ ] **Step 5: Add the synthetic eval cell and run the full check**

Add one cell to `evals/cells.json` for preset `review`, copying the shape of an existing cell, with a synthetic
goal/diff against this repository. Run: `scripts/check.sh` — expected last line `Passed!  - Failed:     0`.

- [ ] **Step 6: Commit**

```bash
git add presets/review.yaml prompts/preset/review.md tests/Chargehand.Tests/ReviewPresetTests.cs evals/cells.json
git commit -m "feat: add the read-only review preset for the change command"
```

---

### Task 2: Plugin scaffold, marketplace entry and version check

**Files:**
- Create: `.claude-plugin/marketplace.json`
- Create: `plugins/chargehand/.claude-plugin/plugin.json`
- Create: `plugins/chargehand/.mcp.json`
- Create: `tests/Chargehand.Tests/PluginManifestTests.cs`
- Modify: `.github/workflows/ci.yml` (new `plugin` job)
- Modify: `RELEASING.md` or the release section of `CONTRIBUTING.md` if one lists files to bump (check `git grep -n "Directory.Build.props" -- '*.md'`)

**Interfaces:**
- Consumes: `Directory.Build.props` `<Version>`; package id `Chargehand` (`src/Chargehand.Cli/Chargehand.Cli.csproj`).
- Produces: plugin name `chargehand`; MCP server name `chargehand` exposing tool `orchestrate`; marketplace name
  `chargehand`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Chargehand.Tests/PluginManifestTests.cs
using System.Text.Json;
using System.Xml.Linq;

namespace Chargehand.Tests;

/// <summary>Goal 0.5: the plugin carries the one version (Directory.Build.props) and starts the packed tool.</summary>
public class PluginManifestTests
{
    private static readonly string Version = XDocument.Load(Repo.Path("Directory.Build.props")).Descendants("Version").Single().Value;

    private static JsonElement Json(params string[] path) => JsonDocument.Parse(File.ReadAllText(Repo.Path(path))).RootElement;

    [Fact]
    public void Plugin_version_is_the_repository_version()
    {
        var plugin = Json("plugins", "chargehand", ".claude-plugin", "plugin.json");
        Assert.Equal("chargehand", plugin.GetProperty("name").GetString());
        Assert.Equal(Version, plugin.GetProperty("version").GetString());
    }

    [Fact]
    public void Mcp_entry_runs_the_packed_tool_at_the_repository_version()
    {
        var server = Json("plugins", "chargehand", ".mcp.json").GetProperty("mcpServers").GetProperty("chargehand");
        var args = server.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToList();
        var packageId = XDocument.Load(Repo.Path("src", "Chargehand.Cli", "Chargehand.Cli.csproj")).Descendants("PackageId").Single().Value;
        Assert.Equal("dotnet", server.GetProperty("command").GetString());
        Assert.Equal(["dnx", $"{packageId}@{Version}", "--yes", "--", "mcp"], args);
    }

    [Fact]
    public void Marketplace_lists_the_plugin_directory()
    {
        var entry = Json(".claude-plugin", "marketplace.json").GetProperty("plugins")[0];
        Assert.Equal("chargehand", entry.GetProperty("name").GetString());
        Assert.Equal("./plugins/chargehand", entry.GetProperty("source").GetString());
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter PluginManifestTests`
Expected: FAIL — `FileNotFoundException` for `plugin.json`.

- [ ] **Step 3: Create the three files** (use the current `<Version>` from `Directory.Build.props`, `0.3.0` today)

```json
// .claude-plugin/marketplace.json
{
  "name": "chargehand",
  "owner": { "name": "Egoushka" },
  "plugins": [
    {
      "name": "chargehand",
      "source": "./plugins/chargehand",
      "description": "One prompt to a reviewed change: chargehand researches and reviews with citations checked against a pinned commit, Claude Code writes the code."
    }
  ]
}
```

```json
// plugins/chargehand/.claude-plugin/plugin.json
{
  "name": "chargehand",
  "displayName": "chargehand",
  "version": "0.3.0",
  "description": "The change command: one prompt to a reviewed change on a local branch.",
  "author": { "name": "Egoushka" },
  "repository": "https://github.com/Egoushka/chargehand",
  "license": "Apache-2.0",
  "keywords": ["review", "citations", "mcp"]
}
```

```json
// plugins/chargehand/.mcp.json
{
  "mcpServers": {
    "chargehand": {
      "command": "dotnet",
      "args": ["dnx", "Chargehand@0.3.0", "--yes", "--", "mcp"]
    }
  }
}
```

- [ ] **Step 4: Run the test to verify it passes, then validate the plugin**

Run: `dotnet test --filter PluginManifestTests` — expected PASS.
Run: `npx -y @anthropic-ai/claude-code@2.1.283 plugin validate --strict ./plugins/chargehand` and
`npx -y @anthropic-ai/claude-code@2.1.283 plugin validate --strict .` (the marketplace) — expected `Validation passed`.
If the validator names a missing required field, add it and rerun.

- [ ] **Step 5: Add the CI job** (pin the Node version and the Claude Code version; no secrets)

```yaml
  plugin:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - run: npx -y @anthropic-ai/claude-code@2.1.283 plugin validate --strict ./plugins/chargehand
      - run: npx -y @anthropic-ai/claude-code@2.1.283 plugin validate --strict .
```

Add it under `jobs:` in `.github/workflows/ci.yml` next to `package`. If a release doc lists files to bump with the
version, add the two plugin files to it.

- [ ] **Step 6: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add .claude-plugin plugins/chargehand tests/Chargehand.Tests/PluginManifestTests.cs .github/workflows/ci.yml
git commit -m "feat: add the chargehand Claude Code plugin and its marketplace entry"
```

---

### Task 3: The `change` skill

**Files:**
- Create: `plugins/chargehand/skills/change/SKILL.md`
- Create: `plugins/chargehand/skills/change/report-template.md`
- Create: `tests/Chargehand.Tests/ChangeSkillTests.cs`

**Interfaces:**
- Consumes: MCP tool `orchestrate` (input `request/v1`, output `result/v1`) from the plugin's `chargehand` server or any
  configured server whose name contains `chargehand`; preset `review` and its input ids `goal`, `diff`, `tests`
  (Task 1).
- Produces: `/chargehand:change <goal> [--budget <usd>]`; branch `change/<slug>`; report
  `.chargehand/reports/<slug>.md`.

- [ ] **Step 1: Write the failing test** (it pins the contract the e2e script and users rely on)

```csharp
// tests/Chargehand.Tests/ChangeSkillTests.cs
namespace Chargehand.Tests;

/// <summary>Goal 0.5: the change skill keeps the names and limits the spec fixes.</summary>
public class ChangeSkillTests
{
    private static readonly string Skill = File.ReadAllText(Repo.Path("plugins", "chargehand", "skills", "change", "SKILL.md"));

    [Fact]
    public void Front_matter_names_the_skill_and_takes_a_goal()
    {
        Assert.StartsWith("---\nname: change\n", Skill.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("argument-hint:", Skill, StringComparison.Ordinal);
        Assert.Contains("$ARGUMENTS", Skill, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("change/<slug>")]
    [InlineData(".chargehand/reports/<slug>.md")]
    [InlineData("docs: add /change report")]
    [InlineData("preset \"review\"")]
    [InlineData("at most 2 fix rounds")]
    [InlineData("60000 characters")]
    public void Keeps_the_spec_names_and_limits(string text) => Assert.Contains(text, Skill, StringComparison.Ordinal);

    [Fact]
    public void Report_template_has_every_section()
    {
        var template = File.ReadAllText(Repo.Path("plugins", "chargehand", "skills", "change", "report-template.md"));
        foreach (var h in new[] { "## Goal", "## Research", "## Change", "## Tests", "## Review rounds", "## Open items", "## Runs" })
            Assert.Contains(h, template, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test --filter ChangeSkillTests` — expected FAIL, `FileNotFoundException`.

- [ ] **Step 3: Write the skill**

````markdown
---
name: change
description: One prompt to a reviewed change. chargehand researches the goal with citations checked against the current commit, you write the change on a new local branch, chargehand reviews it, you fix what it finds (at most 2 fix rounds), and a report is committed. Use when the user runs /chargehand:change with a goal or a GitHub issue reference.
argument-hint: <goal or #issue> [--budget <usd>]
---

Goal from the user: $ARGUMENTS

Follow these steps in order. Stop at the first step that fails, tell the user why and what fixes it.

## 1. Preflight

- Run `git rev-parse --is-inside-work-tree` and `git status --porcelain`. Outside a git work tree, or with any
  uncommitted change, stop: "Commit or stash your changes first, then run the command again." Create nothing.
- Find the `orchestrate` tool of an MCP server whose name contains `chargehand` (the plugin's own server comes first).
  If none is available, stop: "chargehand's MCP server is not running. It needs the .NET 10 SDK; check `/mcp` for its
  error." Create nothing.
- Parse `--budget <usd>` out of the arguments if present; the rest is the goal. If the goal is a GitHub issue
  reference (`#12`, or an issues URL) and `gh` or a GitHub MCP server is available, read the issue and use its title
  and body as the goal; otherwise use the text as given.
- Record the base commit: `git rev-parse HEAD`.

## 2. Research

Call `orchestrate` with `request/v1`: `text` = the goal, `context.repository` = { path: the repository root, commit:
the base commit }, `context.interactive` = true, and `context.budget_usd` when `--budget` was given.

- `needs_input` or questions: ask the user all of them in one message, then call again with the answers added to the
  text.
- `denied`: stop with the reason. Create nothing.
- `failed`: stop with `error.message` and `error.action`. Create nothing.
- `completed`: keep the claims and their citations; they are your map of the code. Claims listed under open questions
  are unverified; do not rely on them without reading the file yourself.

## 3. Branch

Make a slug: lower-case, words from the goal joined by `-`, only `a-z0-9-`, at most 40 characters. Create
`change/<slug>` from the base commit with `git switch -c`. If that branch exists, use `change/<slug>-2`, then `-3`,
and so on.

## 4. Write

Make the change the goal asks for, guided by the research claims. Keep it to what the goal needs.

## 5. Test

Find the repository's test command: CLAUDE.md or AGENTS.md first, then the README, then a standard build file
(`package.json` scripts.test, `*.sln`/`*.slnx` → `dotnet test`, `pyproject.toml` → `pytest`, `Cargo.toml` →
`cargo test`, `go.mod` → `go test ./...`). Run it if found. Keep the exit code and the last 200 lines of output. If
none is found, the test result is "no test command found".

## 6. Commit

One commit with a Conventional Commit message that describes the change.

## 7. Review

Diff input: `git diff <base>..HEAD`. If it is longer than 60000 characters, keep the first 60000 characters and
append the line `[diff truncated at 60000 characters]`.

Call `orchestrate` with `request/v1`: `text` = "Review the change against its goal.", `context.preset` = "review", so
preset "review" runs, `context.repository` = { path, commit: HEAD }, `context.interactive` = false, the budget as in
step 2, and `inputs`:
`[{ "id": "goal", "kind": "goal", "text": <goal> }, { "id": "diff", "kind": "diff", "text": <diff input> },
{ "id": "tests", "kind": "test-output", "text": <exit code and output> }]`.

Each claim in the result is a finding. `failed` or `denied`: keep the branch, go to step 9 and record the step,
`error.message` and `error.action`.

## 8. Fix loop

If there are findings, fix the ones that hold (read the cited lines first; a finding you judge wrong goes to the report
with your reason), rerun the tests, commit with `fix: address review`, and review again as in step 7. Do at most 2 fix
rounds, 3 reviews in all. Findings left after the last review are open items.

## 9. Report

Write `.chargehand/reports/<slug>.md` from `report-template.md` in this skill's directory
(`${CLAUDE_PLUGIN_ROOT}/skills/change/report-template.md`). Commit it on its own: `docs: add /change report`.
If chargehand stopped with `cost_cap_reached`, say which step and what is unfinished.

## 10. Hand back

Tell the user: the branch name, one paragraph on what changed, the tests result, the open items, and that the report
commit can be dropped before merging (`git reset --hard HEAD~1` while it is the last commit).
````

```markdown
# /change report: <goal, one line>

Branch `change/<slug>` from `<base commit>`.

## Goal

<the goal as given, and the issue link if it came from one>

## Research

<chargehand's summary, then each claim used, with its citation (file:lines at the base commit)>

## Change

<what changed and why, one short paragraph per file group>

## Tests

<command, exit code, and the failing test names if any; or "no test command found">

## Review rounds

<for each review: the findings, each marked fixed, rejected (with the reason) or open>

## Open items

<findings still open, unverified claims that mattered, and any step that failed with its error and action>

## Runs

<chargehand run ids, and the cost each result reported>
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter ChangeSkillTests` — expected PASS.
Run: `npx -y @anthropic-ai/claude-code@2.1.283 plugin validate --strict ./plugins/chargehand` — expected
`Validation passed`.

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add plugins/chargehand/skills tests/Chargehand.Tests/ChangeSkillTests.cs
git commit -m "feat: add the change skill, one prompt to a reviewed change"
```

---

### Task 4: End-to-end script

**Files:**
- Create: `scripts/change-e2e.sh`
- Create: `scripts/change-e2e.mcp.json` (points the `chargehand` server at `dotnet run --project src/Chargehand.Cli -- mcp` in this checkout, so the check runs before the package is on nuget.org)
- Create: `.github/workflows/change-e2e.yml`

**Interfaces:**
- Consumes: the plugin (Tasks 2–3), `claude -p` with `--plugin-dir`, `--mcp-config`, `--strict-mcp-config`.
- Produces: exit code 0 when all cases pass; one line per case on stdout.

- [ ] **Step 1: Write the script with its four cases** (each case builds a fresh sample repository under `$TMPDIR`)

```bash
#!/usr/bin/env bash
# End-to-end check of /chargehand:change (goal 0.5). Needs a signed-in `claude` and the .NET SDK; calls a real model,
# so it runs by hand or on the maintainer's runner (.github/workflows/change-e2e.yml), never on every push.
set -euo pipefail
here=$(cd "$(dirname "$0")/.." && pwd)
mcp="$here/scripts/change-e2e.mcp.json"
fail=0

sample() {
  local d; d=$(mktemp -d "${TMPDIR:-/tmp}/change-e2e.XXXXXX")
  git -C "$d" init -q
  printf 'def greet(name):\n    return "hello " + name\n' > "$d/greet.py"
  printf 'from greet import greet\n\ndef test_greet():\n    assert greet("a") == "hello a"\n' > "$d/test_greet.py"
  printf '# sample\nTests: `python3 -m pytest -q`\n' > "$d/CLAUDE.md"
  git -C "$d" add . && git -C "$d" -c user.name=e2e -c user.email=e2e@example.com commit -q -m init
  echo "$d"
}

run() { # dir, mcp-config, goal
  (cd "$1" && claude -p "/chargehand:change $3" --plugin-dir "$here/plugins/chargehand" \
     --mcp-config "$2" --strict-mcp-config --permission-mode acceptEdits \
     --allowedTools "Bash(git:*),Bash(python3:*),mcp__chargehand__orchestrate" >/dev/null 2>&1) || true
}

check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=1; fi; }

d=$(sample); run "$d" "$mcp" "make greet() capitalise the name"
b=$(git -C "$d" branch --list 'change/*' --format '%(refname:short)' | head -1)
check happy "[ -n \"$b\" ] && [ \$(git -C \"$d\" rev-list --count master..\"$b\" 2>/dev/null || git -C \"$d\" rev-list --count main..\"$b\") -ge 2 ] && git -C \"$d\" show \"$b\":.chargehand/reports/\${b#change/}.md >/dev/null"

d=$(sample); echo dirty >> "$d/greet.py"; run "$d" "$mcp" "make greet() capitalise the name"
check dirty "[ -z \"\$(git -C \"$d\" branch --list 'change/*')\" ]"

d=$(sample); echo '{"mcpServers":{}}' > "$d.none.json"; run "$d" "$d.none.json" "make greet() capitalise the name"
check no-server "[ -z \"\$(git -C \"$d\" branch --list 'change/*')\" ]"

d=$(sample); git -C "$d" branch change/make-greet-capitalise-the-name; run "$d" "$mcp" "make greet capitalise the name"
check branch-exists "[ -n \"\$(git -C \"$d\" branch --list 'change/make-greet-capitalise-the-name-2')\" ]"

exit $fail
```

```json
// scripts/change-e2e.mcp.json — the checkout's server under the plugin's server name
{ "mcpServers": { "chargehand": { "command": "dotnet", "args": ["run", "--project", "src/Chargehand.Cli", "--", "mcp"] } } }
```

Note: `claude -p` runs with the sample repository as its working directory, so the `--project` path must be absolute.
Make the script write a temporary copy of `change-e2e.mcp.json` with `$here/src/Chargehand.Cli` substituted, and pass
that copy instead of `$mcp`.

- [ ] **Step 2: Run it locally and fix until all four cases print `ok`**

Run: `chmod +x scripts/change-e2e.sh && scripts/change-e2e.sh`
Expected: `ok happy`, `ok dirty`, `ok no-server`, `ok branch-exists`, exit 0. Uses the maintainer's Claude Code
subscription; keep it to one full run while iterating on a single case with a commented-out rest.

- [ ] **Step 3: Record whether plain `/change` resolves** (spec open item)

Run once in the sample repository: `claude -p "/change make greet() capitalise the name" --plugin-dir … --mcp-config …`.
Write the result (works / "unknown command") as one line under "Open items" in the spec, dated.

- [ ] **Step 4: Add the workflow** (runner label and trigger like `prompt-ci.yml`; manual or label only)

```yaml
name: change-e2e
# Goal 0.5: runs scripts/change-e2e.sh on the maintainer's self-hosted runner, which has a signed-in Claude Code.
# Never runs on forks: workflow_dispatch, or the "change-e2e" label on a same-repository pull request.
on:
  workflow_dispatch:
  pull_request:
    types: [labeled]
permissions:
  contents: read
jobs:
  e2e:
    if: github.event_name == 'workflow_dispatch' || (github.event.label.name == 'change-e2e' && github.event.pull_request.head.repo.full_name == github.repository)
    runs-on: [self-hosted, chargehand-eval]
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
      - run: scripts/change-e2e.sh
```

- [ ] **Step 5: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add scripts/change-e2e.sh scripts/change-e2e.mcp.json .github/workflows/change-e2e.yml docs/specs/2026-09-29-change-command-design.md
git commit -m "test: add the change command's end-to-end check"
```

---

### Task 5: chargehand reviews its own pull requests

**Files:**
- Create: `.github/workflows/self-review.yml`
- Create: `scripts/self-review.sh`

**Interfaces:**
- Consumes: preset `review` and input ids (Task 1); the runner's local chargehand (`dotnet run --project src/Chargehand.Cli -- run`), which reads `request/v1` on stdin and prints `result/v1`.
- Produces: one pull-request comment per run listing the findings with their citations.

- [ ] **Step 1: Write the script**

```bash
#!/usr/bin/env bash
# Reviews a pull request of this repository with the review preset; prints the result/v1 JSON. Runs on the
# maintainer's self-hosted runner (it needs a worker runtime). Usage: scripts/self-review.sh <base-sha> <head-sha>
set -euo pipefail
base=${1:?base sha}; head=${2:?head sha}
diff=$(git diff "$base..$head" | head -c 60000)
[ "$(git diff "$base..$head" | wc -c)" -gt 60000 ] && diff="$diff"$'\n[diff truncated at 60000 characters]'
jq -n --arg repo "$PWD" --arg commit "$head" --arg diff "$diff" --arg goal "$(git log --format=%B -n1 "$head")" '{
  contract_version: "request/v1", text: "Review the change against its goal.",
  context: { interactive: false, preset: "review", repository: { path: $repo, commit: $commit } },
  inputs: [ {id:"goal",kind:"goal",text:$goal}, {id:"diff",kind:"diff",text:$diff}, {id:"tests",kind:"test-output",text:"CI runs the tests separately."} ] }' \
  | dotnet run --project src/Chargehand.Cli -c Release -- run
```

- [ ] **Step 2: Run it locally against the last merged pull request**

Run: `scripts/self-review.sh HEAD~1 HEAD | jq '.status, (.claims|length)'`
Expected: `"completed"` and a number. If the local profile blocks the repository, run from a checkout under a
`repository_roots` entry.

- [ ] **Step 3: Add the workflow** (same-repository pull requests only; the comment needs `pull-requests: write`)

```yaml
name: self-review
# Goal 0.5: chargehand reviews this repository's pull requests with the review preset, on the maintainer's
# self-hosted runner. Same-repository pull requests only: a fork's code never runs there.
on:
  pull_request:
    types: [opened, synchronize, reopened]
permissions:
  contents: read
  pull-requests: write
jobs:
  review:
    if: github.event.pull_request.head.repo.full_name == github.repository
    runs-on: [self-hosted, chargehand-eval]
    continue-on-error: true
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with: { fetch-depth: 0 }
      - id: review
        run: scripts/self-review.sh "${{ github.event.pull_request.base.sha }}" "${{ github.event.pull_request.head.sha }}" > "$RUNNER_TEMP/result.json"
      - env: { GH_TOKEN: "${{ github.token }}" }
        run: |
          jq -r '"### chargehand review\n\n" + .summary + "\n\n" + ([.claims[] | "- " + .text] | join("\n")) + "\n\nRun `" + .run_id + "`; citations checked against " + "${{ github.event.pull_request.head.sha }}" + "."' "$RUNNER_TEMP/result.json" > "$RUNNER_TEMP/comment.md"
          gh pr comment "${{ github.event.pull_request.number }}" --body-file "$RUNNER_TEMP/comment.md"
```

Check the `result/v1` field names used by `jq` (`summary`, `claims[].text`, `run_id`) against
`schemas/result/v1/result.schema.json` before committing and correct them if they differ.

- [ ] **Step 4: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add .github/workflows/self-review.yml scripts/self-review.sh
git commit -m "ci: let chargehand review its own pull requests with the review preset"
```

---

### Task 6: Documentation

**Files:**
- Modify: `README.md` (a "Claude Code plugin" section after "HTTP and MCP"; `/change` in the Commands context)
- Modify: `ROADMAP.md` (0.5 line: `/ch <goal>` → `/chargehand:change <goal>`)

- [ ] **Step 1: README section** (existing style: short paragraphs, one table at most)

```markdown
## Claude Code plugin

`/chargehand:change <goal>` takes one prompt to a reviewed change on a local branch `change/<slug>`: chargehand
researches the goal with citations checked against the current commit, your session writes the change and runs the
tests, chargehand reviews the diff with the `review` preset, the session fixes what holds (at most 2 fix rounds), and a
report lands in `.chargehand/reports/<slug>.md` as its own commit. Nothing is pushed.

    /plugin marketplace add Egoushka/chargehand
    /plugin install chargehand@chargehand

The plugin starts chargehand through `dnx`, so it needs the .NET 10 SDK and a published `Chargehand` package. From a
checkout, point a `chargehand` MCP server at `dotnet run --project src/Chargehand.Cli -- mcp` instead
(`scripts/change-e2e.mcp.json` shows one).
```

- [ ] **Step 2: ROADMAP line**

Replace `` `/ch <goal>` in Claude Code takes one prompt to a reviewed change `` with
`` `/chargehand:change <goal>` in Claude Code takes one prompt to a reviewed change ``.

- [ ] **Step 3: Full check and commit**

Run: `scripts/check.sh` — expected `Passed!  - Failed:     0`.

```bash
git add README.md ROADMAP.md
git commit -m "docs: document the change command and its plugin"
```
