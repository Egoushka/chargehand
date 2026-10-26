namespace Chargehand.Modules.Tasks.Api;

/// <summary>The <see cref="IStartGate"/> of one server. The pool starts at <c>driven.max_parallel_total</c>; each plain 429 halves what it allows now (never below 1), and one slot returns
/// after every <see cref="Recovery"/> without a 429.</summary>
/// <param name="log">Told once per halt, for stderr; the console reads it from the batches' progress.</param>
public sealed class GatewayGate(int max, TimeProvider? time = null, Action<string>? log = null) : IStartGate
{
    public static readonly TimeSpan Recovery = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly int _max = Math.Max(1, max);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _floor = Math.Max(1, max);
    private DateTimeOffset _since;
    private DateTimeOffset _holdUntil;
    private DateTimeOffset _haltUntil;
    private int _running;

    /// <summary>What the pool allows now.</summary>
    public int Effective
    {
        get
        {
            lock (_gate)
                return EffectiveAt(_time.GetUtcNow());
        }
    }

    public DateTimeOffset? HaltedUntil
    {
        get
        {
            lock (_gate)
                return _haltUntil > _time.GetUtcNow() ? _haltUntil : null;
        }
    }

    public bool TryEnter()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_haltUntil > now || _holdUntil > now || _running >= EffectiveAt(now))
                return false;
            _running++;
            return true;
        }
    }

    public void Exit()
    {
        lock (_gate)
            _running = Math.Max(0, _running - 1);
    }

    public void RateLimited(DateTimeOffset? retryAfter)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _floor = Math.Max(1, EffectiveAt(now) / 2);
            _since = now;
            if (retryAfter is { } at && at > _holdUntil)
                _holdUntil = at;
        }
    }

    public void BudgetExhausted(DateTimeOffset resetsAt)
    {
        bool first;
        lock (_gate)
        {
            first = _haltUntil <= _time.GetUtcNow();
            if (resetsAt > _haltUntil)
                _haltUntil = resetsAt;
        }
        if (first)
            log?.Invoke($"the model gateway's budget is used up: no task starts until {resetsAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}");
    }

    private int EffectiveAt(DateTimeOffset now) =>
        _since == default ? _max : Math.Min(_max, _floor + (int)Math.Max(0, (now - _since) / Recovery));
}
