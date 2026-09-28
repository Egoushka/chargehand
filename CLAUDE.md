# CLAUDE.md — chargehand

.NET 10 orchestrator that drives OpenCode sessions over HTTP. Public repository.

## Commands

```bash
scripts/check.sh                      # the one check before a push: format, build (warnings as errors), tests
dotnet build                          # whole solution (Chargehand.slnx)
dotnet test                           # schema tests, contract test against docs/opencode-openapi.json
dotnet format --verify-no-changes     # lint / format check (CI runs this)
scripts/gen-opencode-api.py <url>     # regenerate docs/opencode-api.md + spec (needs OPENCODE_SERVER_PASSWORD)
gitleaks git --redact -v              # secret scan over history
scripts/opencode-serve.sh <bin> <cfg> [port]   # start the orchestrator's own OpenCode server
dotnet run --project src/Chargehand.Cli -- run|serve|show|cache|reconcile|routes|score|eval|prompts sync   # CLI (profiles/local.json)
scripts/prompt-ci.sh <pr>             # Prompt CI on the owner's machine (profiles/local.eval.json, eval OpenCode server)
```

Hooks: `git config core.hooksPath .githooks` (denylist + gitleaks on pre-commit, Conventional
Commits on commit-msg). Never `--no-verify`.

## Public vs private — the rule for every file

The repo is public. Never commit: IP addresses, hostnames, key aliases, employer or project
names, budgets, absolute home paths, session or message ids, API keys, prompts from real runs.

- Code is environment-agnostic: an OpenCode server URL and `provider/model` ids from config.
- Personal setup lives in gitignored `profiles/local.*`; `profiles/example.*` uses placeholders.
  Secrets are referenced by secret-store item name, never stored in a profile.
- Treat every OpenCode `/api/model*`, `/api/provider*`, `/api/config*` body as secret: never log,
  record or commit it; redact `apiKey` before writing any fixture.
- Fixtures recorded from a real server get ids, paths and prompts scrubbed before commit.
- Eval items are real tasks: they live in the orchestrator's Langfuse datasets and the gitignored `runs/`. `evals/`
  holds only the cell definitions and synthetic examples against this repository.

## Conventions

- Decisions: `docs/adr/NNNN-title.md` from `docs/adr/template.md`.
- Schemas: `schemas/<name>/v<major>/`; `$id` carries the major.
- One version source: `Directory.Build.props`. Contract package versions by schema major.
- Commits: Conventional Commits.

## Working rules

- Done means `scripts/check.sh` exits 0; its last test line reads `Passed!  - Failed:     0`. A bug fix starts with a
  failing test.
- A mistake an agent makes twice becomes a line in this file.
- A PR title may end with the maintainer's tracker key (`(CHARGEHAND-12)`); tracker URLs never appear in public text.
- `.claude/settings.json` asks before edits under `schemas/<name>/v<N>/`: published majors take additive changes only.
- Public text says "citations checked", not "claims verified", until the support check ships (ROADMAP.md, 0.8).
