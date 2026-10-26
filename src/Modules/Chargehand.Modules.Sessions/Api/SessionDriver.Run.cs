using System.Diagnostics;
using System.Text.Json;
using Chargehand.Modules.Oversight.Api;

namespace Chargehand.Modules.Sessions.Api;

public static partial class SessionDriver
{
    /// <summary>The goal reminder of a session, the file the hook claims and the text it carries.</summary>
    private sealed record Reminding(GoalReminder Gate, string File, string Text);

    /// <summary>One running session: what <see cref="RunAsync"/> watches while Claude Code runs (the first process, then one per check-gate round, each resuming the session), and
    /// the outcome it writes when it ends.</summary>
    private sealed class Run(SessionOptions options, StreamWriter stream, StallDetector detector, Governor? governor, StreamWriter? governorLog, SemaphoreSlim governorGate,
        Reminding? reminder)
    {
        private Process _process = null!;
        private int? _exitCode;
        private bool _checksFailed;
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
                    Interrupt(_process);
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

        /// <summary>Starts one Claude Code process and watches it until it exits.</summary>
        public async Task DriveAsync(ProcessStartInfo psi, string? start, DateTimeOffset started, CancellationToken ct)
        {
            using var current = Process.Start(psi) ?? throw new InvalidOperationException("could not start Claude Code");
            _process = current;
            _process.StandardInput.Close();
            var stderr = _process.StandardError.ReadToEndAsync(ct);
            var reader = Task.Run(() => ReadAsync(ct), ct);
            var exit = _process.WaitForExitAsync(ct);
            var measuring = await WatchAsync(exit, start, started, ct);
            await exit;
            await reader;
            _ = await stderr;
            if (measuring is not null)
                await measuring;
            _exitCode = _process.ExitCode;
        }

        /// <summary>The check gate: while the session finished on its own, run the repository's checks and the task's criteria; a failure resumes the session with the
        /// failure as its next turn, at most <see cref="SessionTask.CheckRetries"/> times and within the wall clock.</summary>
        /// <returns>The last round's results; null when none ran.</returns>
        public async Task<IReadOnlyList<CheckResult>?> GateAsync(IReadOnlyList<string> rules, string? start, Func<(string SessionId, string Prompt), ProcessStartInfo> resume,
            CancellationToken ct)
        {
            if (rules.Count == 0 && _task.Acceptance is not { Count: > 0 })
                return null;
            IReadOnlyList<CheckResult>? results = null;
            for (var attempt = 1; Finished(); attempt++)
            {
                results = await SessionChecks.RunAllAsync(rules, _task.Acceptance, options.WorkDirectory, attempt, options.Secrets, ct);
                if (results.All(r => r.Passed))
                    return results;
                if (attempt > _task.CheckRetries || _tally.SessionId is not { } session || detector.Check(DateTimeOffset.UtcNow) is StallReason.WallClock)
                {
                    _checksFailed = true;
                    return results;
                }
                // The checks' own time is not the session's silence.
                detector.Touch(DateTimeOffset.UtcNow);
                _tally.NewProcess();
                await DriveAsync(resume((session, SessionChecks.Feedback(results, attempt, _task.CheckRetries))), start, DateTimeOffset.UtcNow, ct);
            }
            // A resumed session that did not finish keeps its own status.
            return results;
        }

        /// <summary>The one extra turn for a session that finished on its own without a usable report: it asks for the report and nothing else, so the branch is the one the checks passed.</summary>
        public async Task ReportAsync(string? start, Func<(string SessionId, string Prompt), ProcessStartInfo> resume, CancellationToken ct)
        {
            if (_checksFailed || !Finished() || Extract(_tally).Report is { } report && HasSummary(report) || _tally.SessionId is not { } session
                || detector.Check(DateTimeOffset.UtcNow) is StallReason.WallClock)
                return;
            detector.Touch(DateTimeOffset.UtcNow);
            _tally.NewProcess();
            await DriveAsync(resume((session, ReportRequest)), start, DateTimeOffset.UtcNow, ct);
        }

        private bool Finished() => _stoppedBy.Cause is null && _tally.HasResult && !_tally.ResultIsError && Extract(_tally).Questions.Count == 0;

        private void Remind()
        {
            if (reminder is null || !reminder.Gate.Due(DateTimeOffset.UtcNow, detector.Turns))
                return;
            try
            {
                File.WriteAllText(reminder.File + ".tmp", GoalReminder.Payload(reminder.Text));
                File.Move(reminder.File + ".tmp", reminder.File, overwrite: true);
            }
            catch (IOException)
            {
                // A reminder that cannot be written only waits for the next interval.
            }
        }

        public async Task ReadAsync(CancellationToken ct)
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync(ct)) is not null)
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
                    Interrupt(_process);
            }
        }

        /// <returns>The diff measure still running when the session exited, if any.</returns>
        public async Task<Task?> WatchAsync(Task exit, string? start, DateTimeOffset started, CancellationToken ct)
        {
            using var watch = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            Task? measuring = null;
            var measuredAt = (Turn: 0, At: started);
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
                Remind();
                if (detector.Check(DateTimeOffset.UtcNow) is { } stall && _stoppedBy.Set(SessionStatus.Stalled, StallDetector.Wire(stall)))
                    Interrupt(_process);
                if (_stoppedBy.Cause is not null && !exit.IsCompleted && DateTimeOffset.UtcNow - _stoppedBy.At > Grace)
                    _process.Kill(entireProcessTree: true);
            }
            return measuring;
        }

        public async Task<SessionOutcome> FinishAsync(string? start, IReadOnlyList<CheckResult>? checks, CancellationToken ct)
        {
            string? governorHash = null;
            if (governorLog is not null)
            {
                await governorLog.DisposeAsync();
                governorHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(options.OutDirectory, GovernorFile), ct)));
            }

            // The bare arm (ADR 0080) commits once, as the person who ran it would, and writes no handoff.
            var snapshotNote = _task.Bare ? await SnapshotAsync(options, "", detector.Turns, ct, BareSubject)
                : _stoppedBy.Cause is { } stopped ? await SnapshotAsync(options, stopped.Reason, detector.Turns, ct) : null;
            if (_stoppedBy.Cause is { } stoppedFor && !_task.Bare)
                await WriteHandoffAsync(options, start, stoppedFor.Reason, detector.Turns, _trace, ct);
            var bundle = await BundleAsync(options, ct);
            var commits = bundle && start is not null ? await CommitsAsync(options, start, ct) : 0;
            var (report, questions) = _task.Bare ? ((string?)null, new List<string>()) : Extract(_tally);
            if (report is not null)
                await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "driven-report.json"), Redact(report, options.Secrets), ct);
            var (status, reason) = Conclude(_stoppedBy, _tally, questions, _checksFailed);
            await stream.DisposeAsync();
            var streamHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(options.OutDirectory, "stream.jsonl"), ct)));
            var outcome = new SessionOutcome(status, reason, detector.Turns, _tally.Input, _tally.Output, _tally.CacheRead, _tally.CostUsd, _exitCode,
                [.. questions.Select(q => Redact(q, options.Secrets))], bundle, streamHash, _tally.CacheWrite, _tally.SessionId, _tally.Model, _tally.ModelUsage.Count == 0 ? null : _tally.ModelUsage,
                _tally.UsdEstimate, status == SessionStatus.RateLimited ? GatewayRefusal.ResetsAt(_tally.ResultText, DateTimeOffset.UtcNow) : null, _tally.UsdEstimatePartial, commits, snapshotNote,
                governor is null ? null : _decisions, governorHash, checks, reminder?.Gate.Sent ?? 0, _task.Bare);
            await File.WriteAllTextAsync(Path.Combine(options.OutDirectory, "session-outcome.json"), Redact(JsonSerializer.Serialize(outcome, OutcomeJson.SessionOutcome), options.Secrets), ct);
            return outcome;
        }
    }
}
