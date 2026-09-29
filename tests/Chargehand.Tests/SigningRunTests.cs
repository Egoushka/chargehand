using System.Security.Cryptography;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.RunLog;
using Chargehand.Signing;

namespace Chargehand.Tests;

/// <summary>Goal 0.8 (ADR 0036): runs sign their result with a user-supplied key, and anyone can verify it offline.</summary>
public class SigningRunTests
{
    private static string WriteKey(TempDir dir, ECDsa key, string name = "signing-key.pem") => dir.Write(name, key.ExportPkcs8PrivateKeyPem());

    private static async Task<(ResultContract Result, ScriptedRuntime Runtime)> Run(TempDir root, SigningSettings? signing)
    {
        var repo = Runs.GitRepo(root.Path);
        var runtime = new ScriptedRuntime(Runs.WorkerReply);
        var orchestrator = new Orchestrator(Runs.Profile(root.Path) with { Signing = signing }, runtime, "2.0.16", Repo.Root,
            new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")), new Dictionary<string, int>());
        return (await orchestrator.RunAsync(Runs.CheapRequest(repo), CancellationToken.None), runtime);
    }

    private static string Json(ResultContract r) => JsonSerializer.Serialize(r, ContractJson.Options);

    [Fact]
    public async Task A_run_with_a_key_returns_a_result_anyone_with_the_public_key_can_verify()
    {
        using var root = new TempDir();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (r, _) = await Run(root, new SigningSettings(WriteKey(root, key)));

        Assert.Equal(ResultStatus.Completed, r.Status);
        Assert.NotNull(r.Signature);
        Assert.True(ResultSignature.Verify(Json(r), key).Valid);
        Assert.False(ResultSignature.Verify(Json(r).Replace(r.Summary, r.Summary + "!", StringComparison.Ordinal), key).Valid);
    }

    [Fact]
    public async Task The_run_log_holds_the_signed_result()
    {
        using var root = new TempDir();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (r, _) = await Run(root, new SigningSettings(WriteKey(root, key)));
        var logged = (await new JsonlRunLog(Path.Combine(root.Path, "log.jsonl")).ReadAsync(r.TaskId, default)).Run!.Result;
        Assert.Equal(r.Signature, logged.Signature);
    }

    [Fact]
    public async Task With_no_key_a_result_is_unsigned()
    {
        using var root = new TempDir();
        var (r, _) = await Run(root, null);
        Assert.Null(r.Signature);
    }

    [Fact]
    public async Task A_missing_key_file_stops_the_run_before_intake_with_the_way_to_make_one()
    {
        using var root = new TempDir();
        var (r, runtime) = await Run(root, new SigningSettings(Path.Combine(root.Path, "nope.pem")));
        Assert.Equal(ResultStatus.Failed, r.Status);
        Assert.Equal(ErrorCode.InvalidRequest, r.Error?.Code);
        Assert.Contains("openssl", r.Error!.Action, StringComparison.Ordinal);
        Assert.Contains("never creates a key", r.Error.Action, StringComparison.Ordinal);
        Assert.Empty(runtime.IntakePrompts);
        Assert.Null(r.Signature);
    }

    [Fact]
    public async Task A_key_on_another_curve_or_not_a_key_is_refused_the_same_way()
    {
        using var first = new TempDir();
        using var second = new TempDir();
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Contains("P-256", (await Run(first, new SigningSettings(WriteKey(first, p384, "p384.pem")))).Result.Error!.Message, StringComparison.Ordinal);
        second.Write("junk.pem", "not a key");
        Assert.Equal(ErrorCode.InvalidRequest, (await Run(second, new SigningSettings(Path.Combine(second.Path, "junk.pem")))).Result.Error?.Code);
    }

    [Fact]
    public void The_environment_variable_wins_over_the_profile()
    {
        using var dir = new TempDir();
        using var fromProfile = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var fromEnv = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profilePath = WriteKey(dir, fromProfile, "profile.pem");
        var envPath = WriteKey(dir, fromEnv, "env.pem");
        using var loaded = ResultSigner.Load(new SigningSettings(profilePath), n => n == ResultSigner.KeyFileVariable ? envPath : null)!;
        Assert.Equal(ResultSignature.KeyId(fromEnv), ResultSignature.KeyId(loaded));
        Assert.Null(ResultSigner.Load(null, _ => null));
    }

    private static (int Code, string Out, string Err) Verify(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = VerifyCli.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task The_verify_command_exits_0_for_a_signed_result_and_1_for_an_edited_or_unsigned_one()
    {
        using var root = new TempDir();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (r, _) = await Run(root, new SigningSettings(WriteKey(root, key)));
        var publicKey = root.Write("public.pem", key.ExportSubjectPublicKeyInfoPem());
        var signed = root.Write("signed.json", Json(r));
        var edited = root.Write("edited.json", Json(r).Replace(r.Summary, "Something else.", StringComparison.Ordinal));
        var unsigned = root.Write("unsigned.json", Json(r with { Signature = null }));

        var ok = Verify(signed, "--public-key", publicKey);
        Assert.Equal(0, ok.Code);
        Assert.Contains(ResultSignature.KeyId(key), ok.Out, StringComparison.Ordinal);
        var bad = Verify(edited, "--public-key", publicKey);
        Assert.Equal(1, bad.Code);
        Assert.Contains("INVALID", bad.Err, StringComparison.Ordinal);
        Assert.Equal(1, Verify(unsigned, "--public-key", publicKey).Code);
    }

    [Fact]
    public void The_verify_command_exits_2_for_a_usage_error_or_an_unreadable_file()
    {
        using var dir = new TempDir();
        Assert.Equal(2, Verify().Code);
        Assert.Equal(2, Verify("result.json").Code);
        Assert.Equal(2, Verify(Path.Combine(dir.Path, "missing.json"), "--public-key", Path.Combine(dir.Path, "missing.pem")).Code);
        var garbage = dir.Write("garbage.pem", "x");
        Assert.Equal(2, Verify(dir.Write("r.json", "{}"), "--public-key", garbage).Code);
    }
}
