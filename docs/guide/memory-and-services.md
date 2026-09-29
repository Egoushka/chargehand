---
title: "Memory and services"
description: "Recall from any MCP memory server, several at once, and give a preset's workers read-only MCP tools: mcp_servers, the memory mapping with Hindsight and Chronicle examples, retain, services, and the check command."
order: 6
section: "Guides"
---

Two parts of a run can come from MCP servers you name in the profile. **Memory** is context chargehand fetches before the workers start and, if you turn it on, writes to when the run ends. **Services** are read-only tools a preset lets its workers call. Both are off until you list a server: a profile with no `mcp_servers`, no `memory` and no preset `services` behaves as before. This page describes v0.6.1 ([changelog](../../CHANGELOG.md#061---2026-09-29)).

## Name your servers: mcp_servers

`mcp_servers` maps a name to one MCP server. Memory entries and presets refer to servers by that name, so a gateway that serves memory tools and service tools is one entry. A name is lowercase letters, digits and hyphens, starting with a letter: Claude Code spells a tool `mcp__<server>__<tool>`, and the pattern keeps that unambiguous.

| field | applies to | meaning |
|---|---|---|
| `url` | HTTP servers | an absolute `http` or `https` URL. It never holds `{secret:...}`, because URLs end up in logs |
| `headers` | `url` | sent on every request; values may hold `{secret:item}` |
| `transport` | `url` | `auto` (default), `streamable-http` or `sse` |
| `command` | stdio servers | the argv of a server chargehand starts, no shell; it never holds `{secret:...}` |
| `env` | `command` | environment variables for that process; values may hold `{secret:item}` |

Each entry has exactly one of `url` and `command`. A stdio server gets the MCP SDK's default environment (`PATH`, `HOME` and the like) plus its `env`, and nothing else of chargehand's own, because that environment can hold the HTTP interface's key and Claude Code credentials.

**Transports.** `auto` tries Streamable HTTP and falls back to the older SSE transport when the server does not speak it; that is what chargehand's own connection does for memory and `extensions check`. A worker's runtime cannot fall back. For a preset's services on Claude Code, `auto` means Streamable HTTP, because its config names a type; OpenCode gets the URL and headers as a remote server, and chargehand names no transport for it. A server that speaks only SSE, such as Chronicle at its `/sse` endpoint, therefore needs `"transport": "sse"`. That also skips the failed first attempt and its delay.

**Secrets.** `{secret:item}` in a header or `env` value is replaced when the connection opens, by the profile's `secrets` sources in order, first success wins. The default source reads an environment variable named like the item, upper-cased with `-` turned into `_`. A `command` source runs an argv with `{item}` replaced in each element, and its stdout is the value. A source that fails, prints nothing or runs longer than 15 s is skipped and the next one tried. If none resolves, chargehand makes no connection, and never an anonymous one; the message names the item and never a value.

`{item}` can go anywhere in the argv, so a store that keys its items by more than the item name still works. A macOS Keychain item stored with a service and an account is not found by `-s` alone; fix the service as a literal and let `{item}` stand for the account:

```json
"secrets": [
  { "env": true },
  { "command": ["security", "find-generic-password", "-s", "<service>", "-a", "{item}", "-w"] }
]
```

## Memory

Once per run, after intake, chargehand asks every provider in the profile's `memory` list for facts about the request and appends them to each worker's prompt. Each provider is one MCP server plus a mapping that says which of its tools recall, retain and invalidate, what arguments they take and where the facts are in the answer. There is no default mapping, because servers name tools and arguments differently.

The profile below stacks two providers: Hindsight through an MCP gateway (a bank, retain available) and Chronicle (a recall-only archive on a legacy SSE server). It is the shape `profiles/example.json` carries.

```json
{
  "schema": "profile/v1",
  "mcp_servers": {
    "gateway": {
      "url": "https://mcp.example.internal/mcp",
      "headers": { "Authorization": "Bearer {secret:gateway-token}" }
    },
    "chronicle": { "url": "http://localhost:<port>/sse", "transport": "sse" }
  },
  "memory": [
    {
      "name": "hindsight",
      "server": "gateway",
      "namespace": "chargehand",
      "tools": {
        "recall": {
          "tool": "hindsight_recall",
          "arguments": { "query": "{query}", "bank_id": "{namespace}", "budget": "low", "max_tokens": 1024 }
        },
        "retain": {
          "tool": "hindsight_retain",
          "arguments": {
            "content": "{text}", "context": "{context}", "document_id": "{document_id}",
            "timestamp": "{timestamp}", "tags": "{tags}", "bank_id": "{namespace}",
            "metadata": { "repository": "{repository}", "commit": "{commit}", "locators": "{locators}" }
          }
        },
        "invalidate": {
          "tool": "hindsight_invalidate_memory",
          "arguments": { "memory_id": "{id}", "reason": "{reason}", "bank_id": "{namespace}" }
        }
      },
      "retain": false
    },
    {
      "name": "chronicle",
      "server": "chronicle",
      "tools": {
        "recall": {
          "tool": "recall",
          "arguments": { "query": "{query}", "limit": "{max_facts}" },
          "results": { "path": "results", "id": "segment_id", "text": ["{date}: {summary}", "{date}: {text}"] }
        }
      }
    }
  ]
}
```

### The entry

| field | default | meaning |
|---|---|---|
| `name` | required | unique, same pattern as a server name. It labels the facts in the prompt, names the chain block `memory/recall/<name>` and the span tags |
| `server` | required | a key of `mcp_servers` |
| `tools` | required | the mapping below |
| `namespace` | the entry's `name` | the value of `{namespace}`, for example a Hindsight bank |
| `max_facts`, `max_chars`, `max_fact_chars` | 10, 4000, 600 | facts kept per recall, characters kept per recall, characters kept per fact (cut with an ellipsis) |
| `timeout_seconds` | 10 | how long one recall or retain call may take |
| `retain` | false | write to this provider when a run ends; needs `tools.retain` |
| `retain_tags` | `["chargehand"]` | tags of the retained item |

`tools.recall` is required: a provider takes part in recall through it. `tools.retain` is required when `retain` is true. `tools.invalidate` is optional and no run calls it. A tool is `{ "tool": "<name>", "arguments": { ... } }`, and the profile fails to load if a mapping names an unknown server or placeholder, or misses its recall tool.

**Arguments.** A string that is exactly one placeholder keeps the value's type: `{tags}` is an array, `{max_facts}` an integer, `{timestamp}` an ISO 8601 string. A placeholder inside longer text is replaced as text, numbers and booleans are literal, and a null value leaves the argument out.

| tool | placeholders |
|---|---|
| recall | `{query}` (the request text), `{namespace}`, `{max_facts}` |
| retain | `{namespace}`, `{text}`, `{context}`, `{document_id}`, `{timestamp}`, `{tags}`, and the provenance `{repository}`, `{commit}` (12 hex characters) and `{locators}` (every locator of the item, joined with `; `) |
| invalidate | `{namespace}`, `{id}`, `{reason}` |

**Results.** For recall, chargehand reads the tool's `structuredContent` when `results.path` leads to an array in it, and otherwise the first text block parsed as JSON. A Python MCP tool that returns a string sends it as text and, wrapped as `{"result": "..."}`, as structured content too; Chronicle does, so the wrapper has no array at `results` and the text block is read. An error result, a path that does not resolve or text that is not JSON counts as a provider failure.

| `results` field | default | meaning |
|---|---|---|
| `path` | `results`; the root if a `results` block leaves it out | dotted property names leading to the array |
| `id` | `id` | the field holding a fact's id; a missing id becomes the first 12 hex characters of the text's SHA-256 |
| `text` | `text` | a field name, a template over fields such as `{date}: {summary}` (a dotted name such as `{metadata.commit}` reads a nested object), or an ordered list of these. An entry is used only if every field it names is present and non-empty; the first that qualifies wins, and an item with none is skipped |
| `format` | `json` | `text` takes the whole first text block as one fact |

### Hindsight through a gateway

Hindsight keeps facts in banks. Through an MCP gateway that serves several backends, its tools carry the backend's name as a prefix (`hindsight_recall`, `hindsight_retain`, `hindsight_invalidate_memory`); a Hindsight endpoint you reach directly lists `recall`, `retain` and `invalidate_memory`. Use the names `chargehand extensions check` reports for your server. The recall answer is one text block holding `{"results":[{"id":...,"text":...}]}`, which the default `results` mapping reads, so the entry has no `results` block. `"retain": false` is the default, and the tool mapping stays in place for when you turn it on.

### Chronicle: recall only, over SSE

Chronicle is an archive of your own conversations and events. Its MCP server speaks legacy SSE at `/sse`, on loopback, takes no credentials and has no write tool, so the entry above:

- names the server with `"transport": "sse"`; `http://localhost:<port>/sse` stands for wherever your Chronicle listens;
- passes the entry's `max_facts` as `limit`, an integer because the whole string is one placeholder, and leaves `date_from`, `date_to` and `source` out so Chronicle's own routing applies (a literal there narrows the search);
- reads `results`, takes `segment_id` as the id, and writes each fact as `<date>: <summary>`; a segment not yet enriched has a null summary, so the second template falls back to the raw text, which `max_fact_chars` then cuts;
- has no `retain` tool and no `retain` flag, and no `namespace`, because Chronicle has no banks.

Chronicle is a personal archive. Recalled segments go into the worker's prompt and on to the model provider the runtime uses, like file contents, so list it only in a profile where that is intended. Its other tools (`first_mention`, `evolution`, `tally`, `timeline`, `open_commitments`, `ground`) are not memory operations; a preset of your own can grant them to workers as a service.

### Stacking

Every provider is asked at once, each under its own `timeout_seconds`. The results merge in the order of the `memory` list: each provider contributes items up to `max_facts` and `max_chars`, a fact is collapsed to one line, and a fact that an earlier provider already returned (same text, ignoring case and surrounding whitespace) appears once with both names. The prompt gets:

```
Facts from long-term memory (unverified; check them in the repository and cite files, never these). The name in brackets is the memory each came from:
- [hindsight] Deploys go through GitOps.
- [hindsight, notes] The API registers handlers with MediatR.
```

Recalled text is untrusted context: the header tells the worker to check it in the repository and never cite it, and workers are read-only. A provider that throws, times out or answers something its mapping cannot read is skipped for that run with a reason; the others still contribute, and with all of them failing the run goes on with no facts. Cancelling the run is the only thing that stops it. The run records one chain block `memory/recall/<name>` per provider that contributed (hashed over that provider's lines), `chargehand show` prints one line per provider, and the span tags are `chargehand.memory.<name>.recalled` and `.error`. `result/v1` does not change. The `eval` commands run without memory.

### Retain

With `retain` true on an entry, a completed run writes one item to that provider, and only from claims whose citations resolved. A claim is retained when it is in a completed result, cites at least one `file` or `commit` entry that resolved at the run's pinned commit, and its text and locators pass the error-text scrubber unchanged (text that looks like a key is left out). The item names where the citations were checked:

```
Repository: github.com/example/proj, commit 0123456789ab (citations checked at this commit)
- The API registers handlers with MediatR. [src/Api/Startup.cs:41-58] (confidence 0.90)
```

Hindsight rewrites the item into facts of its own and keeps only a sentence about the repository, so the mapping also sends where the citations were checked as `metadata` (a string-to-string map on the retain tool), from `{repository}`, `{commit}` and `{locators}`; the Hindsight entry above does. A mapping without those placeholders sends nothing extra. The full item text stays on the stored document, whose `document_id` is the run id.

Every run with `retain` on also recalls first, and recall appends up to 10 bank facts (`max_facts`) to the prompt of each worker, so they go to the model provider.

Its `document_id` is the run id, its context is `chargehand run result`, its timestamp is the run's finish, and its tags are the entry's `retain_tags`. The repository is the checkout's `origin` URL without scheme, user information, port and `.git`, or the directory name when there is no `origin`.

Not retained: the request text, the summary, claims that rest only on caller inputs, URLs or session messages (they do not outlive the run), claims whose citations did not resolve (they are already in `open_questions`), and everything from a run without a repository, which has no commit. There is no confidence floor. A run that retained nothing says why in `chargehand show` (`no commit`, `no claim qualified`). "Resolved" means the cited path and lines, or the commit, exist at that commit; chargehand does not yet compare the cited text with the claim, which is goal 0.8 in [ROADMAP.md](../../ROADMAP.md).

## Services

A service is an MCP server from `mcp_servers` plus the tool names a preset lets its workers call. Shipped presets list none, because a preset that needs a server would fail on a clean machine. Write your own: copy `presets/cheap.yaml` to `presets/docs.yaml`, change its `name`, copy `prompts/preset/cheap.md` to `prompts/preset/docs.md` (each preset has a prompt block of its own), and add `services` to the node kind:

```yaml
node_kinds:
  worker:
    # model, permissions and budget as in cheap.yaml
    services:
      - server: team-docs
        tools: [search_docs, read_doc]
```

Tool names are explicit. A `*` inside a name is a glob (`search_*`), and a lone `*` is refused, so a preset never grants a whole server. At the start of a run chargehand connects to the server, lists its tools and grants the ones the names match. A server that is not in `mcp_servers`, a secret nothing resolves, a server that does not connect, or a name the server does not list drops that part of the request, is recorded on the run (`chargehand show` prints one `service` line) and does not fail it. Workers stay read-only whatever the server offers. chargehand does not proxy tool calls; a gateway you already run is the place to filter further.

| | Claude Code | OpenCode |
|---|---|---|
| How the server reaches the worker | a private `--mcp-config` file, mode 0600 in a fresh 0700 directory under the system temp directory, never in the checkout, removed when the turn's process exits | chargehand registers the server at the run's checkout location before the first session and waits until OpenCode reports it `connected` (30 s at most) |
| What the worker may call | `--allowedTools` names exactly the granted tools and the server's other tools are disallowed; the `dontAsk` mode refuses everything else | the session's rules end with `*_* * deny` and one `<server>_<tool> * allow` per granted tool |
| A server that does not connect | `not_connected: <status>` on the run | dropped, removed, `not_connected: <status>` on the run |

Credentials stay out of argv and logs on both. On OpenCode the registration is named for the server and a keyed hash of its config, shared by every run at that location that uses the same config, held by count and removed when the last run ends.

On OpenCode:

- **Project configuration is off.** The OpenCode server chargehand starts, and `scripts/opencode-serve.sh`, set `OPENCODE_DISABLE_PROJECT_CONFIG=1` and `OPENCODE_CONFIG_PROJECT_DISABLE=1`, so a checkout's `opencode.json`, `.opencode`, `AGENTS.md` and project skills are not read: a checkout could otherwise start a command. If you start OpenCode another way, set both variables.
- **MCP tools are denied unless a preset grants them.** Every OpenCode session's rules end with `*_* * deny`, with or without services, because a preset's leading `* * allow` would otherwise reach the tools of any server registered at the location.
- **Presets that deny `*` cannot use services.** Workers reach a server through OpenCode's code-execution tool, `execute`, and `* * deny` (the `draft` preset) removes it. A preset that lists services must not deny it; the shipped presets start with `* * allow`, so a copy of one works. The Claude Code runtime has no such limit.

A claim that rests on a service's output cites either a URL the service returned (kind `url`, resolved as seen in the node's tool output) or the reply itself: kind `session_message` with a locator that quotes at least 12 characters of the tool's reply exactly. Workers do not see message ids, so the quotation is what they can cite; it resolves only against what a tool of the session returned (not the caller's inputs, and not the arguments of a call). The task text tells the worker so, only when the run has services. Such a claim is not retained. Service output is not stored, and the support check comes with goal 0.8.

## Check your setup

```bash
chargehand extensions check [--preset <name>] [--probe "<query>"]
```

It connects every `mcp_servers` entry and lists its tools, then checks that each tool a memory mapping names is listed, that every argument name is a property of the tool's input schema and every required argument is set, and that each preset's `services` names match listed tools. It reads every preset in `presets/`, or only `--preset`. One line per item, `ok` or the problem with `; action:` and what to do:

```
server gateway: connected, 12 tools
memory hindsight: recall -> hindsight_recall ok
memory hindsight: retain -> hindsight_retain ok
preset docs: service team-docs: search_docs ok
```

`--probe` also runs one real recall per memory and prints how many facts came back, never the facts. Exit 0 means nothing is wrong, 1 a problem, 2 a usage error or a profile that does not load. A line never holds a URL, a credential or an argument value.

## Reading a run

`chargehand show <run-id>` prints one line per memory and per service after the run summary:

```
memory hindsight: recalled 3, retained 1
memory chronicle: recall skipped (<reason>), retained 0
service team-docs: granted read_doc, search_docs
service team-notes: dropped (unreachable: <reason>)
```

## When something is down

| Situation | What happens |
|---|---|
| A memory server is down, times out, errors or answers what the mapping cannot read | skipped for this run with the reason; the other providers contribute and the run continues |
| A server needs a secret nothing resolves | no connection; the provider or service is skipped as above |
| Retain fails | logged; the result is unchanged |
| A claim does not qualify for retain | left out and counted; unresolved claims are already open questions |
| The commit or the citations of a run cannot be established | nothing is retained |
| A service's server, secret or tool does not resolve | that service's tools are missing for the run, recorded; the run continues |
| A granted service tool errors during a run | the worker sees a tool error; an uncited claim becomes an open question as usual |
| You cancel the run | it stops; nothing swallows the cancellation |

Optional things fail open; credentials fail closed.

## Migrating from the old memory object

Before this change `memory` was one object that talked to a Hindsight HTTP API. A profile that still has it fails to load with `memory is a list now` and a pointer to this page. Move the URL and key into `mcp_servers`, and list the provider under `memory`:

```json
"memory": { "backend": "hindsight", "url": "https://<your memory service>", "namespace": "<your bank>",
            "api_key_secret": "<api-key-item>", "max_tokens": 1024, "retain": false }
```

becomes the `gateway` server and the `hindsight` entry above, with the service's MCP endpoint as `url`, the key as `"Authorization": "Bearer {secret:<api-key-item>}"`, `namespace` as the entry's `namespace` and `{namespace}` in the arguments, `max_tokens` as a literal argument of the recall mapping, and `retain` on the entry. Run `chargehand extensions check --probe "<a real query>"` before the first run: it prints which mapped tools the server lacks.

## Checked live

The tests ([GoalSixTests](../../tests/Chargehand.Tests/GoalSixTests.cs), [MemoryStackTests](../../tests/Chargehand.Tests/MemoryStackTests.cs), [McpMemoryProviderTests](../../tests/Chargehand.Tests/McpMemoryProviderTests.cs) and the service tests) use fake MCP servers and fake runtime CLIs. On 2026-09-29 the maintainer's session also ran both halves against real software.

**Memory.** Hindsight through a gateway, and Chronicle over an SSH tunnel to its loopback endpoint with `"transport": "sse"`, on a `draft` run with no commit:

| Check | Result |
|---|---|
| `chargehand extensions check --probe` | exit 0, every line `ok` |
| Hindsight recall over its HTTP API against the gateway's MCP recall | 20 of 20 ids equal, in the same order and with the same text |
| A run with the old object form, then one with the list form | the same `memory/recall/hindsight` chain block hash, 10 facts each |
| A run with both providers in the list | 10 facts recalled from `hindsight` and 10 from `chronicle` |
| Retain | nothing retained: a draft run has no commit |

**Retain.** A real `chargehand run` (OpenCode 2.0.16, the `cheap` preset, a small model) on a scratch repository with one commit and no `origin`, asked what the first line of its README says. The memory was Hindsight through the gateway with `retain: true`, and `retain_tags` carried the bank's project and type tags in place of the default `chargehand`, because that bank's tag rules ask for one of each:

| Check | Result |
|---|---|
| `chargehand run` | completed for about $0.001 with one claim citing `README.md:1`; `chargehand show` read `memory hindsight: recalled 10, retained 1` |
| The stored document (its `document_id` is the run id) | the item as built: the `Repository:` line with the directory name and the 12-character commit, then the claim with `[README.md:1]` and `(confidence 1.00)`; its tags were exactly `retain_tags` |
| Recall | a query on the run's subject returned two ids: the fact Hindsight extracted from the item, tied to that `document_id`, and an observation it consolidated from the fact |
| Invalidate | `hindsight_invalidate_memory` on the fact set it to `invalidated` with the reason; the observation had no other source and was gone; a new recall returned neither. The document stays, with no facts left; nothing was deleted |

Hindsight rewrites the item into facts of its own. The extracted fact kept the repository label and dropped the commit, the locators and the confidence: it was one sentence about the repository with a date. The full item is the document's text, so a recalled fact names the repository but not the commit, which is on the document. That is why retain now sends the provenance as `metadata`. The gateway's retain tool takes `metadata` as an object of strings (or null), and each recalled fact has a `metadata` object (empty on all 19 facts of the bank checked). Whether Hindsight copies a document's metadata onto the facts it extracts is not checked: nothing was written to a bank. To show it on recalled facts, give the recall entry `"results": { "path": "results", "text": ["{text} [{metadata.repository}@{metadata.commit}]", "{text}"] }`; a fact without metadata falls back to the second entry. If Hindsight does not copy it, recalled facts carry no provenance and the document does.

**Services.** A stdio test server on the real Claude Code runtime, and on the shipped OpenCode runtime first with a stand-in model, then with a real one:

| Check | Result |
|---|---|
| Claude Code 2.1.283, one small-model call, a test server granted `echo_fact` | the tool was called and answered; the server's other tool, `write_note`, was absent from the worker's tools |
| Claude Code, a second granted server whose command is missing | it read `failed`, and no config directory was left behind |
| OpenCode 2.0.19, a stand-in model | a granted tool answered and an ungranted one was unknown |
| OpenCode, two runs with different grants at one location | each saw only its own tool |
| OpenCode, a server that fails to start | it was reported and removed |
| OpenCode 2.0.19, a small model, whole `chargehand run`s on a scratch repository, a copy of `cheap` that grants `echo_fact` of the stdio test server | `chargehand show` printed `service fake: granted echo_fact`; the worker called the tool through `execute` and got `echo_fact ok: spike fact`, and the run's answer quoted it (two runs) |
| The same, asked to call `write_note` too | the worker's `search` for it found nothing and the call failed with `Unknown tool '<server>-<hash>.write_note'`; the session's rules ended with `*_* * deny` and one allow for `echo_fact` |
| The same, the registration during and after a run | the server read `connected` at the run's location while the run was going, and `GET /api/mcp` listed no server there afterwards |
| `chargehand extensions check --preset` on that copy | exit 0: `server fake: connected, 2 tools` and `preset <copy>: service fake: echo_fact ok` |

The OpenCode server was a throwaway one started with `scripts/opencode-serve.sh`, and the three runs on the small model cost about $0.005 in all by the run log's prices. Two things the runs showed about citations, neither a fault of the service path. The worker cited the tool's reply as a session message with the reply text as the locator; session-message evidence resolves by message id, so that claim did not resolve and became an open question (the first run then completed with no claims; the run that also called `write_note` ended `needs_input` for the same reason). The test server returns no URL, and a URL is the only citation of service output the resolver accepts. The answer text itself carried the reply in every run.

Not yet run against real services, and covered in CI on fake servers only:

1. A claim that cites a URL a service returned, on either runtime: the test server returns none, so the OpenCode run above could not exercise it.
2. One memory server stopped: the run still completes and `chargehand show` says it was skipped.

Fixed after this check: a worker's citation of a service tool's reply resolves when its locator quotes the reply (see above). The runs above predate it and were not repeated on a real model.
