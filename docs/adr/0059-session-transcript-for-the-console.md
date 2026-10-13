# 0059. The end of a session's transcript, read on request, for the owner and the batch's account

- Status: accepted. Extends 0039 (the runner's list of files it reads from an output volume) and 0056 (the console).
- Date: 2026-10-14

## Context

The console's task screen should show what a session did, not only how it ended. The runner reads a short list of files from a session's output volume (0039): the bundle, the report, the outcome, the usage tally, the handoff files and, since CHARGEHAND-214, the governor's log. The transcript is not on it.

The session already writes one: `stream.jsonl`, the `stream-json` output of Claude Code, one event per line, in the output volume, with the credentials the session itself held replaced by `[redacted]` (`SessionDriver.Redact`; `StreamSha256` in the outcome cites it). It is also the largest file a session leaves (tens of megabytes for a long one), and it holds what the model read and wrote: repository code, the output of every command it ran, whatever a test printed. That text can carry a secret chargehand cannot recognise, such as a password in a `.env` the model opened. The repository is public, the console page holds a key, and an account's batches are not the owner's other accounts' business.

## Options

1. **Add `stream.jsonl` to the runner's read list, whole.** One line of change. Every read moves the whole file over the runner's link (the same link a bundle uses), a client can ask for all of it at once, and the server must hold or stream it. Rejected: it is the bulk copy this decision exists to avoid.
2. **The session writes a second, redacted progress log** (messages and tool names, no tool output). Private by construction and small. But it is a new file format in the session image, which is deployed apart from the server (0039, version skew), it needs an image rebuild before anything shows, and it drops the tool output an operator reads a transcript for. Rejected for now; it is the answer if tail-reading proves too risky (see "Reopen if").
3. **The container's logs** (`GET /logs/{id}` on the runner). They hold the driver's own stderr, not the stream, and exist only while the container does. Rejected.
4. **Serve a bounded tail of `stream.jsonl`, read on request, redacted again at the server.** No new file, no image change; the data stays where the session left it.

## Decision

Option 4.

- **The runner reads the end of one named file, never the file.** `stream.jsonl` is in a new list, `OutFilesTail`, and not in `OutFilesOut`, so no batch step ever copies it. `GET /out/<run>/stream.jsonl?image=...&tail=<bytes>` runs the same no-network, read-only helper with `tail -c`; the policy refuses the name without `tail`, `tail` on any other name, an image off the allowlist and a count outside 1 to 262144 bytes. A runner that predates this refuses the call, and the server says so (`runner_too_old`).
- **The server asks for a window, 64 KiB by default and 256 KiB at most** (`?bytes=`), and makes it whole lines: the partial line the window began in is dropped, a line over 32 KiB is replaced by a stub that gives its size, and the response says whether earlier lines exist.
- **Who may read it.** `GET /v1/batches/{id}/tasks/{task}/sessions/{session}/transcript` is a batch route: the owner, and the account that started the batch, by the test every batch route uses. Another account gets `404`, as for the batch itself. A run token reaches no console route (`403`). The session must be one the task view lists, so a run id the caller invented reaches no volume.
- **Redaction at the server, on top of the session's.** `DiffScan.Redact`, the pattern list of the scanner that guards a push (AWS, GitHub, Anthropic and Slack tokens, private keys, taken from header to footer), plus the credentials this server holds that a session could have printed: the model credential of the batch's account or the server's, the push and forge tokens, the runners' keys and the owner's API key, each also in its JSON-escaped form. The response lists the kinds replaced and never a value. What no pattern and no held credential covers is not redacted, and `not_recorded` says so.
- **Nothing is kept.** A transcript is read from the output volume for each request and goes no further than the response: not into `records/`, the run log, an event, the learning file, a log line or an artifact. The response carries `Cache-Control: no-store`. It lasts as long as the output volume: the last session of a task, and an earlier one until a later one has its bundle (the transfer sweeps the rest).
- **The console shows lines as text.** They are model and tool output; the page must never render them as markup, and must not link anything in them.

## Consequences

- No change to the session image or to what a session writes; the runner and the server move together (an older runner reads as unavailable, not as an error).
- The window is a fixed cost: one helper container start per request, a few milliseconds of `tail`, at most 256 KiB over the link. A console that polls a live transcript should poll slowly; the helper shares its name with the batch's other transfers for the run, which the engine layer already retries.
- Residual risk is the unknown secret. It is bounded: the owner and the account that wrote the task are the only readers, they can already read the diff and the report, and the reader sees at most the last 256 KiB. It is not removed.
- A transcript older than its output volume is gone; a long-lived record of sessions would be a different decision with its own retention rules.

## Reopen if

A secret that no pattern or held credential covers shows up in a served transcript; a deployment needs transcripts off (a profile switch, or a smaller window); an account should not be readable by the owner; or the session image gains a structured, redacted log that makes option 2 cheap.
