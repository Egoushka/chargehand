# 0054. A web UI served by the server, with the key in sessionStorage

- Status: accepted; its "no build step, no third-party code" is amended by 0056 for the operator console at `/console/`, which replaces this page when it reaches parity
- Date: 2026-10-13

## Context

Everything the server does is reachable over HTTP, but a person watching a batch uses `chargehand watch` or `curl`: there is no page that lists runs, shows a run's events as they arrive, links a claim to the line it cites, or shows what an account has spent against its caps. ADR 0050 gave a handful of people their own keys and caps, which makes such a page worth having.

Constraints. The repository is public and the build has no JavaScript toolchain (no npm, no bundler); a toolchain would add a build step and a supply chain to a server that today builds with `dotnet build`. The page holds a bearer key that opens runs and, for the owner, can halt the server and start batches that push branches. Run data is written by models and by repositories (claims, summaries, branch names, diffs), so it is untrusted text on a page that holds a key.

## Options

### Where the page comes from

1. **A separate static site** on another origin. Needs CORS on the server (it sends none, by design) and a second deployment.
2. **Files in the server project**, embedded as resources and served at `/ui/`. Same origin, no CORS, one artifact, and the release image needs no extra folder.

### Where the key lives in the browser

1. **A cookie** the server sets after sign-in. It would need server-side session state, CSRF protection for every `POST`, and the cookie goes out on every request including ones an attacker's page forges.
2. **`localStorage`**. Survives closing the browser, readable by every script of the origin, shared by every tab.
3. **`sessionStorage`**. Lives with the tab, gone when it closes, never sent unless the page sends it.
4. **A variable in memory** only. Safest against XSS persistence, but a reload signs the person out.

## Decision

- **Option 2 for files.** `src/Chargehand.Server/Ui/` (HTML, CSS, ES modules) is embedded and served by `WebUi` at `/ui/` only when `http.ui` is `true`; with it off the paths answer like any unknown route behind the key (`401`, `404` with a key). Static files are public and need no key; they carry no data. Every other route still needs one. A neighbour such as `/uix` is not under `/ui` and still needs the key. The Host check applies to the UI as to every route.
- **sessionStorage for the key, sent as the bearer header.** No server-side session, no cookie, no CSRF surface: a request is authorised by a header only the page's own script adds. Signing out removes the key. The key lasts as long as the tab and goes only to the page's own origin.
- **The cost of that choice:** a script that runs in the page can read the key. So the page is built so that none can run:
  - a strict Content-Security-Policy on every `/ui/` response: `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`. No inline script or style, no `eval`, no other origin to load from or send to;
  - run data is rendered only through `textContent`/text nodes and DOM APIs; the code never parses markup from a string. A link is built only for an `http` or `https` URL (a `javascript:` URL from a result becomes plain text);
  - no third-party script, font or CDN, and no build step: the files in the repository are the files that run;
  - `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `Cache-Control: no-cache`.
  Tests check the header on every UI response, that the embedded files equal the folder, that no file names another origin, builds markup from a string, adds an inline script or style, or calls `fetch` outside `api.js`, and that `localStorage` is used for nothing but a repository's web address.
- **Live events over `fetch`.** The browser's own server-sent-events client cannot send an `Authorization` header, so the page reads `/v1/runs/{id}/events` as a stream and parses the frames itself (about 40 lines).
- **What the page calls**, all existing except two additive things. `GET /v1/accounts/me` returns the caller's name, caps, driven and repository rights, the spend in the UTC day and month (the figure the caps are checked against, ADR 0050), unfinished runs, whether the server is halted and the presets the server accepts. `GET /v1/runs` gains `kind` (`single` or `batch`), `repository` (exact path), `run_id` and, for the owner, `actor`; an account's own filter on actor is ignored because it sees only its own runs. `run-summary/v1` gains optional `kind`, `repository`, `commit` and `tokens`. Both are additive.
- **Citations link to code** when the repository has a web address: taken from a pull request URL in the result (GitHub `.../pull/N`, GitLab `.../-/merge_requests/N`), else typed once by the reader and kept in `localStorage` (it is not a secret). The link is `blob/<commit>/<path>#L<n>` at the evidence's commit, else the run's request commit. Nothing is guessed from a local path.

## Consequences

- One more thing to keep from rotting, with no compiler for it: the JavaScript has tests only for what a test can see (served, headers, no external origin, no markup parsing). It was also loaded in a real browser against a server with seeded runs (sign-in, list, detail, live stream, cancel refusal, scoping, halt and resume, phone width), but nothing runs it in CI.
- A key in `sessionStorage` is still readable by a script that gets past the policy; a browser extension or a compromised dependency of the page (there are none) would see it. A hosted setup that wants stronger would put an identity provider in front, which is out of scope here (ADR 0050, "Reopen if").
- The owner's key in the page can halt the server and start batches. Use an account key for everyday watching; the owner's only when a halt is needed.
- Files are served with `no-cache`, so a new release shows at once; the cost is a revalidation per file, a dozen small ones.
- Behind a TLS-terminating proxy nothing changes: the page uses relative paths. Without TLS, over a private network, the key crosses it in clear as it already does for any caller (ADR 0024).
- `GET /v1/accounts/me` reads the run log for the spend, as a capped request already does.

## Reopen if

The page needs a framework or a build step; keys should come from an identity provider (ADR 0050); the UI must be served from another origin; or the policy has to allow something it forbids now.
