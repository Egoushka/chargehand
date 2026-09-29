using System.Security.Cryptography;
using Chargehand.Contracts;

namespace Chargehand.Signing;

/// <param name="KeyFile">A P-256 private key in PEM (PKCS#8 or SEC1). Chargehand never creates or stores a key (ADR 0036).</param>
public sealed record SigningSettings(string? KeyFile = null);

/// <summary>Loads the key a run signs its result with (ADR 0036). None configured: results stay unsigned.</summary>
public static class ResultSigner
{
    public const string KeyFileVariable = "CHARGEHAND_SIGNING_KEY_FILE";

    private const string P256 = "1.2.840.10045.3.1.7";

    private const string Openssl = "openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out signing-key.pem";

    /// <summary>The key, or null when neither the profile nor <c>CHARGEHAND_SIGNING_KEY_FILE</c> names a file. An unreadable or
    /// wrong-curve key throws <see cref="ChargehandException"/> (<c>invalid_request</c>): a run must not return a result the caller
    /// believed would be signed and is not.</summary>
    public static ECDsa? Load(SigningSettings? settings, Func<string, string?> env)
    {
        var path = env(KeyFileVariable) is { Length: > 0 } fromEnv ? fromEnv : settings?.KeyFile;
        if (string.IsNullOrEmpty(path))
            return null;
        var action = $"Point signing.key_file (or {KeyFileVariable}) at a P-256 private key in PEM. To make one: {Openssl}. Chargehand never creates a key for you.";
        if (!File.Exists(path))
            throw new ChargehandException(ErrorCode.InvalidRequest, $"the signing key file {path} does not exist", action);
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
            if (key.ExportParameters(false).Curve.Oid.Value != P256)
                throw new ChargehandException(ErrorCode.InvalidRequest, $"the signing key in {path} is not on curve P-256, the only one ES256 uses", action);
            return key;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            key.Dispose();
            throw new ChargehandException(ErrorCode.InvalidRequest, $"the signing key file {path} is not a usable P-256 PEM private key", action);
        }
        catch (ChargehandException)
        {
            key.Dispose();
            throw;
        }
    }
}
