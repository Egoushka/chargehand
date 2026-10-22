---
title: "Answers from the phone"
description: "A driven task's question or partial stop goes to an approvals server or an ntfy topic with its answer options; the signed answer that comes back continues the task."
order: 16
section: "Guides"
---

A driven task that stops to ask (it ended `needs_input`) or stops before it finished (a partial: it kept its work and opened a draft pull request) waits in the console's inbox until someone answers it. With `automation.notify` ([ADR 0077](../adr/0077-answers-from-the-phone.md)) the server also sends it where a phone sees it, with the answers a person can give, and an answer that comes back continues the task the way the console's answer does: a new batch of that one task with `continue_from` and `answers` ([ADR 0045](../adr/0045-governed-sessions.md)). Off unless the profile has `automation.notify`.

```json
"automation": { "notify": {
  "approvals": {
    "url": "https://approvals.example",
    "token_secret": "approvals-chargehand-token",
    "key_secret": "approvals-hmac-key",
    "callback_url": "https://chargehand.example/v1/answers/approvals"
  },
  "ntfy_url": "https://ntfy.example/my-topic",
  "console_url": "https://chargehand.example/console/"
} }
```

## Keys

- `approvals`: an approvals server that speaks the items contract below. Answers come back from it. Optional.
  - `url`: its base URL.
  - `token_secret`: the secret item with this source's bearer token on that server.
  - `key_secret`: the secret item with the key that server signs decisions with.
  - `callback_url`: this server's `/v1/answers/approvals` as the approvals server reaches it. It must be https, and the approvals server must allow it.
  - `source`: the source name the token belongs to (default `chargehand`). Every item id starts with it.
- `ntfy_url`: an ntfy-style URL. Each notice is a plain-text note with the questions and a link to the task. There are no buttons: ntfy has no way to send an answer back that this server would trust. Optional; set it, `approvals`, or both.
- `console_url`: the console as the phone reaches it. Every notice links to the task's page there. Optional.

Needs `driven.enabled`. Secrets are read at each call, so a missing one fails that call (a line on stderr) and not the server's start.

## What is sent, and when

The server reads its inbox every minute (the owner's view, every account). Each open question, and each draft pull request of a partial stop, is sent once to every sink. A sink that fails is tried again the next minute. When the item leaves the inbox (answered on the console, superseded by a later batch, dismissed, its pull request closed), the approvals item is withdrawn. On the first start with `automation.notify`, the server records the time: items that were already open are not sent, so turning it on does not send the last two weeks of questions. State is in `notify/state.json` beside the run log.

## The items contract

chargehand is a source of the approvals server. It `PUT`s one decision item per notice, with its source token as the bearer:

```
PUT {url}/items/{id}
Authorization: Bearer <token>
```

```json
{
  "source": "chargehand",
  "id": "chargehand:question:run-20261022-081203-ab12cd:export",
  "kind": "decision",
  "priority": 4,
  "title": "Question: Add the CSV export",
  "summary": "/srv/repos/app · task export",
  "body": { "repository": "/srv/repos/app", "task": "export", "questions": "1. Which delimiter?\n2. Keep the old JSON export?", "answer": "" },
  "editable": ["body.answer"],
  "links": [{ "label": "Task", "url": "https://chargehand.example/console/runs/run-20261022-081203-ab12cd/tasks/export" }],
  "actions": [
    { "id": "answer", "label": "Send answer", "style": "primary" },
    { "id": "decide", "label": "Your call" },
    { "id": "drop", "label": "Drop", "style": "danger", "confirm": true }
  ],
  "expires_at": "2026-10-29T08:12:03.0000000Z",
  "callback": "https://chargehand.example/v1/answers/approvals"
}
```

- `id` is `<source>:<kind>[.<round>]:<batch run id>:<task id>`. `kind` is `question` or `partial`. `round` appears from the second send of the same notice (see the empty answer below).
- A partial stop is titled `Stopped early: …`; its body says the work so far is kept, it links the draft pull request, and its actions are `continue` (primary) and `drop`.
- `expires_at` is when the task's kept session state and work are swept: 7 days after it stopped.
- `200`, `201` and `409` (already decided) count as sent. A withdrawal is `DELETE {url}/items/{id}`; `404` and `409` count as done.

### The decision

The approvals server posts the person's decision to `callback`, with no key:

```json
{ "decision": "01JB…", "id": "chargehand:question:run-…:export", "action": "answer", "edited": { "body.answer": "Semicolons; drop the JSON export." }, "at": "2026-10-22T08:20:11.000Z", "sig": "…" }
```

`sig` is `HMAC-SHA256(key, decision|id|action|at|h)` in lower-case hex, where `h` is the SHA-256 hex of `edited` written as JavaScript's `JSON.stringify` writes it with the keys sorted, or the empty string when `edited` is empty. `edited` holds only the fields the person changed. The signature is the only credential, so chargehand refuses a decision whose `at` is more than 15 minutes from its own clock.

| Action | What chargehand does |
|---|---|
| `answer` | Starts a batch of the task with `continue_from` and the typed text as its one answer. With nothing typed it starts nothing and sends the question again under the next round's id, because the approvals server cannot reopen a decided item. |
| `decide` | Starts the continuation with the answer "decide each open question yourself, choosing the option that changes the least, and say in the pull request what you chose and why". |
| `continue` | Starts the continuation of a partial stop from the work it kept, with the typed text as a note when there is one. |
| `drop` | Starts nothing and dismisses the inbox item. |

The continuation is admitted as the account that started the batch, with that account's caps. The batch gets an `operator` event naming the run.

Every answer is JSON `{ "ok": bool, "message": "…" }`. The approvals server shows the message on the phone.

| Status | When |
|---|---|
| `200` | Done: `Started run …`, `Dropped; nothing started`, or the question comes again. |
| `409` | Nothing to do: the same `decision` id was acted on already, or the item left the inbox before the answer came (answered on the console). The approvals server reads `409` as already handled. |
| `400` | Not a decision, an id of another source, or an action that does not fit the kind. |
| `401` | The signature does not match, or `at` is too far off. |
| `403`, `429`, `503` | The account's caps refused the run, too many runs are unfinished, the server is halted, or it cannot read its key. Retry from the phone later. |

The route is open without a key only when `approvals` is set; otherwise it answers like any other route.
