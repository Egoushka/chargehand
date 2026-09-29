using Chargehand.Runtime;

namespace Chargehand.Tests;

public class ServiceGrantTests
{
    [Fact]
    public void A_grant_never_prints_a_header_or_an_environment_value()
    {
        var http = new ServiceGrant("team-docs", new HttpServiceTransport(new Uri("https://mcp.example.internal/mcp"), new Dictionary<string, string> { ["Authorization"] = "Bearer s3cret" }), ["search_docs"], new string('a', 64));
        var stdio = new ServiceGrant("team-notes", new StdioServiceTransport(["npx", "-y", "example-notes-mcp"], new Dictionary<string, string> { ["NOTES_TOKEN"] = "s3cret" }), ["search_notes"], new string('b', 64));

        Assert.DoesNotContain("s3cret", http.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", http.Transport.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", stdio.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", stdio.Transport.ToString(), StringComparison.Ordinal);
    }
}
