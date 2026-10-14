using Chargehand.Contracts;
using Chargehand.Kernel;

namespace Chargehand.Modules.Oversight.Api;

/// <summary>The learning loop (ADR 0045): each finished task leaves a record, the server reads the outcome of its pull request back from the forge, and <c>chargehand learn report</c> sums them up.
/// The records are always written; these keys tune the read-back and the optional retro.</summary>
/// <param name="Retro">After each batch, one cheap read-only run over the batch's records writes a short retro on the batch result. Off by default.</param>
/// <param name="RetroPreset">The preset the retro run uses; it must be read-only. Default <c>cheap</c>.</param>
/// <param name="PrDays">How long after a task finished its pull request is read again until it is merged or closed. Default 14.</param>
/// <param name="PrCheckMinutes">How often the server looks at the open ones. Default 60.</param>
public sealed record DrivenLearning(bool Retro = false, string RetroPreset = "cheap", int PrDays = 14, int PrCheckMinutes = 60)
{
    /// <summary>Throws an invalid_request error naming the key when a value cannot work.</summary>
    public void Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(RetroPreset)) problems.Add("retro_preset must not be empty");
        if (PrDays < 1) problems.Add("pr_days must be at least 1");
        if (PrCheckMinutes < 1) problems.Add("pr_check_minutes must be at least 1");
        if (problems.Count > 0)
            throw new ChargehandException(ErrorCode.InvalidRequest, $"driven.learning: {string.Join("; ", problems)}", "Fix driven.learning in the profile; docs/guide/driven.md lists the keys.");
    }
}
