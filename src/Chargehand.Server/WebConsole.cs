using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server;

/// <summary>
/// The operator console (ADR 0056): the Vite build in <c>web/console/dist</c>, embedded when it exists and served at <c>/console/</c> when <c>http.ui</c> is on.
/// The files are public and need no key, the page signs in with an API key (or, behind Cloudflare Access, by the identity the proxy sends) and calls the routes the server already has, and
/// every response carries a Content-Security-Policy that allows only this origin's scripts and styles. A build without the web build serves a short page that says so.
/// </summary>
public static class WebConsole
{
    public const string Prefix = "/console";

    /// <summary>The path of the page of plain scripts that the console replaced (ADR 0054, 0072); it now sends the browser to the console.</summary>
    public const string LegacyPrefix = "/ui";

    /// <summary>Scripts and styles from this origin only, no inline of either, calls to this origin only, no frames, no base or form targets. <c>web/console/csp.ts</c> holds the same text.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>Content-hashed files under <c>assets/</c> never change, so a browser may keep them for a year.</summary>
    internal const string ImmutableCache = "public, max-age=31536000, immutable";

    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> Files = new(Load);

    private static readonly byte[] NotBuiltPage = Encoding.UTF8.GetBytes(
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>chargehand console</title></head><body>"
        + "<p>The console is not built into this server.</p>"
        + "<p>Run <code>npm ci &amp;&amp; npm run build</code> in <code>web/console</code>, then build the server again (docs/guide/web-ui.md).</p>"
        + "</body></html>");

    /// <summary>One answer: the file, a client route's <c>index.html</c>, a refusal, or the not-built page.</summary>
    internal sealed record Reply(int Status, string ContentType, byte[] Body, string CacheControl);

    /// <summary>Whether <paramref name="path"/> is the console's, or the old page's that redirects to it: <c>/console</c> or <c>/ui</c> or below.</summary>
    public static bool Serves(PathString path) => path.StartsWithSegments(Prefix) || path.StartsWithSegments(LegacyPrefix);

    /// <summary>Whether the web build was embedded.</summary>
    public static bool Built => Files.Value.ContainsKey("index.html");

    /// <summary>The paths of the embedded files, relative to <c>/console/</c>.</summary>
    internal static IEnumerable<string> Paths => Files.Value.Keys;

    internal static byte[] Read(string path) => Files.Value[path];

    public static void Map(WebApplication app)
    {
        // "/console/" matches the "/console" route too, so one handler tells them apart by the path as sent.
        IResult Serve(string? path, HttpContext ctx)
        {
            Harden(ctx);
            if (ctx.Request.Path == Prefix)
                return HttpResults.Redirect(Prefix + "/");
            var reply = Resolve(Files.Value, path);
            ctx.Response.StatusCode = reply.Status;
            ctx.Response.Headers.CacheControl = reply.CacheControl;
            return HttpResults.Bytes(reply.Body, reply.ContentType);
        }
        app.MapGet(Prefix, Serve);
        app.MapGet(Prefix + "/{**path}", Serve);
        // A bookmark of the old page lands on the console. A temporary redirect, so a browser never keeps it past a later change.
        app.MapGet(LegacyPrefix, () => HttpResults.Redirect(Prefix + "/"));
        app.MapGet(LegacyPrefix + "/{**path}", () => HttpResults.Redirect(Prefix + "/"));
    }

    /// <summary>
    /// What to answer for <paramref name="path"/> (relative to <c>/console/</c>) over <paramref name="files"/>. A file that exists is served as itself; any other path without a
    /// file extension is a client route and gets <c>index.html</c>; a missing asset or file is a 404, so a stale script is never answered with a page.
    /// </summary>
    internal static Reply Resolve(IReadOnlyDictionary<string, byte[]> files, string? path)
    {
        if (!files.TryGetValue("index.html", out var index))
            return new(StatusCodes.Status503ServiceUnavailable, ContentType("index.html"), NotBuiltPage, "no-store");
        var name = string.IsNullOrEmpty(path) ? "index.html" : path;
        if (files.TryGetValue(name, out var body))
            return new(StatusCodes.Status200OK, ContentType(name), body, name.StartsWith("assets/", StringComparison.Ordinal) ? ImmutableCache : "no-cache");
        if (name.StartsWith("assets/", StringComparison.Ordinal) || Path.HasExtension(name))
            return new(StatusCodes.Status404NotFound, "text/plain; charset=utf-8", [], "no-cache");
        return new(StatusCodes.Status200OK, ContentType("index.html"), index, "no-cache");
    }

    private static void Harden(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = ContentSecurityPolicy;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Frame-Options"] = "DENY";
    }

    private static string ContentType(string name) => Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".woff2" => "font/woff2",
        ".json" => "application/json; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static Dictionary<string, byte[]> Load()
    {
        var assembly = typeof(WebConsole).Assembly;
        var files = new Dictionary<string, byte[]>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("console/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files[name["console/".Length..].Replace('\\', '/')] = memory.ToArray();
        }
        return files;
    }
}
