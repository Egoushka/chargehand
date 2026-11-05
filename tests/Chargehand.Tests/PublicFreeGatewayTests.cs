using Chargehand.ClaudeCode;
using Chargehand.Config;
using Chargehand.Modules.Accounts.Api;
using Chargehand.Nodes;
using Chargehand.Runtime;

namespace Chargehand.Tests;

/// <summary>How the pool talks to the gateway (ADR 0093): the model ids it sends and the header that carries its key. Both are read from the gateway's config, not tested live.</summary>
public sealed class PublicFreeGatewayTests : IDisposable
{
    private const string Key = "vk-pool-secret-91";

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Theory]
    [InlineData("openrouter-free/google/gemma-4-26b-a4b-it:free", "openrouter-free", "google/gemma-4-26b-a4b-it:free")]
    [InlineData("mistral-free/mistral-medium-latest", "mistral-free", "mistral-medium-latest")]
    [InlineData("cloudflare/@cf/openai/gpt-oss-20b", "cloudflare", "@cf/openai/gpt-oss-20b")]
    [InlineData("groq/openai/gpt-oss-20b", "groq", "openai/gpt-oss-20b")]
    public void A_pool_model_id_is_split_at_the_first_slash_only_and_resolves_through_the_models_map(string id, string provider, string model)
    {
        var profile = Runs.Profile(_dir.Path) with { Models = new Dictionary<string, string> { ["provider/free-model"] = id } };

        var resolved = profile.ResolveModel("provider/free-model");

        Assert.Equal(id, resolved);
        Assert.Equal(new ModelRef(provider, model), Orchestrator.ParseModel(resolved));
        Assert.Equal(new ModelRef(provider, model), Orchestrator.ParseModel(id));
    }

    [Theory]
    [InlineData("openrouter-free/google/gemma-4-26b-a4b-it:free")]
    [InlineData("cloudflare/@cf/openai/gpt-oss-20b")]
    public async Task The_pool_sends_the_whole_provider_prefixed_id_to_the_cli_and_other_runtimes_send_the_model_alone(string id)
    {
        var model = Orchestrator.ParseModel(id)!;
        var claude = FakeClaude("");
        var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("other-key", false), CancellationToken.None);
        var pool = rt.WithCredential(new ClaudeCodeCredential(Key, false), new Uri("https://gateway.example/anthropic"), PublicFreeSettings.KeyHeaderLine(Key), prefixProvider: true);

        await Run(pool, model);
        Assert.Equal(id, ModelArg());

        await Run(rt.WithCredential(new ClaudeCodeCredential(Key, false), new Uri("https://gateway.example/anthropic")), model);
        Assert.Equal(model.ModelId, ModelArg());
    }

    [Fact]
    public async Task The_pool_carries_its_key_in_the_x_bf_vk_header_and_replaces_an_inherited_list()
    {
        var claude = FakeClaude("");
        Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", "x-inherited: 1");
        try
        {
            var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("other-key", false), CancellationToken.None);
            var pool = rt.WithCredential(new ClaudeCodeCredential(Key, false), new Uri("https://gateway.example/anthropic"), PublicFreeSettings.KeyHeaderLine(Key), prefixProvider: true);

            await Run(pool, new ModelRef("mistral-free", "mistral-medium-latest"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_CUSTOM_HEADERS", null);
        }

        Assert.Equal($"x-bf-vk: {Key}", File.ReadAllText(Path.Combine(_dir.Path, "headers.txt")));
        Assert.Equal($"api={Key} base=https://gateway.example/anthropic", File.ReadAllText(Path.Combine(_dir.Path, "env.txt")).Trim());
    }

    [Fact]
    public async Task A_gateway_that_echoes_the_key_or_the_header_value_does_not_reach_the_failure_text()
    {
        var claude = FakeClaude("""echo "401 rejected key $ANTHROPIC_API_KEY header $ANTHROPIC_CUSTOM_HEADERS" >&2; exit 2""");
        var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("other-key", false), CancellationToken.None);
        var pool = rt.WithCredential(new ClaudeCodeCredential(Key, false), new Uri("https://gateway.example/anthropic"), PublicFreeSettings.KeyHeaderLine(Key), prefixProvider: true);

        var error = await RunError(pool);

        Assert.StartsWith("claude exited 2: 401 rejected key", error, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_result_event_that_echoes_the_key_does_not_reach_the_failure_text()
    {
        var claude = FakeClaude("""echo '{"type":"result","subtype":"error","is_error":true,"result":"bad key '"$ANTHROPIC_API_KEY"'"}'""");
        var rt = await ClaudeCodeWorkerRuntime.ConnectAsync(claude, "2.1.195", new ClaudeCodeCredential("other-key", false), CancellationToken.None);
        var pool = rt.WithCredential(new ClaudeCodeCredential(Key, false), new Uri("https://gateway.example/anthropic"), PublicFreeSettings.KeyHeaderLine(Key), prefixProvider: true);

        var error = await RunError(pool);

        Assert.Contains("bad key", error, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("line\nbreak: injected")]
    [InlineData("")]
    public void A_key_that_is_not_one_token_is_refused_without_naming_it(string key)
    {
        var e = Assert.Throws<InvalidOperationException>(() => PublicFreeSettings.KeyHeaderLine(key));

        Assert.DoesNotContain("break", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("words", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_line_names_the_virtual_key_header()
    {
        Assert.Equal($"x-bf-vk: {Key}", PublicFreeSettings.KeyHeaderLine(Key));
        Assert.Equal("x-bf-vk", PublicFreeSettings.KeyHeader);
    }

    private async Task Run(ClaudeCodeWorkerRuntime rt, ModelRef model)
    {
        var session = await rt.CreateAsync(Spec(model), CancellationToken.None);
        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        Assert.Equal(IdleOutcome.Succeeded, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));
    }

    private async Task<string> RunError(ClaudeCodeWorkerRuntime rt)
    {
        var session = await rt.CreateAsync(Spec(new ModelRef("mistral-free", "mistral-medium-latest")), CancellationToken.None);
        await rt.SubmitAsync(session.Id, "hi", CancellationToken.None);
        Assert.Equal(IdleOutcome.Failed, await rt.AwaitIdleAsync(session.Id, CancellationToken.None));
        return (await rt.ReadMessagesAsync(session.Id, CancellationToken.None)).First(m => m.Kind == WorkerMessageKind.Idle).Error!;
    }

    private NodeSpec Spec(ModelRef model) => new(_dir.Path, "build", model, [], new Dictionary<string, string>());

    private string ModelArg()
    {
        var args = File.ReadAllLines(Path.Combine(_dir.Path, "args.txt"));
        return args[Array.IndexOf(args, "--model") + 1];
    }

    /// <summary>A stand-in CLI that records its arguments and the variables the pool sets, then runs <paramref name="after"/> (default: a successful result).</summary>
    private string FakeClaude(string after)
    {
        var tail = after.Length > 0 ? after : "echo '{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"hi\"}'";
        var script = _dir.Write("claude", $$"""
            #!/bin/sh
            if [ "$1" = "--version" ]; then echo "2.1.195 (Claude Code)"; exit 0; fi
            if [ "$1" = "auth" ]; then exit 0; fi
            printf '%s\n' "$@" > "{{_dir.Path}}/args.txt"
            cat > /dev/null
            printf '%s' "$ANTHROPIC_CUSTOM_HEADERS" > "{{_dir.Path}}/headers.txt"
            echo "api=$ANTHROPIC_API_KEY base=$ANTHROPIC_BASE_URL" > "{{_dir.Path}}/env.txt"
            {{tail}}
            """);
        ScriptFile.Seal(script);
        return script;
    }
}
