---
title: "Overview"
description: "What chargehand is, how a run works, what it leaves out, and where it stands before 1.0."
order: 0
section: "Get started"
---

chargehand turns a request into a typed Task Spec, runs it on one or more coding-agent sessions (OpenCode or Claude Code), and returns a result contract with evidence for every claim. People call it from a CLI; programs call it over HTTP or MCP.

## The problem it solves

Coding agents answer in prose. A program that calls one needs something it can check: claims tied to files at a commit, diffs, session messages or caller inputs, plus a confidence and the exact prompt chain behind the answer.

chargehand keeps the control flow in code: the task graph, budgets, retries and parallelism. Each worker's context stays small, cached and disposable.

## How a run works

1. **Intake** reads a `request/v1` document and writes a Task Spec (`task-spec/v1`) with one action.
2. **The action** decides what happens next:
   - `answer` runs one worker session.
   - `split` runs 2 to 4 read-only subtasks as a task graph, at most 2 at a time. Later nodes fork the first node's session and read its prompt prefix from cache. The node results merge without a model call.
   - `deny` stops with status `denied`, a reason and an unblock condition.
   - `ask` stops with status `needs_input` and the questions.
   - `improve` stops with status `needs_input`, an improved request and its diff.

   When the preset does not allow the action intake chose, the run falls back to `answer`. A preset can also stop a run for approval above a risk or cost estimate; among the shipped presets only `strict` does.
3. **The evidence resolver** checks every citation before a result leaves its node. A `file` citation must name a path and a line range that exist at the pinned commit. A `commit` must exist, a `diff` range must fall inside the node's session diff, a `session_message` must belong to the node's session, a `url` must appear in the node's inputs or tool output, and an `input` must be an id the caller sent. The worker gets one repair turn; claims whose citations still fail move to `open_questions`.
4. **You get `result/v1`.** The run log records tokens, cache hits, cost and the prompt chain of every call.

A citation that resolves points at something that exists. chargehand does not yet read the cited lines against the claim: that support check is planned for 0.8 in [ROADMAP.md](../../ROADMAP.md). This guide says "citations checked" for that reason.

Sources: [README](../../README.md#how-a-run-works), [ADR 0009](../adr/0009-result-contract.md), [ADR 0017](../adr/0017-split-runs-forked-siblings-and-merge.md), [GitEvidenceResolver.cs](../../src/Chargehand/Verification/GitEvidenceResolver.cs).

## What it does not do

- It has no agent loop of its own. The workers are OpenCode or Claude Code sessions.
- It makes no calls to model providers. The worker runtime makes them.
- It does not add parallelism for its own sake. A split runs when intake chooses it and the preset allows it; `default`, `review` and `draft` allow no split.
- Its workers do not write. Every shipped preset is read-only, and a split that includes a writing subtask runs as one answer node.

[ROADMAP.md](../../ROADMAP.md) lists four things as not planned: an agent loop of its own, a model gateway, a memory store and a plugin marketplace.

## Current status

- **Before 1.0.** The latest release is v0.8.3. Goal 0.4, "it runs with nothing configured", is done: v0.4.0 shipped it and v0.4.1 added its MCP Registry listing. Goal 0.5 in the roadmap, one prompt to a reviewed change, is still open: its code (the plugin, the command and the `review` preset) shipped in 0.4.0, there is no 0.5.0, and what is left is the maintainer's own use of the command on real tasks. Goal 0.6, runs that use your MCP services and memory, is done and released in v0.6.0 ([Memory and services](memory-and-services.md)). Goal 0.7 (writing workers) and goal 0.8 (support checking and signed results) are released together in v0.8.0; there is no 0.7.0. The 0.7 exit bar, five real issues taken to a merged change, is not met yet.
- **Read-only workers, and one writer.** Every shipped preset but `code` denies edits. Since 0.8.0, the `code` preset writes a branch that passes its tests in a sandbox ([Writing a branch](writing.md)).
- **On nuget.org and in the MCP Registry.** The `Chargehand` tool package and `Chargehand.Contracts` are published, so `dnx Chargehand@<version> --yes -- mcp` runs chargehand without a clone ([the MCP page](mcp.md#from-the-package)). The registry lists `io.github.Egoushka/chargehand` from 0.4.1, and the release workflow lists each new version.
- **Main and the release.** This guide describes the `main` branch, which is v0.8.3 plus the changes under Unreleased in the [changelog](../../CHANGELOG.md#unreleased). A change that lands on `main` after a release appears there, and pages mark it "on main, not yet released".

## Where to go next

- [What it does, and how we know](capabilities.md): each capability with its status and the evidence behind it.
- [Quickstart](quickstart.md): one question from a checkout, end to end.
- [Use it from an MCP client](mcp.md): the `orchestrate` tool over stdio or HTTP.
- [Run the HTTP server](server.md): `chargehand serve`, its routes and the container image.
- [Writing a branch](writing.md): the `code` preset, its sandbox, what it verifies and what it does not.
- [Driven writing sessions](driven.md): a list of tasks as parallel headless sessions in containers, ending in draft pull requests; what is built and what is not wired yet.
- [Support checking and signed results](support-and-signing.md): each claim checked against the text it cites, and results signed and verified offline.
- [Memory and services](memory-and-services.md): recall from MCP memory servers, retain only claims whose citations resolved, and give a preset's workers read-only MCP tools.
- [The change command](change.md): `/chargehand:change` in Claude Code.
- [Prompt CI](prompt-ci.md): how prompt and preset changes get gated.
- [Reference](reference.md): commands, presets, contracts, profile fields, environment variables.
- [Architecture decisions](decisions.md): every ADR and what it decides.
