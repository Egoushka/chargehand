using System.Text.Json;

namespace Chargehand.Modules.Intake.Api;

/// <summary>How a tracker item id becomes a goal (ADR 0039): call <c>Tool</c> on <c>Server</c> (a key of <c>mcp_servers</c>) with <c>{ref}</c> in
/// <c>Arguments</c> replaced by the id, then read the goal's title and body from the JSON answer by dotted path.</summary>
/// <param name="Title">Dotted property names leading to the title string.</param>
/// <param name="Body">The same for the description; null for a title-only goal.</param>
public sealed record DrivenTaskSource(string Server, string Tool, IReadOnlyDictionary<string, JsonElement> Arguments, string Title, string? Body = null);
