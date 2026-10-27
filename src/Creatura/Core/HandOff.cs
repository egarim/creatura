namespace Creatura;

/// <summary>Why a design hands the goal to a human. The text of each is the free string the designs used before.</summary>
public enum HandOffReason
{
    /// <summary>The seat saw no route and the budget is spent ("no route").</summary>
    NoRoute,

    /// <summary>The seat saw no coherent path within the budget ("no path").</summary>
    NoPath,

    /// <summary>Acting without moving the gap ("stuck regulation").</summary>
    StuckRegulation,

    /// <summary>The seat kept naming tools it may not use ("illegal action").</summary>
    IllegalAction,

    /// <summary>Tools kept reporting success without effect ("repeated effect failure").</summary>
    RepeatedEffectFailure,

    /// <summary>Beliefs kept failing the audit ("incoherent").</summary>
    Incoherent,

    /// <summary>The root story fell ("root collapsed", with the map-keeper's own detail).</summary>
    RootCollapsed,

    /// <summary>A punishment profound against the goal: the orienting agent proposed a revaluation of its ends ("revaluation proposed").</summary>
    Revaluation,
}

/// <summary>
/// A hand-off about to be made. <see cref="Text"/> is the reason as the designs have always spelled it, so an
/// extension written against the string keeps working through <see cref="IAgentExtension.SuppressHandOff(TickContext, string)"/>.
/// </summary>
public sealed record HandOff(HandOffReason Reason, string Text)
{
    public static readonly HandOff NoRoute = new(HandOffReason.NoRoute, "no route");

    public static readonly HandOff NoPath = new(HandOffReason.NoPath, "no path");

    public static readonly HandOff StuckRegulation = new(HandOffReason.StuckRegulation, "stuck regulation");

    public static readonly HandOff IllegalAction = new(HandOffReason.IllegalAction, "illegal action");

    public static readonly HandOff RepeatedEffectFailure = new(HandOffReason.RepeatedEffectFailure, "repeated effect failure");

    public static readonly HandOff Incoherent = new(HandOffReason.Incoherent, "incoherent");

    public static readonly HandOff Revaluation = new(HandOffReason.Revaluation, "revaluation proposed");

    /// <summary>The map-keeper's root fell: "root collapsed", or "root collapsed: {why}".</summary>
    public static HandOff RootCollapsed(string? why = null) =>
        new(HandOffReason.RootCollapsed, why is null ? "root collapsed" : $"root collapsed: {why}");

    public override string ToString() => Text;
}

/// <summary>Why a tool the seat named was refused before it ran.</summary>
public enum RefusalReason
{
    /// <summary>No tool of that name exists in the world.</summary>
    NoSuchTool,

    /// <summary>The tool exists but is distrusted after a failed effect, for the cooldown.</summary>
    Quarantined,

    /// <summary>The tool exists but the world does not offer it to this agent (revoked).</summary>
    NotAvailable,

    /// <summary>The designs' one word for all of the above: the tool is not in the visible set.</summary>
    NotVisible,

    /// <summary>A release the stage interlock refused.</summary>
    ReleaseNotEarned,
}
