using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;
using Chargehand.Runtime;

namespace Chargehand.Prompts;

/// <summary>Builds the prompt_chain recorded on every call (ADR 0007).</summary>
public static class PromptChains
{
    public static PromptChain Build(IEnumerable<(PromptBlock Block, BlockSource Source)> blocks, AsSent asSent) =>
        new(blocks.Select(b => new ChainBlock(b.Block.Name, b.Block.Version, b.Block.Sha256, b.Source)).ToList(), asSent);

    /// <summary>A caller's block must hash to what it claims, or the recorded chain would lie.</summary>
    public static PromptBlock VerifyCallerBlock(PromptBlock block) =>
        PromptBlock.Hash(block.Text) == block.Sha256
            ? block
            : throw new ChargehandException(ErrorCode.InvalidRequest, $"caller block '{block.Name}': sha256 does not match its text.",
                "Send the SHA-256 of the block's text, lower-case hex.");

    /// <summary>Proxy for OpenCode's tool catalog, which is a function of its version, the agent and the ruleset.</summary>
    public static string ToolsSha256(string opencodeVersion, string agent, IReadOnlyList<PermissionRule> rules) =>
        Hash(JsonSerializer.Serialize(new { opencodeVersion, agent, rules = rules.Select(r => new[] { r.Action, r.Resource, r.Effect.ToString() }) }));

    public static string InstructionsSha256(IReadOnlyList<(string Key, string Value)> entries) =>
        Hash(JsonSerializer.Serialize(entries.Select(e => new[] { e.Key, e.Value })));

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
