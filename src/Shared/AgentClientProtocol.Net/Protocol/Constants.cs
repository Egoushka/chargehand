namespace AgentClientProtocol;

/// <summary>Why a prompt turn ended. The wire carries a string, so a value a newer agent adds still reaches the caller.</summary>
public static class AcpStopReason
{
    public const string EndTurn = "end_turn";
    public const string MaxTokens = "max_tokens";
    public const string MaxTurnRequests = "max_turn_requests";
    public const string Refusal = "refusal";
    public const string Cancelled = "cancelled";
}

/// <summary>The kind of a tool call.</summary>
public static class AcpToolKind
{
    public const string Read = "read";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Move = "move";
    public const string Search = "search";
    public const string Execute = "execute";
    public const string Think = "think";
    public const string Fetch = "fetch";
    public const string SwitchMode = "switch_mode";
    public const string Other = "other";
}

/// <summary>The state of a tool call.</summary>
public static class AcpToolStatus
{
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>What a permission option does when selected.</summary>
public static class AcpPermissionKind
{
    public const string AllowOnce = "allow_once";
    public const string AllowAlways = "allow_always";
    public const string RejectOnce = "reject_once";
    public const string RejectAlways = "reject_always";
}
