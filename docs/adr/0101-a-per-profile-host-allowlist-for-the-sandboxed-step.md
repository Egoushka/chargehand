# 0101. A per-profile host allowlist for the sandboxed step

- Status: accepted
- Date: 2026-11-14

## Context

`SandboxSettings.Network` (ADR 0035) is one switch: a sandboxed step (a writing run's tests, the cache warmer's restore) either has no
network or can reach any host. A step that needs the network — a `dotnet restore`, an agent calling its own API — can also reach
anywhere else, so a compromised dependency or a hijacked build target can send data to a host the profile never named. PR #890 recorded
this as a residual risk: "The network is one switch. A per-profile host allowlist is recorded as a proposal in the spec, not built."
(issue #905; related #895, #890).

Chargehand already narrows egress to a list once, for the driven-session container: `AllowlistProxy` (ADR 0039) is a CONNECT-only
forward proxy with a per-host, per-port `AllowlistMatcher`, handed to the container as `HTTPS_PROXY`. The sandboxed step runs on the
orchestrator's own host, not in a container with its own network namespace, so the same mechanism cannot simply be pointed at it as-is.

## Prior art

No prior-art query was run: the allowlist proxy this reuses is chargehand's own (ADR 0039), and the question here is how to extend it to
a second runtime (the local sandbox), not whether proxy-based egress filtering is a reasonable design.

- Coverage: miss (not queried)

## Options

1. A second allowlist engine inside each sandbox implementation (bwrap, seatbelt). Rejected: two engines to keep in sync, and bwrap has
   no host-filtering primitive of its own to hang it on.
2. Reuse `AllowlistProxy` on a loopback port for the duration of the sandboxed command, and force the command through it with
   `HTTPS_PROXY`/`HTTP_PROXY`. Chosen: one allowlist engine, already tested against CONNECT, private-address rebinding and malformed
   requests (`AllowlistProxyTests`).
3. Leave the switch as it is and rely on each step's own command-line restrictions (`--locked-mode`, scoped registry config) to narrow
   egress. Rejected: per-tool, easy to miss, and does nothing for a step that calls an agent API directly.

## Decision

Option 2. `SandboxSpec.AllowedHosts` (and the profile's `sandbox.allowed_hosts`) names the hosts a sandboxed command may reach once
`Network` is on; null or empty keeps today's plain switch. When set, `BubblewrapSandbox`/`SeatbeltSandbox.Wrap` start a `SandboxEgress`
— an `AllowlistProxy` bound to `127.0.0.1:0` with an `AllowlistMatcher` built from the list — and merge its `HTTPS_PROXY`/`HTTP_PROXY`
(upper and lower case) over the command's environment, with `NO_PROXY` cleared so nothing can declare itself exempt. The egress is
disposed with the `SandboxLaunch`.

How much this actually closes off differs by platform, and each sandbox says so in its own doc comment:

- **Seatbelt** (macOS): the generated profile allows `network-outbound` to the proxy's loopback port only, instead of the broad
  `(allow network*)`. The kernel itself refuses any other destination, whether or not the command inside is proxy-aware.
- **Bubblewrap** (Linux): `bwrap` has no "reach only this loopback port" primitive — the sandboxed command still gets `--share-net`,
  the host's own network namespace — and giving it a private namespace with its own route would need `CAP_NET_ADMIN` this process does
  not have. A proxy-aware client (npm, NuGet, curl, most HTTP libraries) that reaches past the allowlist is refused by the proxy; a
  client that opens a raw socket and ignores `HTTPS_PROXY` is not stopped by this alone. This gap is the one the issue's "Seatbelt needs
  the equivalent" line does not have an answer for on Linux yet.

The proxy is tested on its own (`AllowlistProxyTests`): a CONNECT to an unlisted host, an IP literal, a disallowed port and a name that
resolves to a private address are all refused. `SandboxEgressTests` covers the new wiring (the env it hands out, an unlisted host
refused through the started proxy, `Merge`/`Dispose`); `SandboxTests` covers that `Wrap` only starts it with `Network` true and
`AllowedHosts` set, and that Seatbelt's profile narrows instead of widening.

## Consequences

- A profile that wants package restore and an agent API reachable, and nothing else, lists those hosts once
  (`sandbox.allowed_hosts`) and gets it enforced on both the writing-run verifier (`Verifier`) and the cache warmer
  (`SandboxedStepRunner`), which both now take the list through to `SandboxSpec`.
- On Linux the allowlist is a property of a cooperating client, not of the kernel: a decoy build target that shells out to `curl` still
  goes through the proxy and is refused, but a target that speaks raw sockets is not. This is the same gap bwrap already has for
  anything else a hostile `Directory.Build.props` could do that `--unshare-all` does not cover category-by-category.
- No profile sets `allowed_hosts` today; the switch stays the plain on/off of ADR 0035 until one does.

## Reopen if

A Linux sandbox with real per-destination network control (a network namespace with its own default-deny route, requiring
`CAP_NET_ADMIN` or a setuid helper) becomes available, closing the raw-socket gap bwrap leaves open.
