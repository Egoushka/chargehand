# AgentClientProtocol.Net

A client for the [Agent Client Protocol](https://agentclientprotocol.com) (ACP) in .NET. It starts a coding agent as a child
process (Codex through `codex-acp`, Gemini CLI with `--acp`, Claude Code through `claude-agent-acp`, Goose, Cline and the
other agents in the ACP registry) and talks to it over standard input and output.

- Protocol version 1, client side: `initialize`, `authenticate`, `session/new`, `session/load`, `session/prompt`,
  `session/cancel`, `session/set_mode`, `session/set_config_option`, `session/set_model`, `session/update` notifications,
  `session/request_permission`, and the agent's `fs/*` and `terminal/*` calls.
- Native AOT clean: source-generated `System.Text.Json`, no reflection, no other dependencies.
- An agent that dies mid-turn fails the request in flight with `AcpConnectionException` (exit code and the tail of its
  standard error included). It never hangs a caller that holds a deadline.
- Types a newer agent adds still parse: a session update of an unknown kind arrives as `UnknownSessionUpdate`, and stop
  reasons, tool kinds and statuses are strings with constants.

```csharp
using AgentClientProtocol;

await using var client = AcpClient.Launch(
    new AcpLaunchOptions { FileName = "npx", Arguments = ["-y", "@google/gemini-cli@0.66.0", "--acp"] },
    new AcpClientOptions
    {
        OnSessionUpdate = (n, _) =>
        {
            if (n.Update is AgentMessageChunk { Content.Text: { } text })
                Console.Write(text);
            return ValueTask.CompletedTask;
        },
        // Answers session/request_permission. With no callback every request is cancelled, which an agent treats as a refusal.
        OnRequestPermission = (request, _) => ValueTask.FromResult(
            RequestPermissionOutcome.Selected(request.Options.First(o => o.Kind == AcpPermissionKind.RejectOnce).OptionId)),
    });

await client.InitializeAsync();
var session = await client.NewSessionAsync(Directory.GetCurrentDirectory());
var result = await session.PromptAsync("Where is the retry loop in this repository?");
Console.WriteLine($"[{result.StopReason}]");
```

## What it does for you

- **One turn at a time per session.** A prompt sent while a turn runs waits for that turn to end and then goes out, in the order
  it was called (`AcpSession.QueuedPrompts` counts them).
- **Cancel.** `AcpSession.CancelAsync()` sends `session/cancel`. Cancelling the token given to `PromptAsync` does the same and
  waits up to `AcpClientOptions.CancelGrace` for the agent to end the turn with `cancelled`.
- **Capabilities follow the callbacks.** The client offers `fs/read_text_file`, `fs/write_text_file` and `terminal/*` only when
  the matching callback is set (`OnReadTextFile`, `OnWriteTextFile`, `Terminal`). A call it did not offer is answered with
  `-32601`.
- **Transcripts.** `AcpClientOptions.OnWire` sees every line in both directions, including text an agent prints on standard
  output that is not JSON-RPC, which the client otherwise skips.
- **Your own transport.** `AcpClient.Connect(fromAgent, toAgent, options)` takes two streams instead of starting a process.

## Not here yet

The agent side (writing an ACP agent in .NET), `session/resume`, `session/fork`, `session/list` and the other unstable
methods.
