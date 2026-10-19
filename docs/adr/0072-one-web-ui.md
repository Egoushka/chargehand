# 0072. One web UI: the page at /ui/ is retired and redirects to the console

- Status: accepted. Fulfils the condition of 0056 ("`/ui/` stays until the console reaches parity"); supersedes 0054 except for its key-handling rules, which the console keeps.
- Date: 2026-10-19

## Context

Two web UIs have been served since 0056: the plain-ES-module page at `/ui/` (0054) and the Vite and React console at `/console/`. Both gate on `http.ui` and both ask for a key. The owner reached the sign-in prompt of `/ui/` after the console had stopped asking (0071 signs in by Cloudflare Access identity, in the console only), and asked for one UI. 0056 names the condition for removal: the console must do everything `/ui/` does.

Parity was checked screen by screen against the 923 lines of `Ui/` and 0056's list (runs list with filters, a run with live events, claims, tasks and artifacts, cancel, start a run or batch, learning, spend against caps, halt and resume):

| `/ui/` did | The console |
|---|---|
| Runs list, filters on state, kind, repository, actor | Runs: the same filters, plus search, preset, date, saved views |
| Hide a batch's research and review calls ("Show session calls") | **Missing.** Every run was listed |
| Live events and the task table of a run | Batch Events and Tasks tabs; Research shows live events |
| Claims with linked citations, web address kept in `localStorage` | Research: same rule, same storage |
| Tasks of a batch, artifacts (`changes` as diff, JSON, references) | Batch Tasks, Artifacts, Task drawer |
| Artifacts of a run that is not a batch | **Missing.** Research showed no artifacts |
| Cancel a run (on its page) | Runs list and Batch had it; **a single run's page did not** |
| Start a run: goal, preset, repository, commit | **Missing.** Start was a batch composer only |
| Start a batch | Start a batch (richer) |
| Learning | Learning, Analytics |
| Spend against caps; halt and resume (owner) | Overview, top bar, System |

## Decision

- **Fill the three gaps in the console, then remove `/ui/`.** Start gets a **Single run** mode beside the batch composer (goal, preset, repository at its head; the same `POST /v1/runs`). Runs gets a **Session calls** filter, hidden by default as in `/ui/`. A run that is not a batch shows its artifacts and can be cancelled from its own page.
- **`http.ui` serves only the console.** `WebUi`, `src/Chargehand.Server/Ui/`, its embedded resources and its tests are deleted. The tests that were about `GET /v1/accounts/me` and the run-list filters (routes the console uses) stay, in `AccountsMeTests`.
- **`/ui` and anything under it answer `302` to `/console/`**, with `http.ui` on and without a key, like the console's own files; the old page's address in a bookmark keeps working. Temporary, not permanent, so a browser does not keep the redirect if the path is ever reused. With `http.ui` off it is a `401` without a key and a `404` with one, as before. `/uix` is not under `/ui` and still needs a key.
- **What 0054 keeps.** Its rules for the key (`sessionStorage`, bearer header, no cookie), the policy and the Host check carry over unchanged: 0056 adopted them for the console. Only its "no build step, no third-party code" and the page itself go.

## Consequences

- One UI, one set of tests, one place a sign-in change lands. The server has one fewer embedded resource group and no hand-written JavaScript to keep from rotting without a compiler.
- A bookmark such as `/ui/#/runs/<id>` lands on the console's Overview, not on that run: the console's routes are paths, not hash fragments, and the fragment is not sent to the server.
- The `http.ui` setting keeps its name; it now means "the console". Renaming it would break every profile that sets it.
- The start line of `chargehand serve` ends `console /console/` instead of `UI /ui/, console /console/`.

## Reopen if

A deployment needs the key-only, no-Node page back (a build with no `npm`); the redirect should be permanent after a release or two; or a person asks for the hash-route redirect.
