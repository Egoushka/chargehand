using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace Chargehand.Server;

/// <summary>
/// The web UI (ADR 0054): static files embedded from <c>Ui/</c>, served at <c>/ui/</c> when <c>http.ui</c> is on. They are public and need no key; the page signs in
/// with an API key and calls the routes the server already has. Every response carries a Content-Security-Policy that allows only this origin's scripts and styles,
/// so a page cannot run inline script or load anything from another origin.
/// </summary>
public static class WebUi
{
    public const string Prefix = "/ui";

    /// <summary>Scripts and styles from this origin only, no inline of either, calls to this origin only, no frames, no base or form targets.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private static readonly Lazy<IReadOnlyDictionary<string, byte[]>> Files = new(Load);

    /// <summary>Whether <paramref name="path"/> is the UI's: <c>/ui</c> or below it.</summary>
    public static bool Serves(PathString path) => path.StartsWithSegments(Prefix);

    /// <summary>The paths of the embedded files, relative to <c>/ui/</c>.</summary>
    internal static IEnumerable<string> Paths => Files.Value.Keys;

    internal static byte[] Read(string path) => Files.Value[path];

    public static void Map(WebApplication app)
    {
        // "/ui/" matches the "/ui" route too, so one handler tells them apart by the path as sent.
        IResult Serve(string? path, HttpContext ctx)
        {
            Harden(ctx);
            if (ctx.Request.Path == Prefix)
                return HttpResults.Redirect(Prefix + "/");
            var name = string.IsNullOrEmpty(path) ? "index.html" : path;
            return Files.Value.TryGetValue(name, out var body) ? HttpResults.Bytes(body, ContentType(name)) : HttpResults.NotFound();
        }
        app.MapGet(Prefix, Serve);
        app.MapGet(Prefix + "/{**path}", Serve);
    }

    private static void Harden(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h.ContentSecurityPolicy = ContentSecurityPolicy;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Frame-Options"] = "DENY";
        h.CacheControl = "no-cache";
    }

    private static string ContentType(string name) => Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".svg" => "image/svg+xml",
        _ => "application/octet-stream",
    };

    private static Dictionary<string, byte[]> Load()
    {
        var assembly = typeof(WebUi).Assembly;
        var files = new Dictionary<string, byte[]>();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("ui/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files[name["ui/".Length..].Replace('\\', '/')] = memory.ToArray();
        }
        return files;
    }
}
