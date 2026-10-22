using System.Text.RegularExpressions;
using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Intake.Api;

/// <summary>ADR 0077: a task that stops to ask (needs_input) or stops before it finished (a partial) is sent to a person with the answers they can give, so it can be answered away from the console.
/// Unset, nothing is sent and the questions wait in the inbox.</summary>
/// <param name="Approvals">An approvals server (docs/guide/phone-answers.md): each item carries the answer options, and the decision comes back signed to <see cref="NotifyApprovals.CallbackUrl"/>.</param>
/// <param name="NtfyUrl">An ntfy-style URL: a plain-text note with the questions and a link to the task, no answer buttons. The URL can carry a secret topic, so it is never logged.</param>
/// <param name="ConsoleUrl">The console as the phone reaches it (<c>https://host/ui/</c>): the link to the task in every note. Null: no link.</param>
public sealed record AutomationNotify(NotifyApprovals? Approvals = null, string? NtfyUrl = null, string? ConsoleUrl = null)
{
    internal void Validate(bool drivenEnabled)
    {
        List<string> problems = [.. Problems(drivenEnabled)];
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"automation.notify: {string.Join("; ", problems)}", "Fix automation.notify in the profile; docs/guide/phone-answers.md lists the keys.");
    }

    private IEnumerable<string> Problems(bool drivenEnabled)
    {
        if (!drivenEnabled)
            yield return "needs driven.enabled: only a driven task asks";
        if (Approvals is null && NtfyUrl is null)
            yield return "set approvals or ntfy_url";
        if (NtfyUrl is not null && !NotifyApprovals.IsUrl(NtfyUrl, https: false))
            yield return "ntfy_url must be an http or https URL";
        if (ConsoleUrl is not null && !NotifyApprovals.IsUrl(ConsoleUrl, https: false))
            yield return "console_url must be an http or https URL";
        foreach (var problem in Approvals?.Problems() ?? [])
            yield return problem;
    }
}

/// <param name="Url">The approvals server's base URL; items are <c>PUT</c> to <c>{url}/items/{id}</c>.</param>
/// <param name="TokenSecret">The secret item holding this source's bearer token on the approvals server.</param>
/// <param name="KeySecret">The secret item holding the key the approvals server signs decisions with (HMAC-SHA256).</param>
/// <param name="CallbackUrl">Where the approvals server posts a decision: this server's <c>/v1/answers/approvals</c> as that server reaches it. It must be https and on the approvals server's allow list.</param>
/// <param name="Source">The source name the token belongs to; every item id starts with it and a colon (default <c>chargehand</c>).</param>
public sealed partial record NotifyApprovals(string? Url = null, string? TokenSecret = null, string? KeySecret = null, string? CallbackUrl = null, string Source = "chargehand")
{
    internal IEnumerable<string> Problems()
    {
        if (Url is null || !IsUrl(Url, https: false)) yield return "approvals.url must be an http or https URL";
        if (string.IsNullOrWhiteSpace(TokenSecret)) yield return "approvals.token_secret must name the secret item with the source's token";
        if (string.IsNullOrWhiteSpace(KeySecret)) yield return "approvals.key_secret must name the secret item with the decision signing key";
        if (CallbackUrl is null || !IsUrl(CallbackUrl, https: true)) yield return "approvals.callback_url must be an https URL";
        if (!SourceName().IsMatch(Source)) yield return "approvals.source must be lower-case letters, digits and -";
    }

    internal static bool IsUrl(string text, bool https) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url) && (url.Scheme == "https" || (!https && url.Scheme == "http"));

    [GeneratedRegex(@"^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex SourceName();
}
