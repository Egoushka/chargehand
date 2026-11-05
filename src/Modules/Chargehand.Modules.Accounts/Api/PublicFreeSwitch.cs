using System.Text.Json;

namespace Chargehand.Modules.Accounts.Api;

/// <summary>The owner's switch for the free-model pool (ADR 0093), kept in <c>limits/public-free.json</c> beside the run log and written whole through a temporary file. Off by default: a missing,
/// unreadable or odd file means off. Off cuts the pool entirely, a request that names the pool preset included.</summary>
public sealed class PublicFreeSwitch(string? path)
{
    private readonly Lock _lock = new();
    private bool? _enabled;

    public bool Enabled
    {
        get
        {
            lock (_lock)
                return _enabled ??= Read();
        }
    }

    public void Set(bool enabled)
    {
        lock (_lock)
        {
            if (path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var temp = $"{path}.tmp";
                File.WriteAllText(temp, enabled ? """{"enabled":true}""" : """{"enabled":false}""");
                File.Move(temp, path, overwrite: true);
            }
            _enabled = enabled;
        }
    }

    private bool Read()
    {
        try
        {
            if (path is null || !File.Exists(path))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("enabled", out var on) && on.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
