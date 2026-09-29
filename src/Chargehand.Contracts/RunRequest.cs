namespace Chargehand.Contracts;

/// <summary>request/v1: what a caller sends to start a run.</summary>
public sealed record RunRequest(
    string ContractVersion,
    string Text,
    RequestContext Context,
    IReadOnlyList<CallerInput>? Inputs = null,
    IReadOnlyList<PromptBlock>? CallerBlocks = null);

/// <param name="Approved">Runs a request the preset would otherwise stop for approval (preset approval thresholds).</param>
public sealed record RequestContext(bool Interactive, string Preset, decimal? BudgetUsd = null, RepositoryRef? Repository = null, bool? Approved = null, IReadOnlyList<string>? Verify = null);

public sealed record RepositoryRef(string Path, string Commit);

/// <summary>A caller-supplied fact; claims cite it with evidence kind <c>input</c>.</summary>
public sealed record CallerInput(string Id, string Kind, string Text, string? SourceUrl = null);

/// <summary>A versioned, content-hashed prompt block (registry-owned or caller-owned).</summary>
public sealed record PromptBlock(string Name, string Version, string Sha256, string Text)
{
    /// <summary>A block whose sha256 follows request/v1's rule, so a caller needs nothing else to send one.</summary>
    public static PromptBlock Create(string name, string version, string text) => new(name, version, Hash(text), text);

    /// <summary>LF line endings, trailing whitespace stripped from every line.</summary>
    public static string Normalize(string text) =>
        string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimEnd()));

    /// <summary>sha256 of the normalised UTF-8 text, lowercase hex.</summary>
    public static string Hash(string text) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Normalize(text))));
}
