using System.Text.Json.Serialization;
using Chargehand.Modules.Tasks.Api;

namespace Chargehand.Modules.Research.Api;

/// <summary>The inline artifacts of a task's result without reflection (ADR 0060), written byte for byte as the anonymous types before them: snake_case, in declaration order,
/// nulls kept.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(ReviewArtifact))]
[JsonSerializable(typeof(KeptWork))]
[JsonSerializable(typeof(BranchArtifact))]
[JsonSerializable(typeof(PullRequestArtifact))]
[JsonSerializable(typeof(VerificationArtifact))]
[JsonSerializable(typeof(MutationArtifact))]
[JsonSerializable(typeof(SessionTestsArtifact))]
[JsonSerializable(typeof(ChecksArtifact))]
[JsonSerializable(typeof(ChangesArtifact))]
internal sealed partial class TaskArtifactsJsonContext : JsonSerializerContext;

internal sealed record ReviewArtifact(IEnumerable<ReviewRun> Runs);

internal sealed record ReviewRun(string RunId, string Preset, string Status);

internal sealed record BranchArtifact(string? Branch, string? Commit, string Base, bool Pushed);

internal sealed record PullRequestArtifact(string Url, int Number, bool Draft, string? Head);

internal sealed record VerificationArtifact(IReadOnlyList<string> Argv, string Source, string Sandbox, bool Network, int ExitCode, bool Passed, bool TimedOut, int Attempts,
    double DurationSeconds, string OutputTail, IReadOnlyList<string> ChangedVerificationPaths, TestIntegrityArtifact? TestIntegrity, BlindTestsArtifact? BlindTests);

internal sealed record TestIntegrityArtifact(bool Flagged, string Reason, IReadOnlyList<string> TestPaths, IReadOnlyList<string> BuildPaths);

internal sealed record BlindTestsArtifact(int Count, int Failed, IReadOnlyList<string> Names, IReadOnlyList<string> Uncompilable);

/// <summary>The <c>mutation</c> artifact: the share of mutants of the changed lines the tests killed, against the share required; <c>not_run</c> says why the tool could not run.</summary>
internal sealed record MutationArtifact(bool Ran, double? Score, double Threshold, bool Passed, int? Survivors, int? Killed, string? NotRun);

internal sealed record SessionTestsArtifact(string ClaimedBy, string Command, int? ExitCode);

/// <summary>The <c>checks</c> artifact: the last gate round of each repository rule and acceptance criterion, run by the session driver in the session's working tree.</summary>
internal sealed record ChecksArtifact(bool Passed, int Attempts, IReadOnlyList<CheckArtifact> Checks, bool OutputTruncated);

internal sealed record CheckArtifact(string Kind, string Command, bool Passed, int? ExitCode, int ExpectedExitCode, string? ExpectedOutput, string? OutputTail);

internal sealed record ChangesArtifact(string Base, string? Commit, int FilesTotal, IEnumerable<ChangedFile> Files, string? Diff, bool DiffTruncated);

internal sealed record ChangedFile(string Path, int? Added, int? Removed, bool Binary);
