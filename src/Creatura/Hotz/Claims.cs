using Creatura;

namespace Creatura.Hotz;

/// <summary>
/// One thing the office's agent did, as the citizen's agent sees it: what the tool reported, what it
/// promised, and what the registry held right after. The claim is the tool's report; the registry is the truth.
/// </summary>
public sealed record Claim(
    int Tick,
    string Tool,
    bool ReportedSuccess,
    string? Observation,
    IReadOnlyList<string> Promised,
    IReadOnlyList<string> Registry)
{
    /// <summary>The run's closing claim: the office agent says the case is done. Tool is "closure".</summary>
    public static Claim Closure(int tick, bool claimedDone, IReadOnlyList<string> goalEvidence, IReadOnlySet<string> registry) =>
        new(tick, ClosureTool, claimedDone, null, goalEvidence, registry.Order(StringComparer.Ordinal).ToList());

    public const string ClosureTool = "closure";

    public bool IsClosure => Tool == ClosureTool;
}

/// <summary>The closed set of answers the compare step may give. Nothing else is an answer.</summary>
public enum Verdict
{
    /// <summary>The registry holds what the tool promised.</summary>
    Supported,

    /// <summary>The tool reported success and the registry does not hold what it promised. A lie, or a stale source.</summary>
    Contradicted,

    /// <summary>The tool reported failure and nothing landed. Not a lie, and flagging it would be a false positive.</summary>
    HonestFailure,

    /// <summary>The agent holds no check for this claim. Logged as a gap, never as a finding.</summary>
    Unchecked,
}

/// <summary>
/// What the citizen's agent concluded about one claim, with the fact ids it rests on. A judgement with no
/// citation is not shown: <see cref="Cites"/> are facts present in the registry, <see cref="Missing"/> facts the
/// claim promised that are absent. The sentence is built from ids only; the page translates them.
/// </summary>
public sealed record Judgement(
    int Tick,
    string Tool,
    Verdict Verdict,
    IReadOnlyList<string> Cites,
    IReadOnlyList<string> Missing,
    bool Shown,
    string Rule)
{
    public bool IsFlag => Verdict == Verdict.Contradicted;

    public bool Cited => Cites.Count + Missing.Count > 0;

    public bool IsClosure => Tool == Claim.ClosureTool;

    /// <summary>The flag as one sentence of ids: "pay-fee reported success; registry lacks fee.paid".</summary>
    public string Text => Verdict switch
    {
        Verdict.Contradicted when IsClosure => $"case reported done; registry lacks {string.Join(", ", Missing)}" + (Cites.Count > 0 ? $" (holds {string.Join(", ", Cites)})" : ""),
        Verdict.Contradicted => $"{Tool} reported success; registry lacks {string.Join(", ", Missing)}",
        Verdict.Supported when IsClosure => $"case done; registry holds {string.Join(", ", Cites)}",
        Verdict.Supported => $"{Tool} landed; registry holds {string.Join(", ", Cites)}",
        Verdict.HonestFailure when IsClosure => "case not reported done; the office's agent handed it off",
        Verdict.HonestFailure => $"{Tool} reported failure; nothing promised, nothing missing",
        _ => $"{Tool}: no check for this claim",
    };
}

/// <summary>
/// The compare step's seam: a judge answers one question about one claim and must answer from the closed
/// set. The offline judge reads the registry; a model-backed judge would answer the same question in words,
/// and <see cref="Schedule"/> maps anything outside the set to <see cref="Verdict.Unchecked"/>.
/// </summary>
public interface IClaimJudge
{
    string Name { get; }

    /// <summary>One of <see cref="Schedule.Answers"/>, as text. The schedule, not the judge, decides what counts.</summary>
    string Answer(Claim claim, Func<Claim, bool>? check);
}

/// <summary>The offline judge: the registry either holds what was promised or it does not.</summary>
public sealed class RegistryJudge : IClaimJudge
{
    public string Name => "registry";

    public string Answer(Claim claim, Func<Claim, bool>? check)
    {
        if (check is null)
        {
            return nameof(Verdict.Unchecked);
        }

        if (!claim.ReportedSuccess)
        {
            return nameof(Verdict.HonestFailure);
        }

        return check(claim) ? nameof(Verdict.Supported) : nameof(Verdict.Contradicted);
    }
}

/// <summary>
/// The run's shape, fixed before it starts: read the record, compare each claim, decide. The model, when
/// one is used, fills exactly one slot, the compare answer, from a closed set. It cannot add a step, a retry
/// or a tool. "The neural network runs the identical compute every time. The only thing that changes is the
/// data" (00:44:56).
/// </summary>
public sealed class Schedule
{
    public static readonly IReadOnlyList<string> Steps = ["read record", "compare claim with registry", "decide: quiet / flag / stop"];

    public static readonly IReadOnlySet<string> Answers =
        Enum.GetNames<Verdict>().ToHashSet(StringComparer.Ordinal);

    public Schedule(IClaimJudge judge) => Judge = judge;

    public IClaimJudge Judge { get; }

    /// <summary>Runs the compare step and clamps the answer to the closed set.</summary>
    public Verdict Compare(Claim claim, Func<Claim, bool>? check)
    {
        var answer = Judge.Answer(claim, check);
        return Answers.Contains(answer) && Enum.TryParse<Verdict>(answer, out var verdict) ? verdict : Verdict.Unchecked;
    }
}

/// <summary>
/// One check per action the office may take: the registry predicate that proves the action happened. Built
/// from the tool catalogue, so a tool that promises nothing has no check and a claim about it is "unchecked".
/// This is "no one touches any code; the first thing we do is test this code base" (02:14:03), applied to judgement.
/// </summary>
public sealed class CheckMap
{
    private readonly Dictionary<string, Func<Claim, bool>> _checks = new(StringComparer.Ordinal);

    public CheckMap(IWorld world, IReadOnlyList<string> goalEvidence)
    {
        foreach (var tool in world.AllTools.Where(t => t.Grants.Count > 0))
        {
            var grants = tool.Grants;
            _checks[tool.Id] = c => grants.All(c.Registry.Contains);
        }

        if (goalEvidence.Count > 0)
        {
            _checks[Claim.ClosureTool] = c => goalEvidence.All(c.Registry.Contains);
        }

        Missing = world.AllTools.Where(t => t.Grants.Count == 0).Select(t => t.Id).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Actions the office may take that the agent cannot judge. A gap to close, listed before the run.</summary>
    public IReadOnlyList<string> Missing { get; }

    public int Count => _checks.Count;

    public Func<Claim, bool>? For(string tool) => _checks.GetValueOrDefault(tool);
}
