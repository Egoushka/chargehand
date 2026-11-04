using System.Net;
using Chargehand.Modules.Accounts.Api;

namespace Chargehand.Tests;

/// <summary>Runs on public repositories use the free-model pool, and nothing else does (ADR 0093).</summary>
public class PublicFreePoolTests
{
    internal static readonly PublicFreeSettings Settings = new("pool-key-item", "https://gateway.example/anthropic", Replaces: ["default", "cheap"]);

    internal sealed class FakeVisibility(Func<string, RepositoryVisibility> answer) : IRepositoryVisibility
    {
        public int Calls { get; private set; }

        public Task<RepositoryVisibility> LookupAsync(string forge, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(answer(forge));
        }
    }

    internal sealed class ThrowingVisibility(Exception error) : IRepositoryVisibility
    {
        public Task<RepositoryVisibility> LookupAsync(string forge, CancellationToken ct) => Task.FromException<RepositoryVisibility>(error);
    }

    internal static PublicFreePool Pool(IRepositoryVisibility visibility, bool enabled = true, PublicFreeSettings? settings = null, TimeProvider? clock = null)
    {
        var on = new PublicFreeSwitch(null);
        on.Set(enabled);
        return new PublicFreePool(settings ?? Settings, visibility, on, clock ?? TimeProvider.System);
    }

    [Fact]
    public async Task APublicRepositoryMovesAReplacedPresetToThePool()
    {
        var decision = await Pool(new FakeVisibility(_ => RepositoryVisibility.Public)).DecideAsync("cheap", "owner/open", "/srv/repos/open", driven: false, CancellationToken.None);

        Assert.Equal(PoolChoice.Pool, decision.Choice);
        Assert.Equal("public-free", decision.Preset);
    }

    [Fact]
    public async Task APresetThatIsNotReplacedRunsAsBefore()
    {
        var decision = await Pool(new FakeVisibility(_ => RepositoryVisibility.Public)).DecideAsync("code", "owner/open", "/srv/repos/open", driven: false, CancellationToken.None);

        Assert.Equal(PoolChoice.Standard, decision.Choice);
    }

    public static TheoryData<string> NonPublic() => new()
    {
        { nameof(RepositoryVisibility.Private) }, { nameof(RepositoryVisibility.Internal) }, { nameof(RepositoryVisibility.Unknown) }, { "throws" }, { "cancelled-by-timeout" }, { "undefined-value" },
    };

    [Theory]
    [MemberData(nameof(NonPublic))]
    public async Task NonPublicNeverSelectsThePool(string outcome)
    {
        IRepositoryVisibility visibility = outcome switch
        {
            "throws" => new ThrowingVisibility(new HttpRequestException("boom")),
            "cancelled-by-timeout" => new ThrowingVisibility(new TaskCanceledException("timeout")),
            "undefined-value" => new FakeVisibility(_ => (RepositoryVisibility)99),
            _ => new FakeVisibility(_ => Enum.Parse<RepositoryVisibility>(outcome)),
        };
        var pool = Pool(visibility);

        foreach (var preset in new[] { "cheap", "default", "public-free" })
        {
            var decision = await pool.DecideAsync(preset, "owner/closed", "/srv/repos/closed", driven: false, CancellationToken.None);

            Assert.NotEqual(PoolChoice.Pool, decision.Choice);
            // Asking for the pool by name is refused, never run on the pool and never quietly run elsewhere.
            Assert.Equal(preset == "public-free" ? PoolChoice.Refused : PoolChoice.Standard, decision.Choice);
        }
        Assert.False(await pool.QualifiesAsync("owner/closed", "/srv/repos/closed", CancellationToken.None));
    }

    [Fact]
    public async Task WithoutAForgeNameOrUnderWorkNothingQualifies()
    {
        var public_ = new FakeVisibility(_ => RepositoryVisibility.Public);
        var pool = Pool(public_);

        Assert.False(await pool.QualifiesAsync(null, "/srv/repos/open", CancellationToken.None));
        Assert.False(await pool.QualifiesAsync("", "/srv/repos/open", CancellationToken.None));
        Assert.False(await pool.QualifiesAsync("owner/open", "/home/me/Projects/work/open", CancellationToken.None));
        Assert.False(await pool.QualifiesAsync("owner/open", "/home/me/Projects/WORK/open", CancellationToken.None));
        Assert.False(await pool.QualifiesAsync("owner/open", null, CancellationToken.None));
        Assert.False(await pool.QualifiesAsync("not a forge name", "/srv/repos/open", CancellationToken.None));
        Assert.Equal(0, public_.Calls);
        Assert.True(await pool.QualifiesAsync("owner/open", "/home/me/Projects/personal/open", CancellationToken.None));
        Assert.Equal(PoolChoice.Refused, (await pool.DecideAsync("public-free", "owner/open", "/home/me/Projects/work/open", driven: false, CancellationToken.None)).Choice);
    }

    [Fact]
    public async Task RoutingOffRefusesEvenANamedPoolPreset()
    {
        var pool = Pool(new FakeVisibility(_ => RepositoryVisibility.Public), enabled: false);

        Assert.Equal(PoolChoice.Standard, (await pool.DecideAsync("cheap", "owner/open", "/srv/repos/open", driven: false, CancellationToken.None)).Choice);
        var named = await pool.DecideAsync("public-free", "owner/open", "/srv/repos/open", driven: false, CancellationToken.None);
        Assert.Equal(PoolChoice.Refused, named.Choice);
        Assert.Equal("public_free_refused", named.Code);
        Assert.False(await pool.QualifiesAsync("owner/open", "/srv/repos/open", CancellationToken.None));
    }

    [Fact]
    public async Task ADrivenRequestNeverUsesThePool()
    {
        var pool = Pool(new FakeVisibility(_ => RepositoryVisibility.Public));

        Assert.Equal(PoolChoice.Standard, (await pool.DecideAsync("cheap", "owner/open", "/srv/repos/open", driven: true, CancellationToken.None)).Choice);
        Assert.Equal(PoolChoice.Refused, (await pool.DecideAsync("public-free", "owner/open", "/srv/repos/open", driven: true, CancellationToken.None)).Choice);
    }

    [Fact]
    public async Task OnlyAPositiveAnswerIsRemembered()
    {
        var clock = new ManualTimeProvider();
        var answers = new Queue<RepositoryVisibility>([RepositoryVisibility.Unknown, RepositoryVisibility.Public, RepositoryVisibility.Private]);
        var visibility = new FakeVisibility(_ => answers.Dequeue());
        var pool = Pool(visibility, clock: clock);

        Assert.False(await pool.QualifiesAsync("owner/open", "/srv/repos/open", CancellationToken.None));
        Assert.True(await pool.QualifiesAsync("owner/open", "/srv/repos/open", CancellationToken.None));
        Assert.True(await pool.QualifiesAsync("owner/open", "/srv/repos/open", CancellationToken.None));
        Assert.Equal(2, visibility.Calls);

        clock.Advance(PublicFreePool.PublicAnswerLasts + TimeSpan.FromSeconds(1));
        Assert.False(await pool.QualifiesAsync("owner/open", "/srv/repos/open", CancellationToken.None));
        Assert.Equal(3, visibility.Calls);
    }

    [Fact]
    public void TheSettingsNameASecretAndAGatewayNotAKey()
    {
        Assert.Empty(Settings.Problems());
        Assert.Contains(new PublicFreeSettings(null, "https://g.example").Problems(), p => p.Contains("api_key_secret"));
        Assert.Contains(new PublicFreeSettings("item", "not a url").Problems(), p => p.Contains("base_url"));
        Assert.Contains(new PublicFreeSettings("item", "https://g.example", Preset: "").Problems(), p => p.Contains("preset"));
        Assert.Contains(new PublicFreeSettings("item", "https://g.example", Replaces: ["public-free"]).Problems(), p => p.Contains("replaces"));
    }
}

/// <summary>What GitHub's repository answer means (ADR 0093): only <c>visibility: public</c> with <c>private: false</c> is public.</summary>
public class GitHubRepositoryVisibilityTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private static Task<RepositoryVisibility> Lookup(HttpResponseMessage response, string forge = "owner/name") =>
        new GitHubRepositoryVisibility(new HttpClient(new Handler(_ => response)), null).LookupAsync(forge, CancellationToken.None);

    [Fact]
    public async Task OnlyPublicAndNotPrivateIsPublic()
    {
        Assert.Equal(RepositoryVisibility.Public, await Lookup(Json(HttpStatusCode.OK, """{"visibility":"public","private":false}""")));
        Assert.Equal(RepositoryVisibility.Private, await Lookup(Json(HttpStatusCode.OK, """{"visibility":"private","private":true}""")));
        Assert.Equal(RepositoryVisibility.Internal, await Lookup(Json(HttpStatusCode.OK, """{"visibility":"internal","private":true}""")));
        // The two fields must agree: a public visibility on a private repository is not public.
        Assert.Equal(RepositoryVisibility.Private, await Lookup(Json(HttpStatusCode.OK, """{"visibility":"public","private":true}""")));
        Assert.Equal(RepositoryVisibility.Unknown, await Lookup(Json(HttpStatusCode.OK, """{"visibility":"public"}""")));
        Assert.Equal(RepositoryVisibility.Unknown, await Lookup(Json(HttpStatusCode.OK, """{"private":false}""")));
    }

    [Fact]
    public async Task ErrorsAndOddAnswersAreUnknown()
    {
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway })
            Assert.Equal(RepositoryVisibility.Unknown, await Lookup(Json(status, """{"visibility":"public","private":false}""")));
        Assert.Equal(RepositoryVisibility.Unknown, await Lookup(Json(HttpStatusCode.OK, "not json")));
        Assert.Equal(RepositoryVisibility.Unknown, await Lookup(Json(HttpStatusCode.OK, "[]")));
        Assert.Equal(RepositoryVisibility.Unknown, await Lookup(Json(HttpStatusCode.OK, """{"visibility":"PUBLIC ","private":false}""")));
        Assert.Equal(RepositoryVisibility.Unknown, await Lookup(Json(HttpStatusCode.OK, """{"visibility":"public","private":false}"""), forge: "../etc/passwd"));
        var throwing = new GitHubRepositoryVisibility(new HttpClient(new Handler(_ => throw new HttpRequestException("down"))), null);
        Assert.Equal(RepositoryVisibility.Unknown, await throwing.LookupAsync("owner/name", CancellationToken.None));
    }

    [Fact]
    public async Task TheRequestNamesTheRepositoryAndCarriesTheTokenOnlyWhenOneIsGiven()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, """{"visibility":"public","private":false}"""));
        await new GitHubRepositoryVisibility(new HttpClient(handler), "tok").LookupAsync("owner/name", CancellationToken.None);
        Assert.Equal("https://api.github.com/repos/owner/name", handler.Seen!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Seen.Headers.Authorization!.Scheme);

        await new GitHubRepositoryVisibility(new HttpClient(handler), null).LookupAsync("owner/name", CancellationToken.None);
        Assert.Null(handler.Seen!.Headers.Authorization);
    }
}

/// <summary>The console's switch (ADR 0093): off until the owner turns it on, and kept across a restart.</summary>
public class PublicFreeSettingTests
{
    [Fact]
    public void DefaultIsOffAndIsKept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"public-free-{Guid.NewGuid():N}", "limits", "public-free.json");
        try
        {
            Assert.False(new PublicFreeSwitch(path).Enabled);
            new PublicFreeSwitch(path).Set(true);
            Assert.True(new PublicFreeSwitch(path).Enabled);
            new PublicFreeSwitch(path).Set(false);
            Assert.False(new PublicFreeSwitch(path).Enabled);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(path))!, recursive: true);
        }
    }

    [Fact]
    public void AnUnreadableFileMeansOff()
    {
        var path = Path.Combine(Path.GetTempPath(), $"public-free-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.False(new PublicFreeSwitch(path).Enabled);
            File.WriteAllText(path, """{"enabled":"yes"}""");
            Assert.False(new PublicFreeSwitch(path).Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>The example profile's pool block names secrets and placeholders only (ADR 0093).</summary>
public class PublicFreeProfileTests
{
    [Fact]
    public void PublicFreeBlockIsPlaceholdersAndSecretNames()
    {
        var block = Chargehand.Config.Profile.Load(Repo.Path("profiles", "example.json")).PublicFree!;

        Assert.Empty(block.Problems());
        Assert.False(block.ApiKeySecret!.StartsWith("sk-", StringComparison.Ordinal));
        Assert.Contains("example", block.BaseUrl!, StringComparison.Ordinal);
        Assert.Equal("public-free", block.Preset);
    }
}
