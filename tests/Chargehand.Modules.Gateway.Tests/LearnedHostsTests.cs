using System.Text.Json;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Gateway.Tests;

/// <summary>ADR 0078, decision 4: the hosts a batch's sessions were refused, read from the egress proxy's log before its container goes, and kept per repository.</summary>
public sealed class LearnedHostsTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Directory.CreateTempSubdirectory("learned-hosts-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Line(string host, int port, bool allowed, string reason = "", DateTimeOffset? at = null) =>
        JsonSerializer.Serialize(new EgressLogLine(at ?? At, "run-1", host, port, allowed, reason), GatewayJsonContext.Default.EgressLogLine);

    [Fact]
    public void Only_refusals_that_listing_the_host_would_lift_are_kept()
    {
        var log = string.Join('\n',
            "egress proxy listening on 0.0.0.0:3128",
            Line("registry.npmjs.org", 443, true),
            Line("Static.Rust-Lang.org.", 443, false, "'static.rust-lang.org' is not on the allowlist"),
            Line("10.0.0.1", 443, false, "an IP literal is not allowed; use a listed host name"),
            Line("localhost", 443, false, "localhost is not allowed"),
            Line("example.com", 80, false, "port 80 is not allowed (only 443)"),
            """{"at":"2026-10-29T09:00:00+00:00","model_run":"run-1","status":429,"tokens":0,"reason":"budget"}""",
            "{not json",
            "");

        var refused = EgressRefusals.Parse(log);

        Assert.Equal([new EgressRefusal("static.rust-lang.org", At)], refused);
    }

    [Fact]
    public void A_host_refused_in_two_batches_counts_both_and_keeps_its_first_and_last_time()
    {
        var store = new LearnedHosts(Path.Combine(_dir, "learned-hosts.json"));
        store.Record("owner/repo", "batch-1", [new("static.rust-lang.org", At), new("static.rust-lang.org", At.AddMinutes(1))]);
        store.Record("owner/repo", "batch-2", [new("static.rust-lang.org", At.AddHours(1))]);
        store.Record("owner/other", "batch-3", [new("proxy.golang.org", At)]);

        var read = store.Read();

        Assert.Equal([new LearnedHost("static.rust-lang.org", 3, 2, At, At.AddHours(1), "batch-2")], read["owner/repo"]);
        Assert.Equal("proxy.golang.org", Assert.Single(read["owner/other"]).Host);
    }

    [Fact]
    public void A_batch_with_no_refusals_writes_nothing_and_a_broken_file_reads_as_empty()
    {
        var path = Path.Combine(_dir, "learned-hosts.json");
        var store = new LearnedHosts(path);
        store.Record("owner/repo", "batch-1", []);
        Assert.False(File.Exists(path));

        File.WriteAllText(path, "{broken");
        Assert.Empty(store.Read());
        store.Record("owner/repo", "batch-2", [new("proxy.golang.org", At)]);
        Assert.Single(store.Read()["owner/repo"]);
    }

    [Fact]
    public void The_owner_approves_a_refused_host_for_its_repository_only_and_can_take_it_back()
    {
        var store = new LearnedHosts(Path.Combine(_dir, "learned-hosts.json"));
        store.Record("owner/repo", "batch-1", [new("static.rust-lang.org", At)]);

        Assert.False(store.Approve("owner/repo", "never-refused.example.com", At));
        Assert.False(store.Approve("owner/other", "static.rust-lang.org", At));
        Assert.True(store.Approve("owner/repo", "Static.Rust-Lang.org", At.AddHours(1)));
        Assert.Equal(["static.rust-lang.org"], store.ApprovedFor("owner/repo"));
        Assert.Empty(store.ApprovedFor("owner/other"));
        Assert.Equal(At.AddHours(1), store.Read()["owner/repo"][0].Approved);

        // A later refusal of an approved host (a batch that started before the approval) keeps the approval.
        store.Record("owner/repo", "batch-2", [new("static.rust-lang.org", At.AddHours(2))]);
        Assert.Equal(["static.rust-lang.org"], store.ApprovedFor("owner/repo"));

        Assert.True(store.Revoke("owner/repo", "static.rust-lang.org"));
        Assert.False(store.Revoke("owner/repo", "static.rust-lang.org"));
        Assert.Empty(store.ApprovedFor("owner/repo"));
    }

    [Fact]
    public void An_approved_host_is_never_dropped_for_newer_refusals()
    {
        var store = new LearnedHosts(Path.Combine(_dir, "learned-hosts.json"));
        store.Record("owner/repo", "batch-1", [new("old.example.com", At)]);
        store.Approve("owner/repo", "old.example.com", At);
        store.Record("owner/repo", "batch-2",
            [.. Enumerable.Range(0, LearnedHosts.MaxPerRepository + 5).Select(i => new EgressRefusal($"h{i}.example.com", At.AddMinutes(i + 1)))]);

        Assert.Equal(["old.example.com"], store.ApprovedFor("owner/repo"));
        Assert.Equal(LearnedHosts.MaxPerRepository, store.Read()["owner/repo"].Count);
    }

    [Fact]
    public void A_repository_keeps_the_hosts_refused_most_recently()
    {
        var store = new LearnedHosts(Path.Combine(_dir, "learned-hosts.json"));
        var many = Enumerable.Range(0, LearnedHosts.MaxPerRepository + 5).Select(i => new EgressRefusal($"h{i}.example.com", At.AddMinutes(i))).ToList();
        store.Record("owner/repo", "batch-1", many);

        var hosts = store.Read()["owner/repo"];

        Assert.Equal(LearnedHosts.MaxPerRepository, hosts.Count);
        Assert.DoesNotContain(hosts, h => h.Host == "h0.example.com");
        Assert.Equal("h104.example.com", hosts[0].Host);
    }
}
