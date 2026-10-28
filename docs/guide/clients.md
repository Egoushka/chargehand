---
title: "Connect your editor or agent"
description: "Per-client MCP configuration for chargehand: Claude Code, Claude Desktop, Cursor, VS Code, Visual Studio, JetBrains AI Assistant, Junie, Codex, Gemini CLI, Warp, Zed and Android Studio, why ChatGPT cannot connect yet, and which snippets were run."
order: 4
section: "Guides"
---

chargehand speaks MCP (the Model Context Protocol, the way an editor or agent calls tools on another program). A client that connects can call chargehand's three tools: `orchestrate` asks a codebase question, `result` reads a run that is still going, and `repositories` lists the checkouts the server reads ([the MCP page](mcp.md) covers the tools).

There are two ways to connect, and each client section says which it takes:

- **HTTP.** The client calls a running `chargehand serve` at `https://chargehand.example.com/v1/mcp` (Streamable HTTP, the MCP transport over a plain HTTPS request) and sends `Authorization: Bearer <your-key>`. The key is the one [the HTTP server](server.md#the-bearer-key) checks. This is the form most sections show.
- **stdio.** The client starts `dnx Chargehand@<version> --yes -- mcp` itself and talks to it over standard input and output. It needs the .NET 10 SDK and a Claude Code credential ([the MCP page](mcp.md#from-the-package) lists both). A client that cannot send a header takes this form.

Replace `https://chargehand.example.com/v1/mcp` with your server's address, `<your-key>` with your key and `<version>` with a version of the `Chargehand` package on nuget.org. A key written into a config file is stored in plain text, so keep that file out of version control; the sections use the client's variable syntax where it has one.

## What was run

Each snippet carries one of two labels, and each section starts with one.

- **Run** means the snippet was used on 2026-10-28 on a Mac against `chargehand serve` 0.19.4 on the loopback address with a throwaway key. The label says how far: "connected" means the client started, authenticated and listed the tools; "called" means it also called `repositories` and got an answer.
- **Vendor docs** means the snippet follows the vendor's page, listed under [Sources](#sources), and was not started here. Read it as the vendor's claim, adapted to chargehand's endpoint.

| Client | Takes | Label |
|---|---|---|
| [Claude Code](#claude-code) | HTTP, stdio | run, called |
| [Claude Desktop](#claude-desktop) | stdio; HTTP through the `mcp-remote` bridge | vendor docs |
| [Cursor](#cursor) | HTTP, stdio | vendor docs |
| [VS Code](#vs-code) | HTTP, stdio, and the plugin | vendor docs; the CLI form run |
| [Visual Studio](#visual-studio) | HTTP, stdio | vendor docs, one field unverified |
| [JetBrains AI Assistant](#jetbrains-ai-assistant) | stdio; HTTP without a header | vendor docs |
| [Junie](#junie) | HTTP, stdio | vendor docs |
| [Codex](#codex) | HTTP, stdio | run, connected |
| [Gemini CLI](#gemini-cli) | HTTP, stdio | run, connected |
| [Warp](#warp) | HTTP, stdio | vendor docs |
| [Zed](#zed) | HTTP, stdio | vendor docs |
| [Android Studio](#android-studio) | HTTP only | vendor docs |
| [ChatGPT](#chatgpt) | neither, today | vendor docs |

## Claude Code

**Run** (Claude Code 2.1.303, called): add the server from the shell.

```bash
claude mcp add --transport http chargehand https://chargehand.example.com/v1/mcp --header "Authorization: Bearer <your-key>"
```

The command stores the entry in `~/.claude.json` for the current project (the default `local` scope); `--scope user` makes it available in every project and `--scope project` writes a `.mcp.json` you can commit (the run used this scope, in a scratch directory). The header value is stored as given.

**Run** (Claude Code 2.1.303, called): a project `.mcp.json` that reads the key from an environment variable, so the file can be committed. Claude Code expands `${VAR}` in `url` and `headers`.

```json
{
  "mcpServers": {
    "chargehand": {
      "type": "http",
      "url": "https://chargehand.example.com/v1/mcp",
      "headers": { "Authorization": "Bearer ${CHARGEHAND_API_KEY}" }
    }
  }
}
```

Check it with `claude mcp list`, which shows `✔ Connected` per server, or `/mcp` inside a session. Claude Code asks you to approve a server that comes from a project `.mcp.json` before it connects. The stdio form is [on the MCP page](mcp.md#over-stdio-from-a-checkout).

The same checkout holds a Claude Code plugin with two commands, `change` and `run` ([the change command](change.md)). **Vendor docs** (`claude plugin install --help`): add the marketplace, then install the plugin; it starts the stdio form for you.

```bash
claude plugin marketplace add Egoushka/chargehand && claude plugin install chargehand@chargehand
```

## Claude Desktop

**Vendor docs** (modelcontextprotocol.io, "Connect to local servers"): the stdio package, in `claude_desktop_config.json` (macOS `~/Library/Application Support/Claude/`, Windows `%APPDATA%\Claude\`). Quit and restart Claude Desktop after editing it. If the app does not see your shell's `PATH`, give the full path to `dnx`.

```json
{
  "mcpServers": {
    "chargehand": {
      "command": "dnx",
      "args": ["Chargehand@<version>", "--yes", "--", "mcp"],
      "env": { "CLAUDE_CODE_OAUTH_TOKEN": "<token>" }
    }
  }
}
```

Settings, Connectors, "Add custom connector" takes a URL and then follows the server's own sign-in prompts, OAuth being the common case. chargehand has a static key and no sign-in flow, and the vendor page describes no way to send a key from that screen: UNKNOWN whether a form for one appears. To reach a server over HTTP without it, put the `mcp-remote` bridge (a community npm package, Node 18 or later) between them. **Vendor docs** (the `mcp-remote` README): the header value goes in `env`, which also avoids a bug with spaces in `args` on Windows.

```json
{
  "mcpServers": {
    "chargehand": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://chargehand.example.com/v1/mcp", "--header", "Authorization:${CHARGEHAND_AUTH}"],
      "env": { "CHARGEHAND_AUTH": "Bearer <your-key>" }
    }
  }
}
```

Quit Claude Desktop before you edit the file, so the app does not overwrite your change.

## Cursor

**Vendor docs** (Cursor, "Model Context Protocol"): `.cursor/mcp.json` in the project, or `~/.cursor/mcp.json` for every project. Cursor reads `${env:NAME}` in `url` and `headers`.

```json
{
  "mcpServers": {
    "chargehand": {
      "url": "https://chargehand.example.com/v1/mcp",
      "headers": { "Authorization": "Bearer ${env:CHARGEHAND_API_KEY}" }
    }
  }
}
```

Cursor supports stdio, SSE and Streamable HTTP. For stdio use the same `command`, `args` and `env` fields as in the Claude Desktop snippet above.

## VS Code

**Vendor docs** (VS Code, "MCP configuration reference"): `.vscode/mcp.json` in the workspace, or the user profile's `mcp.json`. VS Code asks for the key once, hides what you type, stores it and fills `${input:chargehand-key}`.

```json
{
  "inputs": [
    { "type": "promptString", "id": "chargehand-key", "description": "chargehand API key", "password": true }
  ],
  "servers": {
    "chargehand": {
      "type": "http",
      "url": "https://chargehand.example.com/v1/mcp",
      "headers": { "Authorization": "Bearer ${input:chargehand-key}" }
    }
  }
}
```

**Run** (VS Code 1.143.1, in a scratch profile; written, not started): `code --add-mcp` adds the same entry to the user profile's `mcp.json`. It wrote the `servers` shape above with `"type": "http"`. VS Code did not start the server here, because MCP in VS Code needs a signed-in GitHub Copilot account.

```bash
code --add-mcp '{"name":"chargehand","type":"http","url":"https://chargehand.example.com/v1/mcp","headers":{"Authorization":"Bearer <your-key>"}}'
```

### As an agent plugin

VS Code agent plugins read the Claude plugin layout, so the `plugins/chargehand` directory that Claude Code installs is also a VS Code plugin: `.claude-plugin/plugin.json`, `.mcp.json` and `skills/`. **Vendor docs** (VS Code, "Agent plugins"): register this repository as a marketplace in your user settings, then open the Extensions view, search `@agentPlugins` and install `chargehand`.

```json
{
  "chat.plugins.marketplaces": ["Egoushka/chargehand"]
}
```

The plugin gives VS Code the stdio entry (`dnx`, with `CHARGEHAND_RUNTIME` set to `claude_code`, so a `claude` command must be on `PATH` and signed in) and the skills `change` and `run`. Those two are written for Claude Code: `run` allows Claude Code tools such as `Monitor`, so whether VS Code runs them is UNKNOWN. `PluginFormatsTests` checks the manifest against the rules VS Code documents for that layout (the name, skill names that match their directories, `.mcp.json` entries). The plugin was not installed in a running VS Code. A root `plugin.json` is left out on purpose: one that declares the Agent Plugins 1.0 `$schema` makes VS Code read `mcp.json` and not `.mcp.json`.

## Visual Studio

**Vendor docs** (Microsoft Learn, "Use MCP servers", Visual Studio 2026, or 2022 from 17.14): `<SOLUTIONDIR>\.mcp.json` for a file you can commit, or `%USERPROFILE%\.mcp.json` for every solution. Visual Studio also reads `.vscode/mcp.json` and `.cursor/mcp.json`. Edit the file inside Visual Studio so its JSON schema applies.

```json
{
  "servers": {
    "chargehand": {
      "url": "https://chargehand.example.com/v1/mcp",
      "headers": { "Authorization": "Bearer <your-key>" }
    }
  }
}
```

UNVERIFIED: Microsoft's page shows `url` alone and offers OAuth sign-in for remote servers; it does not show `headers`. The field is the one VS Code's file uses and Visual Studio reads that file, but the page does not say so. If the server's log shows `401`, Visual Studio dropped the header: use stdio instead. The chat pane's tool picker, plus (`+`), "Add custom MCP server" takes a URL or a command and arguments without a file.

## JetBrains AI Assistant

**Vendor docs** (JetBrains, "Model Context Protocol (MCP)"): Settings, Tools, AI Assistant, Model Context Protocol (MCP), then Add, and paste JSON. The page documents `command` and `args` for stdio, and `url` for a remote server; it shows no `headers` and no `env`. So HTTP with a key is not documented, and the stdio package is the form to use.

```json
{
  "mcpServers": {
    "chargehand": {
      "command": "dnx",
      "args": ["Chargehand@<version>", "--yes", "--", "mcp"]
    }
  }
}
```

Without an `env` field, `dnx` and the `claude` command it starts must find their credentials in the environment the IDE was started from: UNKNOWN whether a Dock- or menu-launched IDE passes them. "Import from Claude" copies the servers of a Claude Desktop config. Streamable HTTP and SSE are supported for servers that need no header.

## Junie

**Vendor docs** (Junie CLI, "MCP configuration"): `~/.junie/mcp/mcp.json` for every project, or `.junie/mcp/mcp.json` in a project. In a JetBrains IDE, the Junie plugin's MCP Settings page has an Add button that opens an `mcp.json` for editing; its page does not name the path, so check that it is the file above.

```json
{
  "mcpServers": {
    "chargehand": {
      "url": "https://chargehand.example.com/v1/mcp",
      "headers": { "Authorization": "Bearer <your-key>" }
    }
  }
}
```

The page does not describe variable expansion, so the key sits in the file. Keep `.junie/mcp/mcp.json` out of version control, or use the user file. A local server takes `command`, `args` and `env` as in the Cursor entry above.

## Codex

**Run** (Codex 0.149.1, connected): `codex mcp add` wrote the table below to `~/.codex/config.toml` (an isolated `CODEX_HOME` here). The key is read from the environment variable named by `bearer_token_env_var`, so no key is stored.

```bash
export CHARGEHAND_API_KEY=<your-key>
codex mcp add chargehand --url https://chargehand.example.com/v1/mcp --bearer-token-env-var CHARGEHAND_API_KEY
```

**Run** (Codex 0.149.1, connected): the file form, also valid in a trusted project's `.codex/config.toml`.

```toml
[mcp_servers.chargehand]
url = "https://chargehand.example.com/v1/mcp"
bearer_token_env_var = "CHARGEHAND_API_KEY"
```

`codex mcp list` shows the server with its key variable and an authentication of bearer type, and `/mcp` inside the TUI lists the active servers. Codex started, sent the key, and listed chargehand's tools. It did not call `repositories`: the Codex sign-in on this Mac had expired, so the model call failed before any tool call.

**Vendor docs** (Codex, "Model Context Protocol"): the stdio form.

```toml
[mcp_servers.chargehand]
command = "dnx"
args = ["Chargehand@<version>", "--yes", "--", "mcp"]
```

## Gemini CLI

**Run** (Gemini CLI 0.46.0, connected): `~/.gemini/settings.json` for every project, or `.gemini/settings.json` in a project. Gemini CLI expands `$VAR` and `${VAR}` in these values, and an unset variable becomes an empty string, so a wrong or missing key shows the server as disconnected. `httpUrl` is the Streamable HTTP field; `url` alone means SSE.

```json
{
  "mcpServers": {
    "chargehand": {
      "httpUrl": "https://chargehand.example.com/v1/mcp",
      "headers": { "Authorization": "Bearer $CHARGEHAND_API_KEY" }
    }
  }
}
```

**Run** (Gemini CLI 0.46.0, connected): the same entry from the shell. `--scope` defaults to `project`; pass `--scope user` for a user-wide entry. This version wrote `"url"` with `"type": "http"` where the vendor page says `httpUrl`; both connected.

```bash
gemini mcp add --transport http --header "Authorization: Bearer <your-key>" chargehand https://chargehand.example.com/v1/mcp
```

`gemini mcp list` shows each server as connected or not. A project's own settings load only in a trusted folder (`gemini trust`); in an untrusted one the server was listed as disabled. No model call was made: this Mac has no Gemini sign-in.

## Warp

**Vendor docs** (Warp, "Model Context Protocol"): Settings, Agents, MCP servers, then Add, and paste JSON keyed by server name. Warp also reads `~/.warp/.mcp.json` and, per project, `.warp/.mcp.json`. A URL server takes Streamable HTTP or SSE.

```json
{
  "chargehand": {
    "url": "https://chargehand.example.com/v1/mcp",
    "headers": { "Authorization": "Bearer <your-key>" }
  }
}
```

A server started without credentials opens Warp's OAuth flow; chargehand serves none, so always set the header. A local server takes `command`, `args`, `env` and an optional `working_directory`.

## Zed

**Vendor docs** (Zed, "Model Context Protocol"): `context_servers` in `settings.json` (Settings, AI, MCP Servers shows each server's status). A server with no `Authorization` header starts Zed's OAuth flow, so set the header.

```json
{
  "context_servers": {
    "chargehand": {
      "url": "https://chargehand.example.com/v1/mcp",
      "headers": { "Authorization": "Bearer <your-key>" }
    }
  }
}
```

A local server takes `command`, `args` and `env` in the same place.

## Android Studio

**Vendor docs** (Android Developers, "Add MCP servers"): Settings (Android Studio, Settings on macOS), Tools, AI, MCP Servers, tick Enable MCP Servers, paste the JSON, press OK. Android Studio supports Streamable HTTP through `httpUrl` and does not support stdio. Its page does not name a minimum version. `url` would mean SSE.

```json
{
  "mcpServers": {
    "chargehand": {
      "httpUrl": "https://chargehand.example.com/v1/mcp",
      "headers": { "Authorization": "Bearer <your-key>" }
    }
  }
}
```

## ChatGPT

**Vendor docs** (OpenAI, "Authentication" in the Apps SDK): ChatGPT cannot connect to chargehand today. It authenticates to an MCP server with OAuth 2.1 or reaches it anonymously; OpenAI's page says it cannot present custom API keys, and `/v1/mcp` accepts only chargehand's bearer keys. chargehand has no unauthenticated mode and should not get one: anyone who could reach it could start runs on your model budget.

What would change this is an OAuth 2.1 sign-in in front of `/v1/mcp` that ChatGPT can use. chargehand does not have one ([issue 621](https://github.com/Egoushka/chargehand/issues/621)). OpenAI says account and workspace policies apply to adding a custom server, and its pages do not say which plans may add one; the help article that might was not readable on 2026-10-28: UNKNOWN.

## When it does not connect

- `401` in the client or in the server's log: the key is wrong, revoked or missing. A client that expands a variable gets an empty string when the variable is unset.
- A client that lists no tools right after you add the server: restart it, or reload its MCP list. Claude Desktop needs a full quit and start.
- A long run: `orchestrate` returns a run id after about 50 seconds over HTTP; call `result` with it ([the run id before a client timeout](mcp.md#the-run-id-before-a-client-timeout)).

## What was checked, and how

On 2026-10-28 `chargehand serve` 0.19.4 ran on the loopback address with a throwaway key and the `claude_code` runtime.

- **Claude Code 2.1.303.** `claude mcp add --transport http` wrote the entry shown, and a headless `claude -p` call with the `.mcp.json` above (key in `CHARGEHAND_API_KEY`) called `repositories`; the server logged a `tools/call` from `claude-code 2.1.303`. `claude mcp list` showed the project entry as pending approval, as the vendor page says it would.
- **Codex 0.149.1.** `codex mcp add --url … --bearer-token-env-var …` wrote the TOML shown, and `codex mcp list` and `get` read it back. `codex exec` connected and listed the tools (the server logged `initialize` and `tools/list` from `codex-mcp-client 0.149.1`), then stopped on Codex's own expired sign-in.
- **Gemini CLI 0.46.0.** Both forms showed `Connected` in `gemini mcp list`, and with `CHARGEHAND_API_KEY` set to a wrong value the JSON form showed `Disconnected`.
- **VS Code 1.143.1.** `code --add-mcp` wrote the `servers` entry, in a scratch profile.
- **Not run:** Claude Desktop, Cursor, Visual Studio, JetBrains AI Assistant, Junie, Warp, Zed and Android Studio, and the VS Code plugin install. The apps are not on this Mac or need an account the check did not have.

`ClientsGuideTests` parses every JSON and TOML snippet in this page and checks that each carries a label and no private address, and `PluginFormatsTests` checks the plugin manifest.

## Sources

Each page was read on 2026-10-28.

- Claude Code: [MCP](https://code.claude.com/docs/en/mcp).
- Claude Desktop: [local servers](https://modelcontextprotocol.io/docs/develop/connect-local-servers), [remote servers](https://modelcontextprotocol.io/docs/develop/connect-remote-servers), [mcp-remote](https://github.com/geelen/mcp-remote).
- Cursor: [MCP](https://cursor.com/docs/context/mcp).
- VS Code: [MCP configuration](https://code.visualstudio.com/docs/agents/reference/mcp-configuration), [agent plugins](https://code.visualstudio.com/docs/copilot/customization/agent-plugins), [plugin reference](https://code.visualstudio.com/docs/agents/reference/agent-plugin-reference), [agent skills](https://code.visualstudio.com/docs/agent-customization/agent-skills).
- Visual Studio: [MCP servers](https://learn.microsoft.com/en-us/visualstudio/ide/mcp-servers).
- JetBrains AI Assistant: [MCP](https://www.jetbrains.com/help/ai-assistant/mcp.html).
- Junie: [CLI MCP configuration](https://junie.jetbrains.com/docs/junie-cli-mcp-configuration.html), [plugin MCP settings](https://junie.jetbrains.com/docs/junie-plugin-mcp-settings.html).
- Codex: [MCP](https://learn.chatgpt.com/docs/extend/mcp?surface=cli).
- Gemini CLI: [MCP servers](https://raw.githubusercontent.com/google-gemini/gemini-cli/main/docs/tools/mcp-server.md).
- Warp: [MCP](https://docs.warp.dev/agents/capabilities/mcp.md).
- Zed: [MCP](https://zed.dev/docs/ai/mcp).
- Android Studio: [Add MCP servers](https://developer.android.com/studio/gemini/add-mcp-server).
- ChatGPT: [authentication](https://developers.openai.com/apps-sdk/build/auth), [connect from ChatGPT](https://developers.openai.com/apps-sdk/deploy/connect-chatgpt).
