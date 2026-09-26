using System.Text.RegularExpressions;
using Chargehand.Contracts;

namespace Chargehand.Prompts;

/// <summary>Blocks from <c>prompts/&lt;name&gt;.md</c>, each starting with a front matter <c>version:</c> (SemVer).</summary>
public sealed partial class PromptRegistry(string directory) : IPromptRegistry
{
    public PromptBlock Get(string name)
    {
        var path = Path.Combine(directory, name + ".md");
        var raw = File.ReadAllText(path).ReplaceLineEndings("\n");
        var m = FrontMatter().Match(raw);
        if (!m.Success)
            throw new InvalidDataException($"{path}: expected front matter with 'version: X.Y.Z'.");
        var text = raw[m.Length..];
        return new PromptBlock(name, m.Groups["v"].Value, PromptBlock.Hash(text), text);
    }

    public IReadOnlyList<PromptBlock> ForNode(string preset, string nodeKind) => [Get($"core/{nodeKind}"), Get($"preset/{preset}")];

    [GeneratedRegex(@"^---\n(?:.*\n)*?version:\s*(?<v>\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)\s*\n(?:.*\n)*?---\n")]
    private static partial Regex FrontMatter();
}
