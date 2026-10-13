# 0056. An operator console built with Vite and React, embedded in the server

- Status: accepted. Amends 0054: that ADR's "no build step, no third-party code" holds for `/ui/` only; the console at `/console/` has both.
- Date: 2026-10-13

## Context

ADR 0054 shipped a page of plain ES modules at `/ui/`: three screens in 845 lines, no types, and tests only for what a .NET test can see (served, headers, no external origin). The next screens (a fleet overview, run analytics with charts, a graph of a batch's tasks, highlighted code in claims, a command palette, tables with thousands of rows) are what libraries exist for, and every one of them assumes a build step. Writing them by hand as unbuilt modules would mean vendoring unaudited third-party files into `Ui/` with no compiler and no CI to check them, which is worse for supply-chain safety than a lockfile, not better. The owner chose a build step for this reason.

What stays from 0054: the repository is public, the page holds a bearer key that can start batches and halt the server for the owner, and run data is written by models and repositories, so it is untrusted text on a page that holds a key. The server must still build with `dotnet build` alone: the .NET tests and the `dnx` tool do not need Node.

## Options

### The stack

1. **Plain ES modules and vendored libraries** (0054, extended). No toolchain, but no types, no tests in CI, no tree-shaking, and vendored files that nobody builds from a lockfile.
2. **A server-rendered page** (Razor, Blazor, htmx). Live events and a tab-local key want client state anyway, and Blazor WebAssembly is a large runtime for a table. Rejected.
3. **A single-page app built with a bundler.** Framework: React, Svelte, Solid, Vue or Angular. The libraries the planned screens use (`@xyflow/react` for graphs, `cmdk` for the palette, Radix primitives, TanStack Table and Virtual) are React-first; uPlot, elkjs and Shiki are framework-free. React has the widest set of maintained parts and of people who can review the code.

### Where the build output goes

1. **A separate static site on another origin.** CORS and a second deployment; rejected in 0054 and still.
2. **Committed build output.** Generated files in review, stale on every merge.
3. **Built in CI and in the image, embedded in the server.** One artifact, same origin, nothing generated in the repository.

## Decision

- **React 19, TypeScript, Vite.** TanStack Router for typed routes and TanStack Query for the cache and polling; Table and Virtual join with the screens that need them. Radix primitives, uPlot, `@xyflow/react` with elkjs, Shiki and cmdk are not added now: each comes with its screen, its size named in that pull request. Tokens are CSS custom properties in plain CSS; a CSS-in-JS runtime would inject style at run time against `style-src 'self'`. Tests: Vitest with Testing Library for the API client, the stream parser and the screens; Playwright for one smoke test; Biome for lint and format (one tool, one config).
- **`web/console/`, embedded as resources, served at `/console/`.** `Chargehand.Server.csproj` embeds `web/console/dist/**` when `dist/index.html` exists. Without it the server still builds, and `/console/` answers `503` with a short page that says the console is not built, so `dotnet test` needs no Node. The Docker image builds the console in a Node stage and copies the files only; CI builds it before `dotnet build` so the .NET tests check what the server serves; the NuGet tool pack builds it first.
- **Same trust rules as 0054.** Files are public and need no key; `http.ui` gates both paths; the Host check applies; `/consolex` is not under `/console`. The key stays in `sessionStorage` and goes out as the bearer header, never a cookie. Events are read with `fetch`, because `EventSource` cannot send a header. Everything fetched is dropped from the query cache when the key goes, so a second sign-in never sees the first account's data. A link is built only from an `http` or `https` URL.
- **The policy is 0054's, unchanged:** `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`, with `nosniff`, `no-referrer` and `X-Frame-Options: DENY`. The build is configured to fit it: nothing is inlined (`assetsInlineLimit: 0`), no source maps ship, `index.html` has no inline script or style. `scripts/verify-dist.mjs` runs after `vite build` and fails the build on an inline script or style, an event-handler attribute, a reference outside `/console/`, an unhashed asset or a source map. `vite preview` sends the same headers, so the smoke test fails on a violation (it listens for `securitypolicyviolation`, console errors and requests to another origin), and a .NET test checks that `web/console/csp.ts` holds the server's text.
- **Caching.** Files under `assets/` carry a content hash and are served `public, max-age=31536000, immutable`; `index.html` is `no-cache`. A path with no file extension that is not a file is a client route and gets `index.html`; a missing asset or any path with an extension is `404`, so a stale script is never answered with a page.
- **Supply chain.** The page holds the key, so a package that ends in the bundle runs with it, and the policy does not stop it calling this origin. So:
  - exact versions in `package.json`, `package-lock.json` committed, `npm ci --ignore-scripts` in CI, the Dockerfile and `scripts/check.sh`, and `ignore-scripts=true` in `.npmrc`: no package runs code at install;
  - nothing younger than 14 days: each direct pin was chosen that way, `min-release-age=14` in `.npmrc` holds it for every transitive package when the lockfile changes, and Dependabot's `cooldown` is 14 days;
  - Dependabot (npm, weekly, minor and patch grouped) proposes updates; a major version is its own pull request;
  - no CDN, no remote script, font or style; the Node base image in the Dockerfile is pinned by digest, and its version must equal `.nvmrc` (CI checks);
  - few direct dependencies (4 runtime packages today); each new one is argued in the pull request that adds it.
- **`/ui/` stays until the console reaches parity,** then goes with its `Ui/` folder and tests. Parity means: the runs list with its filters, a run with its live events, claims, tasks and artifacts, cancel, start a run or batch, learning, spend against caps, halt and resume. Until then `/ui/` takes bug fixes only. Both paths are behind `http.ui`.
- **Today's console is the foundation:** the router with a placeholder for each planned screen (overview, runs, run, needs you, new, analytics, learning, intake, repositories, accounts, system), a typed client for the routes that exist (`/v1/runs`, `/v1/runs/{id}`, `/v1/runs/{id}/events`, `/v1/accounts/me`, `/v1/learn`, halt and resume), sign-in, and the runs table. Types are written by hand from `schemas/`; generating them is a follow-up.

## Consequences

- The repository now has a JavaScript supply chain: 136 locked packages, of which 15 reach the bundle (React and ReactDOM, the two TanStack packages and their small cores); the rest run at build or test time only. A compromise of a bundled package is the risk this ADR accepts; the rules above shrink the window and the install-time reach, not the run-time one. Use an account key for everyday watching, as 0054 says.
- Node 24 (the active LTS) is a requirement of `scripts/check.sh`'s web steps, which it skips with a message when Node is absent, and of CI. Nothing in `dotnet build` or `dotnet test` needs it.
- CI gains a `web` job (lint, types, unit tests, build, smoke test) and Node steps in the `build` job. The image build gains a Node stage of well under a minute; the image grows by the size of the build (about 345 KB at the foundation).
- Bumping Node means `.nvmrc`, the Dockerfile tag and its digest together; the CI check catches one without the other.
- The server serves the build uncompressed. Over a private network that is acceptable (110 KB gzipped, 342 KB raw); compression is a later change if it is not.
- Two UIs exist for a while, with two sets of tests.

## Reopen if

A package in the bundle is compromised, or the 14-day rule keeps blocking a security fix that matters; the console needs something the policy forbids; the bundle outgrows what an embedded, uncompressed file can reasonably serve; the page must be served from another origin or rendered on the server; or keys should come from an identity provider (0050, 0054).
