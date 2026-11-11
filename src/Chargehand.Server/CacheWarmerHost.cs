using Chargehand.Modules.Warmer.Api;
using Microsoft.Extensions.Hosting;

namespace Chargehand.Server;

/// <summary>Runs the cache warmer for the life of the server (ADR 0090).</summary>
public sealed class CacheWarmerHost(CacheWarmer warmer) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => warmer.RunAsync(stoppingToken);
}
