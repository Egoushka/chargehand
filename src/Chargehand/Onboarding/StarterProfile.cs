using System.Text;
using System.Text.Json;

namespace Chargehand.Onboarding;

/// <summary>The profile <c>chargehand init</c> writes (profiles/example.json shows the full shape). Secrets are named by secret-store item and
/// never valued: nothing here reads a credential, so none can end up in the file.</summary>
public static class StarterProfile
{
    public const string ApiKeyItem = "chargehand-api-key";
    public const string PushItem = "chargehand-push-token";

    /// <param name="repositoryPath">The one directory workers may read repositories under.</param>
    /// <param name="allowHosts">Package hosts the driven egress allowlist still lacks.</param>
    public static string Render(string repositoryPath, IReadOnlyList<string> allowHosts)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("schema", "profile/v1");
            json.WriteStartArray("secrets");
            json.WriteStartObject();
            json.WriteBoolean("env", true);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteStartArray("repository_roots");
            json.WriteStringValue(repositoryPath);
            json.WriteEndArray();
            json.WriteString("default_preset", "default");
            json.WriteStartObject("http");
            json.WriteString("api_key_secret", ApiKeyItem);
            json.WriteNumber("port", 4300);
            json.WriteEndObject();
            json.WriteStartObject("driven");
            json.WriteBoolean("enabled", false);
            json.WriteStartArray("images");
            json.WriteEndArray();
            json.WriteStartObject("network");
            json.WriteStartArray("allow");
            foreach (var host in allowHosts)
                json.WriteStringValue(host);
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteString("push_secret", PushItem);
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>Writes <paramref name="content"/> to <paramref name="path"/>, creating its directory. An existing file is left alone unless <paramref name="force"/>.</summary>
    public static WriteOutcome Write(string path, string content, bool force)
    {
        var existed = File.Exists(path);
        if (existed && !force)
            return WriteOutcome.Exists;
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } directory)
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, content);
        return existed ? WriteOutcome.Overwritten : WriteOutcome.Written;
    }
}

public enum WriteOutcome { Written, Overwritten, Exists }
