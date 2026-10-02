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

- ▶ **0.5** `/chargehand:change <goal>` in Claude Code takes one prompt to a reviewed change. Its code shipped in 0.4.0
  and there is no 0.5.0; what is open is its usage bar, which the first real driven batch (below) now also meets.
- ✅ **0.6** Runs use your MCP services and memory: any MCP memory server, several at once, retaining only claims whose
  citations resolved, and a preset can give workers read-only tools from an MCP service (released in 0.6.0).
- ✅ **0.7** Workers write branches that build and pass their tests in a sandbox: the `code` preset, the sandbox and the
  verifier (ADR 0035), released in 0.8.0 (there is no 0.7.0). Its usage bar, five real issues taken to a merged change,
  is met by the first real driven batch of five tasks.
- ▶ **Driven sessions** (ADR 0039, pulled forward from after 1.0): a list of tasks becomes parallel headless Claude Code
  sessions, one container each, and a draft pull request per task that chargehand checked, tested in a fresh container
  and pushed itself. Its parts are built and tested; open are the path from a request to them with its end-to-end
  script, the check that Claude Code runs on a substituted credential, deployment on the server, and the skill's
  measured adherence (at least 7 of 10 sessions follow its steps). It gets a minor version when it is wired; until
  then `driven.enabled` stays false in every real profile. [Guide](docs/guide/driven.md).

## Stage 4 — Answers you can prove · ✅ (0.8)

- ✅ **0.8** Every claim is checked for support against its cited text, and results are signed so anyone can verify them
  offline (released in 0.8.0).

## Stage 5 — People find it · (0.9–1.0)

- · **0.9** A public verification benchmark, a demo and listings.
- · **1.0** `result/v1` and the extension API are declared stable.

## Stage 6 — A client of your own · (after 1.0)

chargehand becomes the engine under a client the maintainer uses every day instead of Claude Code's own interface, and
can extend. The client replaces the interface, not the agent: it talks to Claude Code, OpenCode and others through the
agent client protocol. [Spec](docs/specs/2026-10-02-a-client-of-your-own-design.md).

- · **1.1** Sessions you can steer: a session resource (start, stream, message, approve, cancel) over an agent client
  protocol adapter, proved on one runtime. First a spike on what the protocol covers.
- · **1.2** A web client served by `chargehand serve`, usable from a phone: list, live transcript, cost, steering.
- · **1.3** What no vendor client has: every claim in the transcript shows its checked citation and support verdict, a
  session ends in a `result/v1`, memory recall in the client. A second runtime with no client change.
- · **1.4** Unattended pickup: work taken from the tracker by a session with no person in it, as a mode of the same
  session resource.

## After that

A native shell for the client if a browser tab proves not enough, more runtimes. Not planned: an own agent loop, model
gateway, memory store or plugin marketplace; multi-user accounts; a hosted service.
