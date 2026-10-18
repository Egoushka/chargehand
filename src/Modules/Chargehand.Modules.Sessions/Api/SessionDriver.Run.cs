using System.Diagnostics;
using System.Text.Json;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Sessions.Api;

public static partial class SessionDriver
{
    /// <summary>One running session: what <see cref="RunAsync"/> watches while Claude Code runs, and the outcome it writes when it ends.</summary>
    private sealed class Run(SessionOptions options, Process process, StreamWriter stream, StallDetector detector, Governor? governor, StreamWriter? governorLog, SemaphoreSlim governorGate)
    {
        private readonly SessionTask _task = options.Task;
        private readonly Tally _tally = new(options.Task.Prices);
        private readonly SessionTrace _trace = new();
        private readonly StopCause _stoppedBy = new();
        private readonly List<GovernorDecision> _decisions = [];
        private SessionUsage? _lastUsage;
        private string? _stage;
        private decimal? _spentSoFar;

        private async Task Govern(IReadOnlyList<GovernorDecision> made)
        {
            if (governorLog is null || made.Count == 0)
                return;
            await governorGate.WaitAsync(CancellationToken.None);
            try
            {
                // In enforce mode the strongest decision (a stop over a checkpoint) ends the session if nothing else has; only that one is logged as acted on.
                if (_task.Governor is { Mode: GovernorMode.Enforce } && made.Where(d => Governor.StopReason(d) is not null).OrderByDescending(d => d.Action).FirstOrDefault() is { } acting
                    && _stoppedBy.Set(SessionStatus.Stalled, Governor.StopReason(acting)!))
                {
                    made = [.. made.Select(d => d == acting ? d with { Acted = true } : d)];
                    Interrupt(process);
                }
                foreach (var d in made)
                {
                    if (_decisions.Count < MaxGovernorDecisions)
                        _decisions.Add(d);
                    await governorLog.WriteLineAsync(Redact(JsonSerializer.Serialize(d, LineJson.GovernorDecision), options.Secrets));
                }
                await governorLog.FlushAsync(CancellationToken.None);
            }
            finally
            {
                governorGate.Release();
            }
        }

        public async Task ReadAsync(CancellationToken ct)
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                await stream.WriteLineAsync(Redact(line, options.Secrets));
                await stream.FlushAsync(ct);
                JsonElement e;
                try { e = JsonDocument.Parse(line).RootElement; }
                catch (JsonException) { continue; }
                var now = DateTimeOffset.UtcNow;
                detector.OnEvent(e, now);
                _tally.Add(e);
                _trace.OnEvent(e);
                _stage = Adherence.StageOf(e) ?? _stage;
                if (governor is not null)
                {
                    IReadOnlyList<GovernorDecision> made;
                    lock (governor)
                    {
                        _spentSoFar = _tally.UsdEstimate;
                        made = governor.OnEvent(e, detector.Turns, _spentSoFar, now);
                    }
                    await Govern(made);
                }
                WriteUsage(options.OutDirectory, new SessionUsage(_tally.Input + _tally.Output, detector.Turns, _stage, _tally.CacheRead, _tally.CacheWrite, _tally.UsdEstimate, _tally.UsdEstimatePartial), ref _lastUsage);
                if (_task.MaxTokens is { } cap && _tally.Input + _tally.Output >= cap && _stoppedBy.Set(SessionStatus.TokenCap, "token_cap"))
                    Interrupt(process);
            }
        }

        /// <returns>The diff measure and the bundle refresh still running when the session exited, if any (combined into one task).</returns>
        public async Task<Task?> WatchAsync(Task exit, string? start, DateTimeOffset started, CancellationToken ct)
        {
            using var watch = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            Task? measuring = null;
            var measuredAt = (Turn: 0, At: started);
            var bundle = new BundleState(started, null, null);
            while (!exit.IsCompleted)
            {
                var tick = watch.WaitForNextTickAsync(ct).AsTask();
                await Task.WhenAny(exit, tick);
                if (exit.IsCompleted)
                    break;
                if (governor is not null && start is not null && measuring is not { IsCompleted: false } && detector.Turns > measuredAt.Turn
                    && DateTimeOffset.UtcNow - measuredAt.At >= DiffEvery)
                {
                    measuredAt = (detector.Turns, DateTimeOffset.UtcNow);
                    var turn = measuredAt.Turn;
                    measuring = MeasureAsync(options.WorkDirectory, start, lines =>
                    {
                        lock (governor)
                            return governor.OnDiff(lines, turn, _spentSoFar, DateTimeOffset.UtcNow);
                    }, Govern, ct);
                }
                bundle = await RefreshBundleAsync(options, bundle, ct);
                if (detector.Check(DateTimeOffset.UtcNow) is { } stall && _stoppedBy.Set(SessionStatus.Stalled, StallDetector.Wire(stall)))
                    Interrupt(process);
                if (_stoppedBy.Cause is not null && !exit.IsCompleted && DateTimeOffset.UtcNow - _stoppedBy.At > Grace)
                    process.Kill(entireProcessTree: true);
            }
            return measuring is null ? bundle.Bundling : bundle.Bundling is null ? measuring : Task.WhenAll(measuring, bundle.Bundling);
        }

        /// <summary>What <see cref="RefreshBundleAsync"/> carries between polls: when it last checked the branch, the head it last bundled, and the
        /// bundling task still running, if any.</summary>
        private readonly record struct BundleState(DateTimeOffset CheckedAt, string? Head, Task? Bundling);

        /// <summary>Bundles the branch again when it has moved since the last bundle, no more often than <see cref="BundleCheckEvery"/>, and only once
        /// the previous bundling finished: a session's own review call reads this bundle while the session still runs (ADR 0062).</summary>
        private static async Task<BundleState> RefreshBundleAsync(SessionOptions options, BundleState state, CancellationToken ct)
        {
            if (state.Bundling is { IsCompleted: false } || DateTimeOffset.UtcNow - state.CheckedAt < BundleCheckEvery)
                return state;
            var head = await BranchHeadAsync(options, ct);
            return string.IsNullOrEmpty(head) || head == state.Head
                ? state with { CheckedAt = DateTimeOffset.UtcNow }
                : new BundleState(DateTimeOffset.UtcNow, head, BundleAsync(options, ct));
        }

        public async Task<SessionOutcome> FinishAsync(string? start, CancellationToken ct)
        {
            string? governorHash = null;
            if (governorLog is not null)
            {
                await governorLog.DisposeAsync();
                governorHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(options.OutDirectory, GovernorFile), ct)));
            }

            var snapshotNote = _stoppedBy.Cause is { } stopped ? await SnapshotAsync(options, stopped.Reason, detector.Turns, ct) : null;
            if (_stoppedBy.Cause is { } stoppedFor)
                await WriteHandoffAsync(options, start, stoppedFor.Reason, detector.Turns, _trace, ct);
            var bundle = await BundleAsync(options, ct);
            var commits = bundle && start is not null ? await CommitsAsync(options, start, ct) : 0;
            var (report, questions) = Extract(_tally);
            if (report is not null)
                await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "driven-report.json"), Redact(report, options.Secrets), ct);
            var (status, reason) = Conclude(_stoppedBy, _tally, questions);
            await stream.DisposeAsync();
            var streamHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(options.OutDirectory, "stream.jsonl"), ct)));
            var outcome = new SessionOutcome(status, reason, detector.Turns, _tally.Input, _tally.Output, _tally.CacheRead, _tally.CostUsd, process.ExitCode,
                [.. questions.Select(q => Redact(q, options.Secrets))], bundle, streamHash, _tally.CacheWrite, _tally.SessionId, _tally.Model, _tally.ModelUsage.Count == 0 ? null : _tally.ModelUsage,
                _tally.UsdEstimate, status == SessionStatus.RateLimited ? UsageLimit.ResetsAt(_tally.ResultText) : null, _tally.UsdEstimatePartial, commits, snapshotNote,
                governor is null ? null : _decisions, governorHash);
            await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "session-outcome.json"), Redact(JsonSerializer.Serialize(outcome, OutcomeJson.SessionOutcome), options.Secrets), ct);
            return outcome;
        }
    }
}
