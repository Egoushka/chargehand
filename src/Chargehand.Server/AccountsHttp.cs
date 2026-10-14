using Chargehand.Modules.Accounts.Api;
using Microsoft.AspNetCore.Http;

namespace Chargehand.Server;

/// <summary>Where the server keeps a request's principal (ADR 0050), and how it reads it back.</summary>
public static class PrincipalHttp
{
    /// <summary>Where the middleware leaves the request's principal.</summary>
    public const string ItemKey = "chargehand.principal";

    public static Principal? Of(HttpContext? ctx) => ctx?.Items[ItemKey] as Principal;
}

public static class AccountRefusalHttp
{
    /// <summary>The response body of a refused run: the message as the errors list, and the refusal itself.</summary>
    public static object Body(this AccountRefusal refusal) => new { errors = new[] { refusal.Message }, error = refusal };
}
