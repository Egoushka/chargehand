using Chargehand.Modules.Accounts.Api;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

public static partial class ChargehandServer
{
    /// <summary>The header the console sends on every call. A page on another origin cannot add it without a CORS preflight the server never
    /// answers, so a request that rides only on the browser's Cloudflare cookie is refused (ADR 0071).</summary>
    public const string ConsoleHeader = "X-Chargehand-Console";

    private static readonly HttpClient AccessHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The account a Cloudflare Access token's email maps to; null when the request is not the console's, the token is not valid, or the email is not listed.</summary>
    private static async Task<Principal?> AccessPrincipal(HttpContext ctx, CloudflareAccessVerifier access, ServerSettings settings)
    {
        if (ctx.Request.Headers[ConsoleHeader].ToString() != "1" || ctx.Request.Headers[CloudflareAccessSettings.Header].ToString() is not { Length: > 0 } token)
            return null;
        if (await access.EmailAsync(token, ctx.RequestAborted) is not { } email || settings.Access!.AccountOf(email) is not { } name)
            return null;
        if (name == AccountSettings.Owner)
            return Principal.Owner;
        return settings.Accounts?.FirstOrDefault(a => a.Name == name) is { } account ? new Principal(account.Name, account) : null;
    }

    private static Func<CancellationToken, Task<string>> Certs(CloudflareAccessSettings access) =>
        ct => AccessHttp.GetStringAsync(access.CertsUrl, ct);
}
