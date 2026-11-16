using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

public sealed class SignalWatcherHost(SignalWatcher watcher) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => watcher.RunAsync(stoppingToken);
}
