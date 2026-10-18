using System.Net;
using System.Text;

namespace Chargehand.Automation;

/// <summary>Where a scheduled result goes (ADR 0068): a plain-text message with a title.</summary>
public interface IScheduleNotifier
{
    /// <exception cref="IntakeException">The message was not delivered. The text never names the URL: it can carry a secret topic.</exception>
    Task PostAsync(string url, string title, string body, CancellationToken ct);
}

/// <summary>POSTs to an ntfy-style URL: the body is the text, the <c>Title</c> header its title.</summary>
public sealed class HttpScheduleNotifier(HttpClient? client = null) : IScheduleNotifier
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client = client ?? new HttpClient { Timeout = Timeout };

    public async Task PostAsync(string url, string title, string body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        // HTTP headers are Latin-1 at best: a title outside ASCII is cut down to what every server reads the same way.
        request.Headers.TryAddWithoutValidation("Title", new string([.. title.Where(c => c is >= ' ' and <= '~')]));
        try
        {
            using var response = await _client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new IntakeException($"the notification service answered {(int)response.StatusCode}", (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new IntakeException($"the notification was not delivered ({e.GetType().Name})", e is HttpRequestException { StatusCode: { } status } ? (int)status : null);
        }
    }
}
