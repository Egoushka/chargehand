// Landing stage 0, items 1 and 3 (docs/specs/2026-11-23-agent-git-landing.md): against a throwaway repository the App is installed on,
// with a one-repository installation token, open a PR, mark it ready, try auto-merge, merge it, push refs/notes/intent, push a revert
// and merge that, then try to create a merge-queue ruleset. Usage: node landing-preconditions.mjs <owner/throwaway>
// Env: APP_ID, INSTALLATION_ID, CHARGEHAND_APP_KEY (PEM or base64 of it). Prints statuses and the token's shape (lengths), never a token or key.
import { createPrivateKey, sign } from "node:crypto";
const [owner, repo] = process.argv[2].split("/");
const appId = process.env.APP_ID, inst = process.env.INSTALLATION_ID;
const b64u = (b) => Buffer.from(b).toString("base64url");
let k = process.env.CHARGEHAND_APP_KEY || ""; if (!k.includes("BEGIN")) k = Buffer.from(k, "base64").toString("utf8"); if (!k.includes("\n")) k = k.replace(/\\n/g, "\n");
const now = Math.floor(Date.now() / 1000);
const h = b64u(JSON.stringify({ alg: "RS256", typ: "JWT" })), c = b64u(JSON.stringify({ iat: now - 60, exp: now + 540, iss: appId }));
const jwt = `${h}.${c}.${b64u(sign("RSA-SHA256", Buffer.from(`${h}.${c}`), createPrivateKey(k)))}`;
const log = (s) => console.log(s);
async function api(path, token, method = "GET", body) {
  const r = await fetch("https://api.github.com" + path, { method, headers: { Authorization: `Bearer ${token}`, Accept: "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28", "User-Agent": "stage0" }, body: body ? JSON.stringify(body) : undefined });
  const j = await r.json().catch(() => ({}));
  return { s: r.status, j };
}
const gql = async (token, query, variables) => { const r = await fetch("https://api.github.com/graphql", { method: "POST", headers: { Authorization: `Bearer ${token}`, "User-Agent": "stage0" }, body: JSON.stringify({ query, variables }) }); return r.json(); };
const app = await api("/app", jwt); log(`app ${app.j.slug} permissions ${JSON.stringify(app.j.permissions)}`);
const t = await api(`/app/installations/${inst}/access_tokens`, jwt, "POST", { repositories: [repo] });
log(`scoped token status ${t.s}`);
const tok = t.j.token;
const cls = { len: tok.length, prefix: tok.slice(0, 4), lower: (tok.match(/[a-z]/g) || []).length, upper: (tok.match(/[A-Z]/g) || []).length, digit: (tok.match(/\d/g) || []).length, us: (tok.match(/_/g) || []).length, dash: (tok.match(/-/g) || []).length, dot: (tok.match(/\./g) || []).length, other: (tok.match(/[^A-Za-z0-9_.-]/g) || []).length, segments: tok.split("_").map((x) => x.length) };
log(`token shape ${JSON.stringify(cls)} repos ${JSON.stringify(t.j.repositories?.map((r) => r.full_name))} perms ${JSON.stringify(t.j.permissions)}`);
const R = `/repos/${owner}/${repo}`;
const main = await api(`${R}/git/ref/heads/main`, tok); log(`get main ref ${main.s}`);
const base = main.j.object?.sha;
const baseCommit = (await api(`${R}/git/commits/${base}`, tok)).j;
async function commit(parent, treeSha, msg) { return (await api(`${R}/git/commits`, tok, "POST", { message: msg, tree: treeSha, parents: [parent] })); }
const blob = await api(`${R}/git/blobs`, tok, "POST", { content: "stage 0 landing probe\n", encoding: "utf-8" }); log(`create blob ${blob.s}`);
const tree = await api(`${R}/git/trees`, tok, "POST", { base_tree: baseCommit.tree.sha, tree: [{ path: "probe.txt", mode: "100644", type: "blob", sha: blob.j.sha }] }); log(`create tree ${tree.s}`);
const c1 = await commit(base, tree.j.sha, "probe: add file\n\nIntent: stage 0"); log(`create commit ${c1.s}`);
const br = await api(`${R}/git/refs`, tok, "POST", { ref: "refs/heads/probe/one", sha: c1.j.sha }); log(`create branch ${br.s}`);
const pr = await api(`${R}/pulls`, tok, "POST", { title: "probe one", head: "probe/one", base: "main", body: "stage 0", draft: true }); log(`open draft PR ${pr.s} ${pr.s >= 300 ? JSON.stringify(pr.j) : ""}`);
if (pr.s < 300) {
  const n = pr.j.number, id = pr.j.node_id;
  const rd = await gql(tok, "mutation($id:ID!){markPullRequestReadyForReview(input:{pullRequestId:$id}){pullRequest{isDraft}}}", { id }); log(`markReady ${JSON.stringify(rd.errors ? rd.errors.map((e) => e.message) : rd.data)}`);
  const am = await gql(tok, "mutation($id:ID!){enablePullRequestAutoMerge(input:{pullRequestId:$id,mergeMethod:SQUASH}){pullRequest{autoMergeRequest{enabledAt}}}}", { id }); log(`enableAutoMerge ${JSON.stringify(am.errors ? am.errors.map((e) => e.message) : am.data)}`);
  const mg = await api(`${R}/pulls/${n}/merge`, tok, "PUT", { merge_method: "squash" }); log(`merge PR ${mg.s} ${mg.s >= 300 ? JSON.stringify(mg.j) : "merged=" + mg.j.merged}`);
  const note = await api(`${R}/git/refs`, tok, "POST", { ref: "refs/notes/intent", sha: c1.j.sha }); log(`push refs/notes/intent ${note.s} ${note.s >= 300 ? JSON.stringify(note.j) : ""}`);
  const mainNow = (await api(`${R}/git/ref/heads/main`, tok)).j.object.sha;
  const rv = await commit(mainNow, baseCommit.tree.sha, "Revert probe"); log(`create revert commit ${rv.s}`);
  const rb = await api(`${R}/git/refs`, tok, "POST", { ref: "refs/heads/probe/revert", sha: rv.j.sha }); log(`push revert branch ${rb.s}`);
  const pr2 = await api(`${R}/pulls`, tok, "POST", { title: "revert probe", head: "probe/revert", base: "main" }); log(`open ready PR ${pr2.s}`);
  if (pr2.s < 300) { const m2 = await api(`${R}/pulls/${pr2.j.number}/merge`, tok, "PUT", { merge_method: "squash" }); log(`merge revert PR ${m2.s}`); }
}
const rule = { name: "queue", target: "branch", enforcement: "active", conditions: { ref_name: { include: ["~DEFAULT_BRANCH"], exclude: [] } }, rules: [{ type: "merge_queue", parameters: { check_response_timeout_minutes: 5, grouping_strategy: "ALLGREEN", max_entries_to_build: 5, max_entries_to_merge: 5, merge_method: "SQUASH", min_entries_to_merge: 1, min_entries_to_merge_wait_minutes: 1 } }] };
const rs = await api(`${R}/rulesets`, tok, "POST", rule); log(`merge_queue ruleset via App ${rs.s} ${rs.s >= 300 ? JSON.stringify(rs.j) : ""}`);
await api(`/installation/token`, tok, "DELETE");
