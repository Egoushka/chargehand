# 0077. Answers from the phone, through an approvals server's signed decisions

- Status: accepted
- Date: 2026-10-22

## Context

From four weeks of the maintainer's own run data: 65 driven runs ended `needs_input`, and draft pull requests of partial stops
waited without anyone looking. Both are listed in the console's inbox (ADR 0064), which is read at a desk. An answer already has
a path: a new batch of the one task with `continue_from` and `answers` (ADR 0045), which the inbox's question items describe.
What is missing is getting the question to a person away from the console, and getting the answer back.

The only notification code is the schedules' ntfy-style POST (ADR 0068): one way, plain text. The maintainer runs an approvals
server whose sources `PUT` decision items with actions and editable fields, and which posts the decision back to a callback,
signed with HMAC-SHA256 over the decision's fields.

## Options

1. A Telegram bot with inline buttons. Needs a bot token, a webhook or a polling loop for button presses, and its own trust model
   for who pressed the button; nothing in the repository speaks Telegram.
2. ntfy action buttons that POST back. ntfy would store a credential for this server in every message, and anyone who reads the
   topic could answer.
3. A source of an approvals server: items with the answer options, the decision back to a callback, signed. Free text fits (one
   editable field), the signature is the credential, and the approvals server already handles devices, retries and expiry.

## Decision

Option 3, with ntfy kept as a one-way sink (a note and a link to the task) for a setup without an approvals server. Both sit
behind one interface, `IQuestionSink`, so another sink is one class.

- `automation.notify` turns it on: `approvals` (url, token and signing-key secret items, callback URL, source name), `ntfy_url`,
  `console_url`.
- A notifier reads the inbox every minute and sends each open question and partial draft once per sink; it withdraws the item
  when it leaves the inbox. Its first wake records a start time: older items are not sent.
- Answers: `answer` (typed text), `decide` (a fixed answer telling the session to choose the least-change option and say so in
  the pull request), `continue` (a partial, with an optional note), `drop` (dismiss). Each continuation goes through the same
  admission as a person's request, as the batch's own account.
- `POST /v1/answers/approvals` is the one route the key middleware opens without a key, and only when `approvals` is set. The
  route checks the HMAC, refuses a decision more than 15 minutes from its clock, and acts on a decision id once.
- An `answer` with nothing typed starts nothing and sends the question again under a new item id: the approvals server cannot
  reopen a decided item.
- The contract is written down in `docs/guide/phone-answers.md`, so the approvals side is built against it.

## Consequences

- A question can be answered from a phone, and the continuation shows in the console like any other.
- The approvals key is a credential that starts runs: anyone with it can continue any open task. It is a secret item, read per call.
- The callback must be reachable from the approvals server over https, which is a new inbound path to this server.
- The notifier rebuilds the inbox from the run log every minute, as the console does on every page load.

## Reopen if

- A second phone channel is wanted that the approvals server cannot carry.
- Answers need more than one free-text field (a choice per question).
- Reading the whole run log every minute shows up in the server's CPU time.
