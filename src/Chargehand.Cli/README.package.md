# chargehand

<!-- mcp-name: io.github.Egoushka/chargehand -->

An MCP server that runs coding-agent workers (Claude Code or OpenCode) on a question about a codebase and returns
`result/v1`: an answer whose claims each carry evidence, with a confidence and the questions that stayed open.
Workers are read-only.

You need the .NET 10 SDK and one agent CLI on `PATH`. For Claude Code, sign in with `claude` once, or set one of
`ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`). A profile (`CHARGEHAND_PROFILE`) is optional: without one, workers run on the agent CLI's default model.

```bash
claude mcp add chargehand -- dnx Chargehand@<version> --yes -- mcp
```

The tool also runs as a CLI: `dnx Chargehand@<version> --yes -- run < request.json`.

Source, documentation and the other client configurations: https://github.com/Egoushka/chargehand
