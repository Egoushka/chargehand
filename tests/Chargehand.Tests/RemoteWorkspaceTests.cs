using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Chargehand.Contracts;
using Chargehand.Driven;
using Chargehand.Kernel;
using Chargehand.Modules.Runners.Api;
using Chargehand.Modules.Sessions.Api;
using Chargehand.Server;

namespace Chargehand.Tests;

/// <summary>ADR 0058: a runner on another machine is sent the repository as a bundle and gives the session's result bundle back, over the runner's HTTP API, with real git at both ends and a fake engine
/// in the middle. The bundle is streamed, size-capped and authenticated.</summary>
public sealed class RemoteWorkspaceTests : IDisposable
{
    private const string Image = "registry.example/session@sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string Key = "runner-key-for-tests";

    private readonly TempDir _dir = new();
    private readonly string _source;
    private readonly string _commit;

    /// <summary>The engine of the runner: it keeps what is written into output volumes and records the workspace it was asked for.</summary>
    private sealed class FakeEngine : IContainerEngine, IWorkspaceEngine, IBundleWorkspaceEngine, IOutVolumeEngine
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<(string Name, long Length, bool Streamed)> Writes { get; } = [];
        public List<byte[]> Sent { get; } = [];
        public BundleWorkspaceSpec? Prepared { get; private set; }
        public WorkspaceSpec? PreparedFromPath { get; private set; }

        public bool FailEmptying { get; set; }

        public Task WriteOutFileAsync(OutFileSpec spec, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            Calls.Add($"write {spec.Name}");
            Files[$"{spec.OutVolume}/{spec.Name}"] = content.ToArray();
            Writes.Add((spec.Name, content.Length, false));
            return Task.CompletedTask;
        }

        public async Task WriteOutStreamAsync(OutFileSpec spec, Stream content, long length, CancellationToken ct)
        {
            if (FailEmptying && length == 0)
                throw new ChargehandException(ErrorCode.ContainerUnavailable, "docker run of the helper failed");
            Calls.Add($"stream {spec.Name}");
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            Files[$"{spec.OutVolume}/{spec.Name}"] = buffer.ToArray();
            Sent.Add(buffer.ToArray());
            Writes.Add((spec.Name, buffer.Length, true));
        }

        public async Task<bool> ReadOutFileAsync(OutFileSpec spec, Stream destination, CancellationToken ct)
        {
            Calls.Add($"read {spec.Name}");
            if (!Files.TryGetValue($"{spec.OutVolume}/{spec.Name}", out var bytes))
                return false;
            await destination.WriteAsync(bytes, ct);
            return true;
        }

        public Task PrepareBundleWorkspaceAsync(BundleWorkspaceSpec spec, CancellationToken ct)
        {
            Calls.Add("prepare-bundle");
            Prepared = spec;
            return Task.CompletedTask;
        }

        public Task PrepareWorkspaceAsync(WorkspaceSpec spec, CancellationToken ct)
        {
            Calls.Add("prepare-path");
            PreparedFromPath = spec;
            return Task.CompletedTask;
        }

        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task SignalAsync(string id, string signal, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveNetworkAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineResources> ListAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Remote(FakeEngine engine, Microsoft.AspNetCore.Builder.WebApplication app, Func<long, HttpClient> client) : IAsyncDisposable
    {
        public FakeEngine Engine { get; } = engine;
        public Uri Address { get; } = new(app.Urls.First());
        public RunnerClient Client(long maxDownloadBytes = DockerCliEngine.OutFileCap) => new(client(0), maxDownloadBytes);
        public HttpClient Http { get; } = client(0);
        public async ValueTask DisposeAsync() { Http.Dispose(); await app.DisposeAsync(); }
    }

    public RemoteWorkspaceTests()
    {
        _source = Path.Combine(_dir.Path, "checkout");
        Directory.CreateDirectory(_source);
        TestGit.Run(_source, "init", "-q", "-b", "main");
        File.WriteAllText(Path.Combine(_source, "a.txt"), "one\n");
        TestGit.Run(_source, "add", "-A");
        TestGit.Run(_source, "commit", "-q", "-m", "one");
        File.WriteAllText(Path.Combine(_source, "b.txt"), "two\n");
        TestGit.Run(_source, "add", "-A");
        TestGit.Run(_source, "commit", "-q", "-m", "two");
        _commit = TestGit.Run(_source, "rev-parse", "HEAD").Trim();
        TestGit.Run(_source, "checkout", "-q", "--detach", _commit);
    }

    public void Dispose() => _dir.Dispose();

    private static async Task<Remote> Start(long maxBundleBytes = RunnerPolicy.DefaultMaxBundleBytes, IReadOnlyList<string>? sourceRoots = null)
    {
        var engine = new FakeEngine();
        var app = RunnerServer.Create(new RunnerSettings(0, Key, new RunnerPolicy([Image], "registry.example/chargehand@sha256:2222222222222222222222222222222222222222222222222222222222222222",
            SourceRoots: sourceRoots, MaxBundleBytes: maxBundleBytes)), engine);
        await app.StartAsync();
        var address = new Uri(app.Urls.First());
        return new Remote(engine, app, _ =>
        {
            var http = new HttpClient { BaseAddress = address, Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            return http;
        });
    }

    private WorkspaceSpec Spec(string run = "run1") => new(run, Image, _source, $"chargehand-work-{run}", $"chargehand/{run}", _commit);

    // --- the repository goes to the runner

    [Fact]
    public async Task The_repository_is_streamed_to_the_runner_as_a_bundle_and_the_workspace_is_made_from_it_with_no_source_root()
    {
        await using var remote = await Start(sourceRoots: []);   // a path workspace would be refused here
        var client = remote.Client();
        var workspace = new BundleWorkspaceEngine(client, client, new RepositoryBundles(Path.Combine(_dir.Path, "bundles")));

        await workspace.PrepareWorkspaceAsync(Spec(), default);

        Assert.Equal(["stream source.bundle", "prepare-bundle", "stream source.bundle"], remote.Engine.Calls);
        var write = remote.Engine.Writes[0];
        Assert.True(write.Streamed);
        var sent = remote.Engine.Sent[0];
        Assert.Equal(write.Length, sent.Length);
        Assert.Equal(new FileInfo(Path.Combine(_dir.Path, "bundles", $"{_commit}.bundle")).Length, sent.Length);
        // Once the workspace is made the file is emptied, so a kept output volume does not hold the repository.
        Assert.Empty(remote.Engine.Files["chargehand-out-run1/source.bundle"]);
        Assert.Equal(new BundleWorkspaceSpec("run1", Image, "chargehand-out-run1", "chargehand-work-run1", "chargehand/run1", _commit), remote.Engine.Prepared);
        // What arrived is a bundle git accepts, and a clone of it is the repository at the commit.
        var received = Path.Combine(_dir.Path, "received.bundle");
        await File.WriteAllBytesAsync(received, sent);
        TestGit.Run(_source, "bundle", "verify", received);
        var clone = Path.Combine(_dir.Path, "clone");
        TestGit.Run(_dir.Path, "clone", "-q", "--no-checkout", received, clone);
        TestGit.Run(clone, "checkout", "-q", "-b", "chargehand/run1", _commit);
        Assert.Equal(["two", "one"], TestGit.Run(clone, "log", "--format=%s").Trim().Split('\n'));
    }

    [Fact]
    public async Task A_runner_that_will_not_empty_the_file_afterwards_does_not_fail_the_workspace()
    {
        await using var remote = await Start();
        remote.Engine.FailEmptying = true;
        var client = remote.Client();
        var workspace = new BundleWorkspaceEngine(client, client, new RepositoryBundles(Path.Combine(_dir.Path, "bundles")));

        await workspace.PrepareWorkspaceAsync(Spec(), default);

        Assert.NotNull(remote.Engine.Prepared);
    }

    [Fact]
    public async Task A_bundle_over_the_runners_limit_is_refused_before_it_is_read_and_nothing_reaches_the_engine()
    {
        await using var remote = await Start(maxBundleBytes: 100);
        var client = remote.Client();
        var workspace = new BundleWorkspaceEngine(client, client, new RepositoryBundles(Path.Combine(_dir.Path, "bundles")));

        var e = await Assert.ThrowsAsync<ChargehandException>(() => workspace.PrepareWorkspaceAsync(Spec(), default));

        Assert.IsNotType<RunnerUnreachableException>(e);
        Assert.Contains("413", e.Message);
        Assert.Contains("above the runner's limit of 100 bytes (--max-bundle-mb)", e.Message);
        Assert.Empty(remote.Engine.Calls);
    }

    [Fact]
    public async Task A_bundle_without_a_length_is_refused()
    {
        await using var remote = await Start();
        using var chunked = new HttpRequestMessage(HttpMethod.Put, $"/out/run1/source.bundle?image={Uri.EscapeDataString(Image)}") { Content = new PushStreamContent() };
        chunked.Headers.TransferEncodingChunked = true;

        var response = await remote.Http.SendAsync(chunked);

        Assert.Equal(HttpStatusCode.LengthRequired, response.StatusCode);
        Assert.Empty(remote.Engine.Calls);
    }

    private sealed class PushStreamContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync("bundle"u8.ToArray()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task The_new_routes_need_the_key_and_a_listed_image_and_the_runs_own_volumes()
    {
        await using var remote = await Start();
        var other = "registry.example/other@sha256:3333333333333333333333333333333333333333333333333333333333333333";
        using var anonymous = new HttpClient { BaseAddress = remote.Address };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync($"/out/run1/source.bundle?image={Uri.EscapeDataString(Image)}", new ByteArrayContent([1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/workspace/bundle", Json(Body()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.Http.PostAsync("/workspace/bundle", Json(Body(image: other)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.Http.PostAsync("/workspace/bundle", Json(Body(work: "chargehand-work-other")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await remote.Http.PutAsync($"/out/run1/source.bundle?image={Uri.EscapeDataString(other)}", new ByteArrayContent([1]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await remote.Http.PostAsync("/workspace/bundle", Json(Body(extra: ",\"source_path\":\"/etc\"")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await remote.Http.PostAsync("/workspace/bundle", Json(Body(branch: "main")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await remote.Http.PostAsync("/workspace/bundle", Json(Body(commit: "$(id)")))).StatusCode);
        Assert.Empty(remote.Engine.Calls);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Body(string image = Image, string work = "chargehand-work-run1", string branch = "chargehand/run1", string commit = "abc1234def5678abc1234def5678abc1234def56", string extra = "") =>
        $$"""{"run_id":"run1","image":"{{image}}","work_volume":"{{work}}","branch":"{{branch}}","commit":"{{commit}}"{{extra}}}""";

    [Fact]
    public async Task A_valid_bundle_workspace_request_reaches_the_engine_with_the_volumes_the_runner_derives()
    {
        await using var remote = await Start();

        var response = await remote.Http.PostAsync("/workspace/bundle", Json(Body()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new BundleWorkspaceSpec("run1", Image, "chargehand-out-run1", "chargehand-work-run1", "chargehand/run1", "abc1234def5678abc1234def5678abc1234def56"), remote.Engine.Prepared);
    }

    [Fact]
    public async Task A_path_workspace_is_still_refused_without_source_roots_so_a_remote_runner_cannot_be_asked_to_mount_a_host_directory()
    {
        await using var remote = await Start(sourceRoots: []);

        var response = await remote.Http.PostAsync("/workspace", Json($$"""{"run_id":"run1","image":"{{Image}}","source_path":"/etc","work_volume":"chargehand-work-run1","branch":"chargehand/run1","commit":"abc1234"}"""));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(remote.Engine.Calls);
    }

    [Fact]
    public async Task The_verification_asks_for_the_workspace_again_in_the_same_volume_and_gets_the_bundle_sent_again_from_the_same_file()
    {
        await using var remote = await Start();
        var client = remote.Client();
        var bundles = new RepositoryBundles(Path.Combine(_dir.Path, "bundles"));
        var workspace = new BundleWorkspaceEngine(client, client, bundles);

        await workspace.PrepareWorkspaceAsync(Spec(), default);            // the session's
        var made = File.GetLastWriteTimeUtc(await bundles.BundleAsync(_source, _commit, default));
        remote.Engine.Files["chargehand-out-run1/source.bundle"] = "the session overwrote it"u8.ToArray();
        await workspace.PrepareWorkspaceAsync(Spec() with { Branch = "chargehand/run1" }, default);   // the handover's verification, in the same run's volume

        Assert.Equal(["stream source.bundle", "prepare-bundle", "stream source.bundle", "stream source.bundle", "prepare-bundle", "stream source.bundle"], remote.Engine.Calls);
        Assert.NotEqual("the session overwrote it", Encoding.UTF8.GetString(remote.Engine.Sent[2]));
        Assert.Equal(remote.Engine.Sent[0], remote.Engine.Sent[2]);
        Assert.Equal(made, File.GetLastWriteTimeUtc(Path.Combine(_dir.Path, "bundles", $"{_commit}.bundle")));
    }

    // --- the result comes back

    [Fact]
    public async Task The_sessions_result_bundle_comes_back_byte_for_byte_and_the_rest_of_the_output_with_it()
    {
        await using var remote = await Start();
        var work = Path.Combine(_dir.Path, "session");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "c.txt"), "three\n");
        TestGit.Run(work, "init", "-q", "-b", "main");
        TestGit.Run(work, "add", "-A");
        TestGit.Run(work, "commit", "-q", "-m", "three");
        var resultBundle = Path.Combine(_dir.Path, "result.bundle");
        TestGit.Run(work, "bundle", "create", resultBundle, "main");
        var bytes = await File.ReadAllBytesAsync(resultBundle);
        remote.Engine.Files["chargehand-out-run1/chargehand.bundle"] = bytes;
        remote.Engine.Files["chargehand-out-run1/session-outcome.json"] = """{"status":"completed"}"""u8.ToArray();
        var volumes = new EngineSessionVolumes(remote.Client(), Image);
        var into = Path.Combine(_dir.Path, "out");
        Directory.CreateDirectory(into);

        await volumes.FetchOutputAsync("run1", into, default);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(into, "chargehand.bundle")));
        Assert.Equal("""{"status":"completed"}""", await File.ReadAllTextAsync(Path.Combine(into, "session-outcome.json")));
        Assert.False(File.Exists(Path.Combine(into, "driven-report.json")));       // the session wrote none
        TestGit.Run(work, "bundle", "verify", Path.Combine(into, "chargehand.bundle"));
    }

    [Fact]
    public async Task A_result_file_over_the_download_limit_is_refused_before_it_is_copied()
    {
        await using var remote = await Start();
        remote.Engine.Files["chargehand-out-run1/chargehand.bundle"] = new byte[2 * 1024 * 1024];
        using var destination = new MemoryStream();

        var e = await Assert.ThrowsAsync<ChargehandException>(() => remote.Client(maxDownloadBytes: 1024 * 1024).ReadOutFileAsync(new OutFileSpec("run1", Image, "chargehand-out-run1", "chargehand.bundle"), destination, default));

        Assert.Contains("larger than 1 MiB", e.Message);
        Assert.Equal(0, destination.Length);
    }

    [Fact]
    public async Task A_runner_that_cannot_prepare_a_bundle_workspace_says_so()
    {
        var engine = new NoBundleEngine();
        var app = RunnerServer.Create(new RunnerSettings(0, Key, new RunnerPolicy([Image], "registry.example/chargehand@sha256:2222222222222222222222222222222222222222222222222222222222222222")), engine);
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);

            var response = await http.PostAsync("/workspace/bundle", Json(Body()));

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private sealed class NoBundleEngine : IContainerEngine
    {
        public Task<string> StartAsync(ContainerSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task SignalAsync(string id, string signal, CancellationToken ct) => throw new NotSupportedException();
        public Task<ContainerState> InspectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task KillAllAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string> LogsTailAsync(string id, int bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateVolumeAsync(string name, string runId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveVolumeAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateNetworkAsync(string name, string batchId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveNetworkAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> StartEgressAsync(EgressSpec spec, CancellationToken ct) => throw new NotSupportedException();
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> OwnsAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineResources> ListAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}
