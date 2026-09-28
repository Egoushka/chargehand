#!/bin/sh
# Claude Code PreToolUse hook (.claude/settings.json): asks before an edit under a published schema major,
# schemas/<name>/v<N>/. Published majors take additive changes only; a breaking change is a new major (CONTRIBUTING.md).
command -v jq > /dev/null || exit 0
path=$(jq -r '.tool_input.file_path // empty' | tr '\\' '/')
case "$path" in
  *schemas/*/v[0-9]*/*)
    printf '%s\n' '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"Published schema major: only additive changes (new optional fields). A breaking change goes into a new major directory (CONTRIBUTING.md)."}}' ;;
esac
exit 0
