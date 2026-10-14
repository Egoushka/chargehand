using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chargehand.Contracts;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The rungs of ADR 0045's ladder. A warn has no channel into the session yet (the driver closes its stdin), so it is only logged.</summary>
[JsonConverter(typeof(SnakeCaseEnumConverter<GovernorAction>))]
public enum GovernorAction { Warn, Checkpoint, Stop }

/// <summary>The governor's settings for one session, in <c>task.json</c>: the profile's <c>driven.governor</c> with the task's budget resolved.</summary>
/// <param name="BudgetUsd">The task's budget in the dollar equivalent of <c>SessionUsage.UsdEstimate</c>; null: no spend rule.</param>
/// <param name="PriorUsd">What earlier sessions of the task spent (ADR 0044's continuations): the budget is the task's, not one session's.</param>
/// <param name="SizeClass">S, M or L: where the budget came from, logged with each decision.</param>
/// <param name="RepeatCalls">Identical action and observation pairs in a row that fire <c>repeated_call</c>.</param>
/// <param name="RepeatFailures">Failures of one identical action that fire <c>repeated_failure</c>.</param>
/// <param name="FailingCalls">Failing tool calls in a row that fire <c>failing_calls</c>.</param>
/// <param name="ProbeCommands"><c>environment_probing</c> fires on the command after this many that only ask what the container has.</param>
/// <param name="NoGrowthTurns">Turns with the diff's added plus removed lines unchanged that fire <c>no_diff_growth</c>.</param>
/// <param name="RewarnTurns">A signal that fires again at least this many turns after its warn is a checkpoint.</param>
/// <param name="ContextTokens">One message's context (input, cache read and cache write) above this is a checkpoint: a fresh session is cheaper per turn.</param>
/// <param name="CheckpointAt">The share of the budget at which spend is a checkpoint; 100% is a stop.</param>
/// <param name="ProbePatterns">Regular expressions; a command all of whose parts match one only asks what the container has.</param>
/// <param name="WaitPatterns">Regular expressions for commands that wait on purpose: they count toward no repeat or failure rule, and no-growth turns pause while one runs.</param>
/// <param name="BudgetSource">Where <paramref name="BudgetUsd"/> came from (ADR 0045): the task's own, the flat budget of its size class, or a percentile of past spend; logged with each decision.</param>
public sealed record SessionGovernor(
    GovernorMode Mode = GovernorMode.Observe,
    decimal? BudgetUsd = null,
    decimal PriorUsd = 0,
    string? SizeClass = null,
    int RepeatCalls = 4,
    int RepeatFailures = 3,
    int FailingCalls = 8,
    int ProbeCommands = 10,
    int NoGrowthTurns = 40,
    int RewarnTurns = 10,
    long ContextTokens = 160_000,
    decimal CheckpointAt = 0.85m,
    IReadOnlyList<string>? ProbePatterns = null,
    IReadOnlyList<string>? WaitPatterns = null,
    BudgetSource? BudgetSource = null)
{
    public static IReadOnlyList<string> DefaultProbePatterns { get; } =
    [
        @"^(which|whereis|type|command -v)\b",
        @"^\S+ (--version|-v|-V|version)$",
        @"^(env|printenv|uname|lsb_release|nproc|free|df|id|whoami|hostname)\b",
        @"^cat /etc/\S*release",
        @"^(dpkg -l|apt list|pip3? list|npm ls -g|dotnet --(info|list-sdks|list-runtimes))\b",
        @"^ls( -\S+)* /(usr|opt|bin|etc|root)\b",
    ];

    public static IReadOnlyList<string> DefaultWaitPatterns { get; } = [@"(^|[\s;&|])sleep\s", @"--watch\b", @"\bgh run watch\b"];

    /// <summary>A profile's patterns match in time linear in the command, so they need no match timeout: a timeout is wall-clock, and a loaded host ran out of 50 ms on plain commands.</summary>
    internal const RegexOptions PatternOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
}

/// <summary>One line of <c>governor.jsonl</c>: what fired, the rung it reached and why, and where the session stood. <c>Acted</c> is true only for the decision that stopped the
/// session in <c>enforce</c> mode.</summary>
/// <param name="Rule">Why this rung: <c>first_hit</c>, <c>same_signal_again</c>, <c>second_signal</c>, <c>context_size</c>, <c>spend_checkpoint</c> or <c>spend_stop</c>.</param>
/// <param name="Values">The signal's measurements, for judging afterwards whether the decision was right.</param>
/// <param name="BudgetSource">Where the budget came from (ADR 0045); null in a log from before it was recorded.</param>
public sealed record GovernorDecision(DateTimeOffset At, int Turn, GovernorMode Mode, GovernorAction Action, string Signal, string Rule, IReadOnlyDictionary<string, string> Values,
    decimal? SpendUsd, decimal? BudgetUsd, string? SizeClass, bool Acted = false, BudgetSource? BudgetSource = null);

/// <summary>ADR 0045's governor: reads a session's stream and says when it is stuck or over budget, with no model call. Pure: the caller passes the turn, the time, the spend
/// and the diff size, and writes the decisions. The stuck signals (repeated call, repeated failure, alternation, failing calls, environment probing, no diff growth) warn on
/// their first hit; the same signal again after <see cref="SessionGovernor.RewarnTurns"/>, or a second signal while one is warned, is a checkpoint. Context size and 85% of
/// the budget are checkpoints, 100% a stop. A checkpoint clears the warnings: the session it would start is a fresh one.</summary>
public sealed partial class Governor
{
    private const int MaxValueLength = 200;

    /// <summary>The stop reasons of an enforced decision, as they appear in the session outcome. The three checkpoints continue (<c>StallDetector.ContinuableReasons</c>); a
    /// spent budget does not.</summary>
    public const string CheckpointStuck = "checkpoint_stuck";
    public const string CheckpointContext = "checkpoint_context";
    public const string CheckpointSpend = "checkpoint_spend";
    public const string BudgetSpent = "budget_spent";

    /// <summary>The stop reason a decision leads to in <c>enforce</c> mode; null for a warn, which stays a log line.</summary>
    public static string? StopReason(GovernorDecision d) => d.Action switch
    {
        GovernorAction.Stop => BudgetSpent,
        GovernorAction.Checkpoint => d.Signal switch { "spend" => CheckpointSpend, "context_size" => CheckpointContext, _ => CheckpointStuck },
        _ => null,
    };

    private readonly SessionGovernor _settings;
    private readonly Regex[] _probes;
    private readonly Regex[] _waits;
    private readonly Dictionary<string, (string Action, string Describe, bool Wait)> _pending = [];
    private readonly Dictionary<string, int> _failures = [];
    private readonly Dictionary<string, int> _warned = [];
    private readonly List<string> _recent = [];
    private string? _lastPair;
    private int _pairRepeats;
    private int _failingInARow;
    private int _probeCount;
    private bool _probed;
    private long? _diffLines;
    private int _diffSince;
    private bool _contextFired;
    private bool _spendCheckpoint;
    private bool _spendStop;

    public Governor(SessionGovernor settings)
    {
        _settings = settings;
        _probes = settings.ProbePatterns is { } probes ? Build(probes) : DefaultProbes;
        _waits = settings.WaitPatterns is { } waits ? Build(waits) : DefaultWaits;
    }

    /// <summary>The built-in patterns, built once: they cannot backtrack badly, and the linear engine costs tens of milliseconds to build per governor.</summary>
    private static readonly Regex[] DefaultProbes = [.. SessionGovernor.DefaultProbePatterns.Select(p => new Regex(p, RegexOptions.CultureInvariant))];
    private static readonly Regex[] DefaultWaits = [.. SessionGovernor.DefaultWaitPatterns.Select(p => new Regex(p, RegexOptions.CultureInvariant))];

    private static Regex[] Build(IEnumerable<string> patterns) => [.. patterns.Select(p => new Regex(p, SessionGovernor.PatternOptions))];

    /// <summary>The decisions this stream event leads to. <paramref name="sessionUsd"/> is this session's dollar equivalent so far; earlier sessions' is added.</summary>
    public IReadOnlyList<GovernorDecision> OnEvent(JsonElement e, int turn, decimal? sessionUsd, DateTimeOffset now)
    {
        var decisions = new List<GovernorDecision>();
        var spend = sessionUsd is { } usd ? usd + _settings.PriorUsd : (decimal?)null;
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("type", out var type) || !e.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return Spend(decisions, turn, spend, now);
        var blocks = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array ? content.EnumerateArray().ToList() : [];
        switch (type.GetString())
        {
            case "assistant":
                OnUsage(message, turn, spend, now, decisions);
                foreach (var block in blocks.Where(b => Is(b, "tool_use")))
                    OnCall(block, turn, spend, now, decisions);
                break;
            case "user":
                foreach (var block in blocks.Where(b => Is(b, "tool_result")))
                    OnResult(block, turn, spend, now, decisions);
                break;
        }
        return Spend(decisions, turn, spend, now);
    }

    /// <summary>A new measure of the branch's diff against the base (added plus removed lines). No change for <see cref="SessionGovernor.NoGrowthTurns"/> turns fires
    /// <c>no_diff_growth</c>, except while a command that waits on purpose runs.</summary>
    public IReadOnlyList<GovernorDecision> OnDiff(long changedLines, int turn, decimal? sessionUsd, DateTimeOffset now)
    {
        if (_diffLines != changedLines)
        {
            (_diffLines, _diffSince) = (changedLines, turn);
            return [];
        }
        if (turn - _diffSince < _settings.NoGrowthTurns || _pending.Values.Any(p => p.Wait))
            return [];
        var since = _diffSince;
        _diffSince = turn;
        var spend = sessionUsd is { } usd ? usd + _settings.PriorUsd : (decimal?)null;
        return Stuck("no_diff_growth", new() { ["changed_lines"] = Num(changedLines), ["unchanged_since_turn"] = Num(since) }, turn, spend, now) is { } d ? [d] : [];
    }

    private void OnUsage(JsonElement message, int turn, decimal? spend, DateTimeOffset now, List<GovernorDecision> decisions)
    {
        if (!_contextFired && message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && Long(usage, "input_tokens") + Long(usage, "cache_read_input_tokens") + Long(usage, "cache_creation_input_tokens") is var context && context > _settings.ContextTokens)
        {
            _contextFired = true;
            decisions.Add(Checkpoint("context_size", "context_size", new() { ["context_tokens"] = Num(context), ["limit"] = Num(_settings.ContextTokens) }, turn, spend, now));
        }
    }

    private void OnCall(JsonElement block, int turn, decimal? spend, DateTimeOffset now, List<GovernorDecision> decisions)
    {
        var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        var input = block.TryGetProperty("input", out var i) ? i : default;
        var command = name == "Bash" && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : null;
        var describe = Cut(command is not null ? $"Bash: {command}" : $"{name}: {(input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText())}");
        var wait = command is not null && _waits.Any(r => r.IsMatch(command));
        if (block.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            _pending[id.GetString()!] = ($"{name}|{(input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText())}", describe, wait);
        if (command is not null)
            OnProbe(command, describe, turn, spend, now, decisions);
    }

    private void OnProbe(string command, string describe, int turn, decimal? spend, DateTimeOffset now, List<GovernorDecision> decisions)
    {
        if (_probed || !OnlyProbes(command))
            return;
        if (++_probeCount <= _settings.ProbeCommands)
            return;
        _probed = true;
        if (Stuck("environment_probing", new() { ["probe_commands"] = Num(_probeCount), ["last"] = describe }, turn, spend, now) is { } d)
            decisions.Add(d);
    }

    private void OnResult(JsonElement block, int turn, decimal? spend, DateTimeOffset now, List<GovernorDecision> decisions)
    {
        if (!block.TryGetProperty("tool_use_id", out var id) || id.ValueKind != JsonValueKind.String || !_pending.Remove(id.GetString()!, out var call) || call.Wait)
            return;
        var failed = block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
        var observation = block.TryGetProperty("content", out var content) ? content.GetRawText() : "";
        var pair = $"{call.Action}\n{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(observation)))}";

        _pairRepeats = pair == _lastPair ? _pairRepeats + 1 : 1;
        _lastPair = pair;
        if (_pairRepeats >= _settings.RepeatCalls)
        {
            _pairRepeats = 0;
            _lastPair = null;
            Add(decisions, Stuck("repeated_call", new() { ["action"] = call.Describe, ["count"] = Num(_settings.RepeatCalls) }, turn, spend, now));
        }

        if (failed)
            OnFailure(call.Action, call.Describe, turn, spend, now, decisions);
        else
            _failingInARow = 0;

        _recent.Add(call.Action);
        if (_recent.Count > 6)
            _recent.RemoveAt(0);
        if (_recent.Count == 6 && _recent[0] != _recent[1] && Enumerable.Range(2, 4).All(k => _recent[k] == _recent[k % 2]))
        {
            _recent.Clear();
            Add(decisions, Stuck("alternation", new() { ["last"] = call.Describe, ["cycles"] = "3" }, turn, spend, now));
        }
    }

    private void OnFailure(string action, string describe, int turn, decimal? spend, DateTimeOffset now, List<GovernorDecision> decisions)
    {
        var count = _failures[action] = _failures.GetValueOrDefault(action) + 1;
        if (count >= _settings.RepeatFailures)
        {
            _failures[action] = 0;
            Add(decisions, Stuck("repeated_failure", new() { ["action"] = describe, ["count"] = Num(count) }, turn, spend, now));
        }
        if (++_failingInARow >= _settings.FailingCalls)
        {
            Add(decisions, Stuck("failing_calls", new() { ["in_a_row"] = Num(_failingInARow), ["last"] = describe }, turn, spend, now));
            _failingInARow = 0;
        }
    }

    private bool OnlyProbes(string command)
    {
        var parts = CommandSeparators().Split(command).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts.Count > 0 && parts.All(p => _probes.Any(r => r.IsMatch(p)));
    }

    /// <summary>The ladder for a stuck signal: warn on the first hit; checkpoint on the same signal after the re-warn window or on a second signal; nothing on the same
    /// signal inside the window (it is still the warned episode).</summary>
    private GovernorDecision? Stuck(string signal, Dictionary<string, string> values, int turn, decimal? spend, DateTimeOffset now)
    {
        if (_warned.Keys.FirstOrDefault(s => s != signal) is { } other)
        {
            values["warned"] = other;
            return Checkpoint(signal, "second_signal", values, turn, spend, now);
        }
        if (_warned.TryGetValue(signal, out var warnedAt))
        {
            if (turn - warnedAt < _settings.RewarnTurns)
                return null;
            values["warned_at_turn"] = Num(warnedAt);
            return Checkpoint(signal, "same_signal_again", values, turn, spend, now);
        }
        _warned[signal] = turn;
        return Decision(GovernorAction.Warn, signal, "first_hit", values, turn, spend, now);
    }

    private GovernorDecision Checkpoint(string signal, string rule, Dictionary<string, string> values, int turn, decimal? spend, DateTimeOffset now)
    {
        _warned.Clear();
        return Decision(GovernorAction.Checkpoint, signal, rule, values, turn, spend, now);
    }

    private List<GovernorDecision> Spend(List<GovernorDecision> decisions, int turn, decimal? spend, DateTimeOffset now)
    {
        if (spend is not { } usd || _settings.BudgetUsd is not { } budget || budget <= 0)
            return decisions;
        var values = new Dictionary<string, string> { ["share"] = Num(Math.Round(usd / budget, 3)) };
        if (!_spendCheckpoint && usd >= budget * _settings.CheckpointAt)
        {
            _spendCheckpoint = true;
            decisions.Add(Checkpoint("spend", "spend_checkpoint", values, turn, spend, now));
        }
        if (!_spendStop && usd >= budget)
        {
            _spendStop = true;
            decisions.Add(Decision(GovernorAction.Stop, "spend", "spend_stop", values, turn, spend, now));
        }
        return decisions;
    }

    private GovernorDecision Decision(GovernorAction action, string signal, string rule, Dictionary<string, string> values, int turn, decimal? spend, DateTimeOffset now) =>
        new(now, turn, _settings.Mode, action, signal, rule, values, spend is { } s ? Math.Round(s, 4) : null, _settings.BudgetUsd, _settings.SizeClass, BudgetSource: _settings.BudgetSource);

    private static void Add(List<GovernorDecision> decisions, GovernorDecision? decision)
    {
        if (decision is not null)
            decisions.Add(decision);
    }

    private static bool Is(JsonElement block, string type) =>
        block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == type;

    private static long Long(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string Num(long n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Num(decimal n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Cut(string s) => s.Length <= MaxValueLength ? s : s[..MaxValueLength] + "…";

    [GeneratedRegex(@"&&|\|\||;|\|")]
    private static partial Regex CommandSeparators();
}

/// <summary>A task's budget in the dollar equivalent and where it came from.</summary>
public sealed record BudgetChoice(decimal Usd, BudgetSource Source);

public static class DrivenGovernorExtensions
{
    /// <summary>A task's own budget, else the flat budget of its size class (M when it names none).</summary>
    public static BudgetChoice Budget(this DrivenGovernor settings, string? size, decimal? taskBudget) =>
        taskBudget is { } own ? new BudgetChoice(own, new BudgetSource("task"))
            : new BudgetChoice((settings.Budgets ?? DrivenGovernor.DefaultBudgets).GetValueOrDefault(size ?? "M", DrivenGovernor.DefaultBudgets[size ?? "M"]), new BudgetSource("flat"));

    /// <summary>The settings a session of a task gets: its budget (<paramref name="chosen"/> when the caller looked at the history, else the task's own or its size class's flat one, M when it names none),
    /// and what earlier sessions of the task spent.</summary>
    public static SessionGovernor ToSession(this DrivenGovernor settings, string? size, decimal? taskBudget, decimal priorUsd, BudgetChoice? chosen = null)
    {
        var sizeClass = size ?? "M";
        var d = new SessionGovernor();
        var budget = chosen ?? settings.Budget(size, taskBudget);
        return new SessionGovernor(settings.Mode, budget.Usd, priorUsd, sizeClass,
            settings.RepeatCalls ?? d.RepeatCalls, settings.RepeatFailures ?? d.RepeatFailures, settings.FailingCalls ?? d.FailingCalls, settings.ProbeCommands ?? d.ProbeCommands,
            settings.NoGrowthTurns ?? d.NoGrowthTurns, settings.RewarnTurns ?? d.RewarnTurns, settings.ContextTokens ?? d.ContextTokens, settings.CheckpointAt ?? d.CheckpointAt,
            settings.ProbePatterns, settings.WaitPatterns, budget.Source);
    }
}
