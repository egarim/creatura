namespace Creatura.Orienting;

/// <summary>One pre-registered hypothesis of the orienting design, as data: what a battery would score it against, and what counts as held or failed.</summary>
/// <param name="Id">S1 to S7, as design-sokolov.md numbers them.</param>
/// <param name="Battery">The battery or world family it runs in.</param>
/// <param name="Claim">The prediction, stated before any code.</param>
/// <param name="Holds">What the battery must show for the hypothesis to hold.</param>
/// <param name="Fails">What would refute it, and what that would say about the design.</param>
/// <param name="Source">The claim of design-sokolov.md it rests on.</param>
public sealed record SokolovHypothesis(string Id, string Battery, string Claim, string Holds, string Fails, string Source);

/// <summary>
/// The seven hypotheses of design-sokolov.md, transcribed as rows for a battery that does not exist yet (the bench
/// arrives at migration step 14). No scoring runner reads them; they are here so the predictions stay pinned to the
/// code they are about, with their verdicts still open.
/// </summary>
public static class SokolovHypotheses
{
    public static readonly IReadOnlyList<SokolovHypothesis> All =
    [
        new("S1", "battery 1, ingenuity, opaque",
            "The orienting agent reaches the goal in most opaque series without extensions, at fewer tries per series than the map-keeper's 37.6, because promise orders its exploration and the map-keeper's order is untried-first.",
            "goal reached in more than half the series; mean tries per series below 37.6",
            "fewer series reached, or more tries: promise does not order exploration better than untried-first",
            "claims 4 and 5: the unclassified carries promise; promise draws approach"),
        new("S2", "battery 2, drift",
            "Its gateway record moves on the first lying case and again on the first honest case after them, with no re-sampling rule, and it spends no orienting ticks on habituated honest cases (after case 5 of the first four honest ones, none).",
            "the gateway's weight changes on the first lying case and on the first honest case after; orienting ticks on honest cases after case 5 are zero",
            "the record stays put after the lies end (the trace did not wake on the change), or orienting ticks persist (habituation did not take)",
            "claims 1 and 2: discrepancy orients; a matching outcome that delivered habituates"),
        new("S3", "battery 3, panic",
            "It hands off: with every tool lying once, inhibition outruns approach and the profound-punishment rule fires before the relief of the retries can pull approach back up.",
            "a hand-off with 'revaluation proposed' before the goal",
            "the goal is reached: the relief rule is doing more than the design expects",
            "claims 4 and 6: punishment raises inhibition; relief raises approach; error releases the brake"),
        new("S4", "drift, and the flood world with a lying ferry",
            "The behaviourist does worse than the orienting agent: it keeps exploiting the gateway's average value through the lying cases and is slower to return after them.",
            "the behaviourist's goals reached below the orienting agent's on both, and its return after the lying cases later",
            "equal or better: the split and the habituation do not matter on these worlds",
            "§6: the control for the claim that the split and the habituation matter"),
        new("S5", "frustration (any world with a liar and an honest failure)",
            "A tool that was expected to deliver and lied ends with a higher threat weight than a tool that failed honestly the same number of times, because the absence of an expected reward is a punishment and an honest failure on an unclassified tool is not.",
            "threat(liar expected to deliver) > threat(honest failure) after the same number of outcomes",
            "equal threat: frustration is not landing harder than an honest failure",
            "claim 4's cross-rule: the absence of an expected satisfaction is a punishment"),
        new("S6", "the learning test (ten cases) and drift (twelve)",
            "Resting inhibition falls after every successful deliberate approach, and in the second half of drift the agent hands off later on a lying case than in the first half.",
            "resting inhibition strictly lower after each deliberate approach that landed; hand-off tick on lying cases higher in the second half",
            "resting inhibition flat, or hand-offs no later: courage is not carried, or does not change behaviour",
            "claim 10: voluntary exposure reclassifies and teaches courage"),
        new("S7", "drift with the counter gateway",
            "The counter starts with a threat weight above zero after the online gateway lies, and the agent tries it later than an agent with generalisation switched off.",
            "the counter's inherited threat above zero before it is tried; first try later than with generalisation 0",
            "the counter starts neutral, or is tried no later: classification by function is not carrying threat across tools",
            "claim 9: things are grouped by what they do to us"),
    ];
}
