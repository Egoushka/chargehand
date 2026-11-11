using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Checks the session images against every runner's allowlist at the server's start and then every <paramref name="every"/> (issue #425), and says in the log when a runner falls out
/// of step and when it is back. Admission refuses such a task on its own; this tells the operator before a batch asks.</summary>
/// <param name="check">Runner name to what its allowlist lacks (null: nothing); a runner left out could not be asked.</param>
/// <param name="write">Where a change is told; the server's stderr.</param>
public sealed class ImagePreflight(TimeSpan every, Func<CancellationToken, Task<IReadOnlyDictionary<string, string?>>> check, Action<string>? write = null, TimeProvider? clock = null)
    : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, string> _told = new(StringComparer.Ordinal);
    private readonly Action<string> _write = write ?? (line => Console.Error.WriteLine(line));

    /// <summary>One pass: tells each runner whose mismatch is new, and each whose mismatch has cleared.</summary>
    public async Task PassAsync(CancellationToken ct)
    {
        foreach (var (runner, mismatch) in await check(ct))
        {
            if (mismatch is not null && (!_told.TryGetValue(runner, out var said) || said != mismatch))
            {
                _told[runner] = mismatch;
                _write($"chargehand serve: runner '{runner}': {mismatch}; tasks are refused there until it does");
            }
            else if (mismatch is null && _told.Remove(runner))
                _write($"chargehand serve: runner '{runner}' carries the session images again");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(every, clock ?? TimeProvider.System);
        try
        {
            do
            {
                try
                {
                    await PassAsync(stoppingToken);
                }
                catch (Exception e) when (!(e is OperationCanceledException && stoppingToken.IsCancellationRequested))
                {
                    _write($"chargehand serve: could not check the runners' image allowlists: {e.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
