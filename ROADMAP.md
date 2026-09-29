# Roadmap

Where chargehand is going, as stages of what is true when each is done. Before 1.0 a finished goal bumps the minor
version; the [changelog](CHANGELOG.md) says what shipped. Legend: ✅ done · ▶ next · · later.

## Stage 1 — Answers questions about code, with checked citations · ✅ (0.1–0.2)

Intake, one or more read-only workers on OpenCode or Claude Code, `result/v1` with every citation resolved against the
pinned commit, presets with token and USD budgets, CLI, HTTP and MCP, Prompt CI, cache and routing reports, a server
image.

## Stage 2 — Easy to install, trust and follow · ✅ (0.3–0.4)

- ✅ **0.3** Every change is tracked, released and checked: GitHub Releases with notes, `Chargehand.Contracts` on
  nuget.org, OpenSSF Scorecard, CodeQL, dependency review, stricter analyzers.
- ✅ **0.4** It runs with nothing configured: no profile file needed, the runtime picked by name or found on the machine,
  secrets from environment variables, extensions by category, the `Chargehand` tool package on nuget.org, and its
  MCP Registry listing (from 0.4.1: the 0.4.0 package's README could not match the registry's name).

## Stage 3 — One prompt, whole result · ▶ (0.5–0.7)

- ▶ **0.5** `/chargehand:change <goal>` in Claude Code takes one prompt to a reviewed change.
- · **0.6** Runs use your MCP services and memory: any MCP memory server, several at once.
- · **0.7** Workers write branches that build and pass their tests in a sandbox.

## Stage 4 — Answers you can prove · (0.8)

- · **0.8** Every claim is checked for support against its cited text, and results are signed so anyone can verify them
  offline.

## Stage 5 — People find it · (0.9–1.0)

- · **0.9** A public verification benchmark, a demo and listings.
- · **1.0** `result/v1` and the extension API are declared stable.

## After 1.0

Issues in and reviewed changes out without anyone watching, a chat client that approves and denies runs, more agent
runtimes through ACP. Not planned: an own agent loop, model gateway, memory store or plugin marketplace.
