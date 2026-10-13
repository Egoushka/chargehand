namespace Chargehand.Runtimes.OpenCode.Api;

public sealed record OpenCodeSettings(string Url, string PasswordSecret, string Version, string? Binary = null);
