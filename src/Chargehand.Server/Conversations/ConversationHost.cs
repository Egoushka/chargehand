using Chargehand.Modules.Conversations.Api;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chargehand.Server.Conversations;

/// <summary>Takes up the conversations of the earlier process before the listener opens, so one still open then is lost and not served as live, and closes the open ones when the server stops.</summary>
internal sealed partial class ConversationHost(ConversationService service, ILogger<ConversationHost> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        await service.RecoverAsync(ct);
        foreach (var id in service.Unreadable)
            Unreadable(logger, id);
    }

    public async Task StopAsync(CancellationToken ct) => await service.DisposeAsync();

    [LoggerMessage(Level = LogLevel.Warning, Message = "conversation journal {Id} could not be read and is left as it is")]
    private static partial void Unreadable(ILogger logger, string id);
}
