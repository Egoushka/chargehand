# 0071. Sign in to the console by the identity a proxy checked

- Status: accepted
- Date: 2026-10-19

## Context

The console ([ADR 0056](0056-operator-console.md)) and the web UI ([ADR 0054](0054-web-ui.md)) ask for an API key and keep it in `sessionStorage`, so every new tab and every browser restart asks again. The maintainer finds that tiresome. A server that is reached from a browser over the internet already sits behind Cloudflare Access, which has checked who the person is before a request arrives, and puts a token it signed in the `Cf-Access-Jwt-Assertion` header of every request it lets through. [ADR 0050](0050-accounts-and-named-api-keys.md) said to reopen accounts when keys must come from an identity provider.

The console the maintainer opens, on the `.lab` host, is behind Pangolin (a Traefik resource with single sign-on), not Cloudflare Access. Pangolin forwards `Remote-Email` and `Remote-User` as plain headers and signs nothing, so the server cannot check them the way it checks a Cloudflare token. The same Traefik also routes `/v1/mcp` of another public host with no sign-on, and the server is also published on the tailnet.

Facts used. Cloudflare signs that token with RS256 under keys it publishes at `https://<team>.cloudflareaccess.com/cdn-cgi/access/certs`; the token carries the team as `iss`, the Access application's audience tag in `aud`, `exp`, and the person's `email`. The header is added on every request whose browser holds the Access cookie, including a request another site causes, unless the cookie's SameSite setting stops it. `/cdn-cgi/access/logout` on the same host ends the Access session.

## Options

1. **Keep the key in `localStorage`.** One line of change. The key then outlives the tab and any script that ever runs in the page reads it for good.
2. **Trade the key once for a server session cookie.** One paste per browser. A new piece of server state, and a cookie the browser sends by itself, so every write needs a CSRF defence.
3. **Take the identity Cloudflare Access checked.** Nothing to paste and nothing stored in the browser. Works only behind Access, and trusts Cloudflare to have checked the person; a request that reaches the server some other way (the private network) has no valid token and still needs a key.

## Decision

Option 3, beside keys, not instead of them.

- **Profile.** `http.cloudflare_access` holds `team_domain`, `audience` and `identities`, a map from email to `owner` or an account of `http.accounts`. Emails compare without case. A mapping to an undeclared account, a team domain with a scheme or path, or an empty audience is refused when the profile loads.
- **What the server accepts.** Only a request with no `Authorization` header at all: a key sent with it decides alone, so a wrong key is still `401`. The token must be RS256 under a key from the team's certs, name the team as issuer and the audience among `aud`, and not be expired (one minute of skew). Keys are fetched on first use and again when a token names a key id not seen yet, at most once a minute, so a forged key id cannot make the server fetch on every request. A token that fails any check, or an email not in `identities`, gets `401`, as a wrong key does.
- **CSRF.** The identity is taken only from a request that carries `X-Chargehand-Console: 1`. A page on another origin cannot add that header without a CORS preflight, which the server never answers, so a request that rides on the browser's Access cookie alone opens nothing. The console sends it on every call.
- **Proxy sign-in.** `http.proxy_sign_in` holds `hosts`, `proxies`, `identities` and an optional `header` (default `Remote-Email`). The forwarded email counts only when all of these hold: no `Authorization` header; the console's header; the request's `Host` is one of `hosts` (the proxy also serves hosts with no sign-on, where anyone can set the header); and the connection comes from one of `proxies`, addresses or host names resolved again every 30 seconds so a restarted container keeps working. Without the address check, any device on the tailnet that reaches the published port could send `Remote-Email: <owner>`. The weakness that remains is another process that can connect from the proxy's own address, which is the proxy.
- **Console.** With no key in the tab, it calls `GET /v1/accounts/me` once with no key. If the server answers, the console is signed in as that account and no key is stored; otherwise it shows the key form, as before. Signing out of an identity sign-in shows the key form until the page loads again; the proxy's session is the proxy's to end. The web UI at `/ui/` keeps asking for a key: it is to be folded into the console.

## Consequences

- Behind Access, opening the console asks for nothing. On the private network, or without `cloudflare_access`, nothing changes.
- Whoever controls the Access policy controls who signs in; the profile's `identities` list is a second gate, not a replacement for the policy.
- Revoking someone is removing their email from `identities` (restart) or from the Access policy (at once).
- MCP clients and the API keep using keys: they do not run in a browser with an Access session.
- With `proxy_sign_in`, whoever can connect from the proxy's address with the console header and a made-up `Remote-Email` is that person; keep the proxy the only thing on its address.
- The server makes an outbound HTTPS call to the team's certs (Cloudflare Access only). Without it, identity sign-in fails closed and keys still work.

## Reopen if

Another identity provider is wanted (a generic OIDC issuer would use the same checks with its own certs URL), or the console must sign in where there is no Access in front.
