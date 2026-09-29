# 0006. Presets

- Status: accepted; workers' MCP tools amended by 0034
- Date: 2026-09-26

## Context

Presets `cheap`, `thorough` and `strict` are versioned, schema-validated files compiled into an OpenCode agent
plus the permission ruleset passed at session creation, with budgets enforced by the orchestrator. No
`autonomous` preset until sandboxing is designed.

## Evidence (phase 2 spike)

- Per-session rulesets work: with the v0 default, `git log` ran, `touch` failed with `permission.rejected` and
  left no pending request.
- **Last matching rule wins.** `[allow git log*, deny *]` for `shell` removed the shell tool entirely.
- A trailing `<action> * deny` **removes the tool from the catalog** (the model reported no edit tool). Tool sets
  therefore differ per preset, which is fine because they are fixed per preset (ADR 0010).
- `ask` + `once` applied the edit; `ask` + `reject` made the model retry; `always` was never sent, and saved
  permissions stayed at zero.
- **A session ruleset overrides the agent's own rules.** With a leading `* * allow`, the `build` agent's
  ask-before-reading-`.env` rules no longer apply: a worker read `*.env.*` files without asking (found during the
  v0 benchmark). Presets must deny secret files themselves.
- **A rejected `ask` can abort the whole step** (2.0.16, v0 benchmark): the step ends with `aborted: Step
  interrupted`, no idle marker is written, and `wait` still returns. In the spike the model retried instead. Either
  way `reject` is a poor control; `deny` rules return a tool error and the worker continues.
- V2 compaction keys are `auto`, `keep.tokens`, `buffer` (v1's `preserve_recent_tokens` and `reserved` map onto
  them).

## Evidence (shell rule matching, 2.0.16 source, 2026-09-27)

- The shell tool parses the command with tree-sitter-bash and asserts `shell` once with one resource per `command`
  node, nested substitutions included: the node's source text, trimmed (with its redirections when it has any).
  `cd`-like commands are checked as `external_directory` instead. Any resource matching a `deny` denies the call.
- A rule's resource is a wildcard over that text: `*` is any run of characters (newlines included), `?` one
  character, a trailing ` *` also matches nothing. Nothing is unquoted or normalised first, so `--p""re` or
  `--pr\e` escape a deny on `--pre` while the shell still passes `--pre`. `git` also takes unique prefixes of long
  options (`--out`), and `rg` searches a file named on the command line even when `.gitignore` excludes it.
- Consequence: allowing `git grep*`, `git log*` or `git show*` let a worker run programs (`git grep -O`) and write
  files (`--output`); `rg *` let it read an ignored `*.env` by naming it. No deny list over source text closes
  these.

## Decision

- `preset/v1` (`schemas/preset/v1`); files in `presets/<name>.yaml`. v0 shipped `default`; phase 4 adds `cheap`,
  `thorough` and `strict`, all read-only until worktrees exist (ADR 0015). Their critic has no writing node to
  review yet.
- Compaction: session create takes no compaction settings (phase 4 spike), so `auto`, `keep_tokens` and `buffer`
  describe the server config; the orchestrator enforces `trigger_tokens` and `max_input_tokens` itself (ADR 0010).
- `approval` stops a run with `needs_input` above its risk or estimate threshold unless the request sets
  `context.approved`. Only `strict` sets thresholds: intake's estimate is uncalibrated (ADR 0005; phase 4 saw
  $0.35 estimated for a ~$0.01 run), so elsewhere it would stop runs at random.
- Rulesets are ordered, last match wins; write the broad rule first, then exceptions.
- Every preset denies reading `*.env` and `*.env.*` (allowing `*.env.example`).
- No preset gives workers the shell tool (a trailing `shell * deny`; tested): rules cannot constrain a command's
  arguments (evidence above). Workers search with OpenCode's `read`, `grep` and `glob` tools and lose `git`
  history until a sandbox exists.
- `grep` asserts its pattern, not the path it searches, and runs `rg --hidden -- <pattern> <path>`: a named path or
  an `include` glob reaches files `.gitignore` excludes (checked with rg 15.2), so no rule keeps it out of a secret
  file. The orchestrator therefore refuses a checkout holding any file, tracked or untracked, whose last matching
  `read` rule denies it, before a worker session starts. Workers run on checkouts without local secrets.
- Workers get no MCP tool. On OpenCode a leading `* * allow` reached the tools of any server registered at the location
  (ADR 0034, follow-up), so every session's rules end with `*_* * deny`, and the server chargehand starts ignores a
  checkout's own OpenCode configuration.
- Prefer `deny` over `ask`: the orchestrator's watcher rejects any remaining ask, which may end the node.
- The orchestrator answers pending permissions only with `once` or `reject`, never `always`.
- Defaults: `cheap` = small-model workers, no critic. `thorough` = large-model workers plus a critic from
  another model family on writing nodes only. `strict` = `thorough` plus approval above risk `low` or an
  estimate above $0.50. v0 `default` = edits, shell, web fetch, external directories, questions and subagents
  denied.

## Reopen if

A sandboxing ADR exists (then `autonomous`, and shell or `git` history for workers inside it), or phase 4 shows a
cheaper model meeting the bar in a cell.
