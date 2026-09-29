using System.Security.Cryptography;
using Chargehand.Contracts;

namespace Chargehand.Signing;

/// <summary><c>chargehand verify &lt;result.json&gt; --public-key &lt;key.pem&gt;</c>: offline, no profile, no network (ADR 0036).
/// Exit codes: 0 valid, 1 invalid or unsigned, 2 usage or an unreadable file.</summary>
public static class VerifyCli
{
    public const string Usage = "usage: chargehand verify <result.json> --public-key <key.pem>";

    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        if (args is not [var file, "--public-key", var keyFile])
        {
            error.WriteLine(Usage);
            return 2;
        }
        string text;
        ECDsa key;
        try
        {
            text = File.ReadAllText(file);
            key = ECDsa.Create();
            key.ImportFromPem(File.ReadAllText(keyFile));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            error.WriteLine($"cannot read {(e is CryptographicException or ArgumentException ? keyFile : e is FileNotFoundException fe ? fe.FileName : file)}: {e.Message}");
            return 2;
        }
        using (key)
        {
            var check = ResultSignature.Verify(text, key);
            if (check.Valid)
            {
                output.WriteLine($"valid: signed with key {ResultSignature.KeyId(key)}");
                return 0;
            }
            error.WriteLine($"INVALID: {check.Reason}");
            return 1;
        }
    }
}
