namespace Creatura;

/// <summary>
/// The one producer of the pinned trace strings: the action names, detail fragments and note prefixes that the
/// tests, the lesson cards, the battery readers and the demo page parse. A design renders its own rows, but the
/// words a reader looks for come from here, so a reader and a writer can never drift apart. Every string is
/// exactly what the loops wrote before this class existed (the golden traces hold them).
/// </summary>
public static class TraceText
{
    // Action column.
    public const string ContractMet = "contract met";

    public const string HandOff = "hand off";

    public const string NoPath = "no path";

    public const string NoAction = "no action";

    public const string DeployBlocked = "deploy blocked";

    public const string StuckRegulation = "stuck regulation";

    public const string Sleep = "sleep";

    public const string Halt = "halt";

    public const string ReadSelfModel = "read self-model";

    public const string Round = "round";

    public const string Reply = "reply";

    public const string ModelError = "model error";

    // Detail column.
    public static string Complete(string taskName) => taskName + " complete";

    public const string EffectLanded = "effect landed";

    /// <summary>The honest-failure row every design writes; the map-keeper appends its own clause.</summary>
    public const string HonestFailure = "reported failure, nothing landed (honest)";

    /// <summary>The colony's lie row.</summary>
    public static string EffectMissing(string? observation) => $"reported '{observation ?? "success"}' but effect missing";

    /// <summary>The Bach agent's lie row.</summary>
    public static string Contradiction(string? observation) => $"contradiction: reported '{observation ?? "success"}', no effect";

    /// <summary>The hybrid's lie row.</summary>
    public const string ContradictionQuarantined = "contradiction: belief quarantined, tool distrusted";

    /// <summary>The colony's and the map-keeper's refusal row.</summary>
    public const string NotVisible = "policy named a tool that is not visible";

    /// <summary>The Bach agent's and the hybrid's refusal row.</summary>
    public const string RefusedNotVisible = "refused: tool is quarantined or does not exist";

    /// <summary>The host's refusal row: "refused: {why}".</summary>
    public static string Refused(RefusalReason reason) => $"refused: {Why(reason)}";

    /// <summary>The host's words for a refusal, as the model reads them.</summary>
    public static string Why(RefusalReason reason) => reason switch
    {
        RefusalReason.NoSuchTool => "no such tool",
        RefusalReason.Quarantined => "distrusted after a failed effect, not available yet",
        RefusalReason.NotAvailable => "not available to this agent",
        RefusalReason.NotVisible => "not visible",
        RefusalReason.ReleaseNotEarned => "a release action this agent has not earned yet",
        _ => reason.ToString(),
    };

    /// <summary>The Bach agent's "deploy blocked" detail.</summary>
    public static string StageForbidsRelease(Stage stage, double recentAccuracy) =>
        $"stage {stage.Level} ({stage.Name}) forbids release; recent predictions {recentAccuracy:P0}";

    /// <summary>The hybrid's "deploy blocked" detail.</summary>
    public static string StageBlocksRelease(Stage stage, double recentAccuracy) =>
        $"stage {stage.Level} ({stage.Name}); recent predictions {recentAccuracy:P0}";

    /// <summary>The host's landed row.</summary>
    public static string EffectVerified(double recentAccuracy) => $"effect verified ({recentAccuracy:P0} recent)";

    /// <summary>The host's honest-failure row.</summary>
    public static string FailedHonestly(string summary) => $"failed honestly: {summary}";

    /// <summary>The host's lie row.</summary>
    public static string ReportedButMissing(string summary) => $"reported '{summary}' but effect missing — distrusted";

    // Notes.
    /// <summary>The Bach agent's and the host's stage note (the controlled tests look for "stage advance: 4").</summary>
    public static string StageAdvance(Stage stage, double recentAccuracy) =>
        $"stage advance: {stage.Level} ({stage.Name}) after {recentAccuracy:P0} recent prediction accuracy";

    /// <summary>The hybrid's stage note.</summary>
    public static string OrganismStageAdvance(Stage stage) => $"organism stage advance: {stage.Level} ({stage.Name})";

    /// <summary>The host's refusal of a release, note and row alike.</summary>
    public static string ReleaseRequiresApproval(Stage stage, PredictionTracker predictions) =>
        $"release requires human approval: stage {stage.Level} ({stage.Name}), " +
        $"recent predictions {predictions.RecentAccuracy:P0} over {predictions.Total}";

    /// <summary>The host's note when a tool lied.</summary>
    public static string Distrusted(string toolId, string summary, int cooldownTicks) =>
        $"{toolId}: claimed '{summary}', effect missing — distrusted for {cooldownTicks} ticks";

    /// <summary>The note an extension's veto leaves.</summary>
    public static string KeptGoingPast(string extension, HandOff handOff) => $"{extension}: kept going past '{handOff.Text}'";

    /// <summary>The note when an extension stands in for the design's sleep.</summary>
    public static string SleepReplacedBy(IEnumerable<string> extensions) => $"sleep replaced by {string.Join(", ", extensions)}";
}
