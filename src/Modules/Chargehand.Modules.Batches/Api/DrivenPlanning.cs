using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Batches.Api;

/// <summary>The planning step before a batch (ADR 0049): one read-only run through the orchestrator's own pipeline, on a cheap preset, so no new model client or credential is involved.</summary>
/// <param name="Enabled">Plan every batch; a request's <c>driven.plan</c> overrides it either way. Off by default.</param>
/// <param name="Preset">The preset the run uses; it must be read-only. Default <c>cheap</c>.</param>
public sealed record DrivenPlanning(bool Enabled = false, string Preset = "cheap")
{
    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Preset))
            throw new ChargehandException(ErrorCode.InvalidRequest, "driven.planning: preset must not be empty", "Fix driven.planning in the profile; docs/guide/driven.md lists the keys.");
    }
}
