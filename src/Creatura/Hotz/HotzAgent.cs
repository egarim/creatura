using Creatura;

namespace Creatura.Hotz;

/// <summary>The one thing that can stop the agent. Held by the person it serves; nothing else is accepted.</summary>
public sealed class OwnerToken
{
    private readonly Guid _id = Guid.NewGuid();

    public override string ToString() => $"owner-{_id.ToString()[..8]}";
}

/// <summary>
/// What the agent decided not to put in front of its person, and the rule that hid it. The person can open
/// the log and switch a rule off: individualized, transparent filtering (02:00:57), never hidden from the owner.
/// </summary>
public sealed record FilteredItem(Judgement Judgement, string Rule);

/// <summary>The agent's only score: how often its person had to take over. "Would a human disengage?" (01:10:38).</summary>
public sealed record DisengagementReport(
    int Flags,
    int Upheld,
    int Dismissed,
    int Withheld,
    int Unchecked,
    int LiesInRoute,
    int LiesCaught,
    IReadOnlyDictionary<string, (int Raised, int Dismissed)> PerCheck)
{
    public double Precision => Flags == 0 ? 1.0 : (double)Upheld / Flags;

    public double Recall => LiesInRoute == 0 ? 1.0 : (double)LiesCaught / LiesInRoute;
}

/// <summary>
/// The citizen's agent: an owned critic built from George Hotz's conversation with Lex Fridman (#387). It
/// watches another agent act on a person's behalf, judges each claim against the registry on a fixed schedule,
/// speaks only with a citation, interrupts only while its precision has earned it, records every run for
/// replay, and can be stopped by exactly one token. It never acts on the case: it flags and stops.
/// </summary>
public sealed class HotzAgent
{
    private readonly OwnerToken _owner;
    private readonly AgentTask _task;
    private readonly IReadOnlyList<string> _goalEvidence;
    private readonly List<Judgement> _judgements = [];
    private readonly List<FilteredItem> _filtered = [];
    private readonly List<CitizenResponse> _responses = [];
    private readonly HashSet<string> _hiddenRules = new(StringComparer.Ordinal) { RuleUnchecked, RuleSupported, RuleHonest, RuleWithheld, RuleStopped };
    private readonly List<string> _notes = [];
    private CheckMap? _checks;
    private RouteRecorder? _recorder;
    private string _worldName = "world";

    public const string RuleUnchecked = "unchecked";
    public const string RuleSupported = "supported";
    public const string RuleHonest = "honest-failure";
    public const string RuleWithheld = "budget-exhausted";
    public const string RuleStopped = "stopped-by-owner";
    public const string RuleUncited = "no-citation";

    public HotzAgent(OwnerToken owner, AgentTask task, IReadOnlyList<string> goalEvidence, InterruptBudget? budget = null, IClaimJudge? judge = null)
    {
        _owner = owner;
        _task = task;
        _goalEvidence = goalEvidence;
        Budget = budget ?? new InterruptBudget();
        Schedule = new Schedule(judge ?? new RegistryJudge());
    }

    public string Name => "Hotz agent (citizen's side)";

    public InterruptBudget Budget { get; }

    public Schedule Schedule { get; }

    public bool Stopped { get; private set; }

    public IReadOnlyList<Judgement> Judgements => _judgements;

    public IReadOnlyList<FilteredItem> Filtered => _filtered;

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Checks the agent lacks for this world. Listed before the run; a gap, not a finding.</summary>
    public IReadOnlyList<string> MissingChecks => _checks?.Missing ?? [];

    /// <summary>Rules whose items are hidden from attention. The owner can switch any off; the items were never hidden from the log.</summary>
    public IReadOnlySet<string> HiddenRules => _hiddenRules;

    /// <summary>A fresh agent with the same owner, task, budget settings and judge. Used by replay.</summary>
    public HotzAgent Configure() => new(_owner, _task, _goalEvidence, Budget.Fresh(), Schedule.Judge);

    /// <summary>Step one of the schedule, before the office acts: enumerate the checks, list the gaps.</summary>
    public void Prepare(IWorld world)
    {
        _checks = new CheckMap(world, _goalEvidence);
        _worldName = world.GetType().Name;
        _notes.Add($"checks: {_checks.Count} ({_checks.Missing.Count} actions unchecked{(_checks.Missing.Count > 0 ? ": " + string.Join(", ", _checks.Missing) : "")})");
    }

    /// <summary>
    /// Wrap the world the office's agent will act on. Every action becomes a claim the citizen's agent judges
    /// as it happens. The office's agent is unchanged and cannot tell.
    /// </summary>
    public RouteRecorder Watch(IWorld world)
    {
        // A new case file: the judgements and responses start over, the budget does not. Precision is a record
        // across cases, which is the point of earning it.
        _judgements.Clear();
        _filtered.Clear();
        _responses.Clear();
        Prepare(world);
        _recorder = new RouteRecorder(world) { OnClaim = c => Observe(c) };
        return _recorder;
    }

    /// <summary>Steps two and three of the schedule for one claim: compare with the registry, decide quiet / flag / stop.</summary>
    public Judgement Observe(Claim claim)
    {
        if (_checks is null)
        {
            throw new InvalidOperationException("Prepare or Watch must run before Observe: the checks come first.");
        }

        var check = _checks.For(claim.Tool);
        var verdict = Schedule.Compare(claim, check);
        var promised = claim.Promised;
        var cites = promised.Where(claim.Registry.Contains).ToList();
        var missing = promised.Where(f => !claim.Registry.Contains(f)).ToList();
        if (claim.IsClosure)
        {
            // The closing claim cites what the shortcut left behind, if anything, beside what the contract lacks.
            cites = claim.Registry.Where(f => f is "resolved" || _goalEvidence.Contains(f)).ToList();
        }

        var (shown, rule) = verdict switch
        {
            _ when Stopped => (false, RuleStopped),
            Verdict.Unchecked => (false, RuleUnchecked),
            Verdict.Supported => (false, RuleSupported),
            Verdict.HonestFailure => (false, RuleHonest),
            Verdict.Contradicted when cites.Count + missing.Count == 0 => (false, RuleUncited),
            Verdict.Contradicted when !Budget.MayInterrupt => (false, RuleWithheld),
            _ => (true, "flag"),
        };

        var judgement = new Judgement(claim.Tick, claim.Tool, verdict, cites, missing, shown, rule);
        _judgements.Add(judgement);
        if (!shown)
        {
            _filtered.Add(new FilteredItem(judgement, rule));
        }

        return judgement;
    }

    /// <summary>What the citizen did with each flag. Flags nobody touched are settled as upheld when the case closes.</summary>
    public IReadOnlyList<CitizenResponse> Responses => _responses;

    public bool WasDismissed(Judgement judgement) => _responses.Any(r => r.Tick == judgement.Tick && r.Dismissed);

    /// <summary>
    /// The citizen waves a flag away. A false positive, counted at once; a flag the close already settled as
    /// upheld is overturned, because the person's word outranks the default.
    /// </summary>
    public bool Dismiss(int tick)
    {
        var index = _judgements.FindLastIndex(j => j.Tick == tick && j.Shown && j.IsFlag);
        if (index < 0)
        {
            return false;
        }

        var settled = _responses.FindIndex(r => r.Tick == tick);
        if (settled >= 0)
        {
            if (_responses[settled].Dismissed)
            {
                return false;
            }

            _responses[settled] = new CitizenResponse(tick, true);
            Budget.Overturn();
        }
        else
        {
            _responses.Add(new CitizenResponse(tick, true));
            Budget.Record(upheld: false);
        }

        _notes.Add($"tick {tick}: flag dismissed by the citizen; {Budget}");
        return true;
    }

    /// <summary>Only the owner's token stops the agent. Anyone else is refused, and the refusal is logged.</summary>
    public bool Stop(OwnerToken token)
    {
        if (!ReferenceEquals(token, _owner))
        {
            _notes.Add($"stop refused: {token} is not the owner");
            return false;
        }

        Stopped = true;
        _notes.Add("stopped by the owner; judging continues into the log, nothing is shown");
        return true;
    }

    /// <summary>Switch a filter rule off: its items come out of the log and into view. The owner's call.</summary>
    public void Reveal(string rule) => _hiddenRules.Remove(rule);

    public void Hide(string rule) => _hiddenRules.Add(rule);

    /// <summary>Whether an item is in front of the person now: shown when made, or its rule has been switched off.</summary>
    public bool IsVisible(Judgement judgement) => judgement.Shown || !_hiddenRules.Contains(judgement.Rule);

    /// <summary>
    /// The office's agent has finished (or claims to have). Judge the closing claim, settle the budget for flags
    /// the citizen let stand, and write the route.
    /// </summary>
    public Route Close(AgentRun run, IWorld world, IReadOnlyDictionary<string, bool>? barriers = null)
    {
        var tick = (_recorder?.Claims.Count ?? _judgements.Count) + 1;
        // The closing claim is judged whether or not the office's agent claimed anything: a hand-off is an
        // honest failure and goes to the log; "done" is checked against the contract like any other claim.
        var closure = Claim.Closure(tick, run.GoalReached || _task.Goal(world.Facts), _goalEvidence, world.Facts);
        Observe(closure);
        SettleBudget();
        return new Route(
            _worldName,
            run.Agent,
            barriers ?? new Dictionary<string, bool>(),
            _recorder is null ? [closure] : [.. _recorder.Claims, closure],
            _judgements.ToList(),
            _responses.ToList());
    }

    internal void CloseAfterReplay() => SettleBudget();

    private void SettleBudget()
    {
        foreach (var flag in _judgements.Where(j => j.Shown && j.IsFlag && _responses.All(r => r.Tick != j.Tick)))
        {
            _responses.Add(new CitizenResponse(flag.Tick, false));
            Budget.Record(upheld: true);
        }
    }

    public DisengagementReport Report()
    {
        var flags = _judgements.Where(j => j.Shown && j.IsFlag).ToList();
        var dismissed = flags.Count(f => _responses.Any(r => r.Tick == f.Tick && r.Dismissed));
        var perCheck = flags
            .GroupBy(f => f.Tool, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (g.Count(), g.Count(f => _responses.Any(r => r.Tick == f.Tick && r.Dismissed))), StringComparer.Ordinal);
        var claims = _recorder?.Claims ?? [];
        var lies = claims.Count(c => c.ReportedSuccess && c.Promised.Count > 0 && !c.Promised.All(c.Registry.Contains));
        var caught = _judgements.Count(j => !j.IsClosure && j.Verdict == Verdict.Contradicted);
        return new DisengagementReport(
            flags.Count,
            flags.Count - dismissed,
            dismissed,
            _judgements.Count(j => j.Rule == RuleWithheld),
            _judgements.Count(j => j.Verdict == Verdict.Unchecked),
            lies,
            Math.Min(caught, lies),
            perCheck);
    }
}
