using Chargehand.Automation;
using Chargehand.Modules.Events.Api;
using Chargehand.Modules.Pool.Api;

namespace Chargehand.Server;

/// <summary>The pool's per-repository breaker (the sweep design, section 7): intake's <c>IntakeBreakers</c> never see a pool run, so this does the same job. Three pool runs in a row that ended
/// failed hold the repository's scopes with reason <c>breaker</c> for <see cref="BreakerFor"/>. A start the repository's own configuration refuses (a checkout that tracks a denied file, an App that
/// cannot push, a checkout that is missing) holds them with reason <c>problem</c> for <see cref="ProblemFor"/>, emits one <c>chargehand.sweep.problem</c> per repository and text, and is not a failure.</summary>
internal sealed class PoolBreakerCheck(IEventLog? events, TimeProvider clock) : IPoolCheck
{
    public const int Failures = 3;

    public static readonly TimeSpan BreakerFor = TimeSpan.FromMinutes(120);

    /// <summary>How long a repository problem holds before one start tries again, in case the owner fixed it (a starting value).</summary>
    public static readonly TimeSpan ProblemFor = TimeSpan.FromMinutes(15);

    private static readonly string[] ProblemMarks =
    [
        "denies reading", "denied file", "cannot push", "422", "is not in automation.github.repositories", "checkout", "not a git repository",
    ];

    private readonly Lock _gate = new();
    private IPoolCheck? _problems;
    private readonly Dictionary<string, int> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Text, DateTimeOffset Until)> _problem = new(StringComparer.Ordinal);

    public string Name => "breaker";

    /// <summary>The check that holds on a repository problem; it carries reason <c>problem</c>.</summary>
    public IPoolCheck Problems => _problems ??= new ProblemCheck(ProblemHold);

    /// <summary>Whether a starter's refusal is the repository's own configuration and not capacity (halted, an account's cap).</summary>
    public static bool IsRepositoryProblem(string refusal) => ProblemMarks.Any(m => refusal.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>The problems in force: repository, text and when the hold ends.</summary>
    public IReadOnlyList<(string Repository, string Text, DateTimeOffset Until)> Active()
    {
        var now = clock.GetUtcNow();
        lock (_gate)
            return [.. _problem.Where(p => p.Value.Until > now).Select(p => (p.Key, p.Value.Text, p.Value.Until)).Concat(_open.Where(o => o.Value > now)
                .Select(o => (o.Key, $"{Failures} pool runs in a row failed", o.Value))).OrderBy(p => p.Item1, StringComparer.Ordinal)];
    }

    public Task<string?> HoldAsync(PoolScope scope, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_open.TryGetValue(scope.Repository, out var until))
                return Task.FromResult<string?>(null);
            if (until > now)
                return Task.FromResult<string?>($"{Failures} pool runs in a row failed on {scope.Repository}; held until {until:HH:mm} UTC");
            _open.Remove(scope.Repository);
            _failed.Remove(scope.Repository);
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>A worker's run ended: <paramref name="failed"/> counts toward the breaker, anything else resets it.</summary>
    public void Finished(string repository, bool failed)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!failed)
            {
                _failed.Remove(repository);
                _problem.Remove(repository);
                return;
            }

            var count = _failed.GetValueOrDefault(repository) + 1;
            _failed[repository] = count;
            if (count >= Failures)
                _open[repository] = now + BreakerFor;
        }
    }

    /// <summary>The starter refused a start on <paramref name="repository"/>.</summary>
    /// <returns>Whether it is a repository problem; a refusal that is not is capacity and the pool holds it as <c>starter</c> as before.</returns>
    public async Task<bool> RefusedAsync(string repository, string refusal)
    {
        if (!IsRepositoryProblem(refusal))
            return false;
        var text = refusal.ReplaceLineEndings(" ");
        text = text.Length > 200 ? text[..200] : text;
        bool fresh;
        lock (_gate)
        {
            fresh = !_problem.TryGetValue(repository, out var known) || known.Text != text;
            _problem[repository] = (text, clock.GetUtcNow() + ProblemFor);
        }

        if (fresh && events is not null)
            await SweepEvents.EmitAsync(events, clock, $"pool-{Guid.NewGuid():N}", "chargehand.sweep.problem", "problem", w =>
            {
                w.WriteString("repository", repository);
                w.WriteString("text", text);
            }, CancellationToken.None);
        return true;
    }

    private string? ProblemHold(PoolScope scope)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
            // Once the hold ends the text stays, so the same refusal again is not announced twice.
            return _problem.TryGetValue(scope.Repository, out var p) && p.Until > now ? p.Text : null;
    }

    private sealed class ProblemCheck(Func<PoolScope, string?> hold) : IPoolCheck
    {
        public string Name => "problem";

        public Task<string?> HoldAsync(PoolScope scope, CancellationToken ct) => Task.FromResult(hold(scope));
    }
}
