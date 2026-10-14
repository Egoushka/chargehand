using System.Text;
using Chargehand.Contracts;
using Chargehand.Kernel;
using Chargehand.Modules.Gateway.Api;

namespace Chargehand.Modules.Batches.Api;

/// <summary>The key a batch's egress container signs its run tokens with (ADR 0039), one file per batch beside the journal, which keeps secrets out (ADR 0061, amendment of 2026-10-14). The file is
/// written once, readable by its owner only, and deleted with the batch's steps. Its value is never logged or journaled.</summary>
public static class TokenKeyFile
{
    private const string Extension = ".token-key";

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>The file of <paramref name="batch"/>'s key in <paramref name="directory"/>.</summary>
    public static string PathOf(string directory, string batch) => Path.Combine(directory, batch + Extension);

    /// <summary>The batch's key: the one its file holds, else a new one written to a new file.</summary>
    /// <exception cref="ChargehandException">The file can be read or written by anyone but its owner: the batch does not start with a key others may have read.</exception>
    public static string ReadOrCreate(string directory, string batch)
    {
        var path = PathOf(directory, batch);
        if (Read(path) is { } kept)
            return kept;
        var key = ModelTokens.NewKey();
        return WriteOnce(directory, path, key) ? key : Read(path)!;
    }

    /// <summary>Deletes the batch's key file, if it has one.</summary>
    public static void Delete(string directory, string batch) => File.Delete(PathOf(directory, batch));

    /// <summary>One release only (2026-10-14): the key a snapshot held, written to the batch's file unless it has one.</summary>
    internal static void Import(string directory, string batch, string key)
    {
        var path = PathOf(directory, batch);
        if (!File.Exists(path))
            WriteOnce(directory, path, key);
    }

    /// <summary>Writes a temporary file, owner-only from its creation, flushes it and links it under <paramref name="path"/>: a crash leaves no file or a whole one, and a key once written is never
    /// replaced.</summary>
    /// <returns>False when another writer's file was there first.</returns>
    private static bool WriteOnce(string directory, string path, string key)
    {
        Directory.CreateDirectory(directory);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnly;
        using (var file = new FileStream(temp, options))
        {
            file.Write(Encoding.ASCII.GetBytes(key));
            file.Flush(flushToDisk: true);
        }
        try
        {
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
            return false;
        }
    }

    private static string? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & ~OwnerOnly) != 0)
            throw new ChargehandException(ErrorCode.CredentialUnavailable, $"the token key file {Path.GetFileName(path)} can be read or written by others than its owner",
                "Delete the file to end the batch, or make it readable by its owner only (chmod 600).");
        return File.ReadAllText(path).Trim();
    }
}
