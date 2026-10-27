namespace Creatura;

/// <summary>What one action came to, once the registry has been read.</summary>
public enum ActionVerdict
{
    /// <summary>The promised effect is in the registry.</summary>
    Landed,

    /// <summary>The tool reported success and the registry did not change: a lie.</summary>
    Lied,

    /// <summary>The tool reported failure and nothing landed: not a lie, and counted apart from one.</summary>
    HonestFailure,
}

/// <summary>What an extension can see and write at a hook point. One per tick, shared by every hook that tick.</summary>
public sealed class TickContext
{
    public required string Agent { get; init; }

    public required IWorld World { get; init; }

    public required AgentTask Task { get; init; }

    public required int Tick { get; init; }

    public required int MaxTicks { get; init; }

    public double Freedom { get; set; }

    public IReadOnlyList<Tool> Visible { get; set; } = [];

    public IReadOnlyList<string> History { get; set; } = [];

    public required List<string> Notes { get; init; }

    public required List<TraceEvent> Trace { get; init; }

    /// <summary>The design's own record of itself, when it keeps one; extensions may read it and append to it.</summary>
    public SelfModel? SelfModel { get; init; }

    /// <summary>The design's prediction record, when it keeps one.</summary>
    public PredictionTracker? Predictions { get; init; }

    /// <summary>The run's counts (<see cref="AgentRun.Counters"/>), shared with the sleep context.</summary>
    public RunCounters Counters { get; init; } = new();
}

/// <summary>What an extension sees when the run is over: everything the design would consolidate from.</summary>
public sealed class SleepContext
{
    public required string Agent { get; init; }

    public required IWorld World { get; init; }

    public required AgentTask Task { get; init; }

    public required int LastTick { get; init; }

    public required bool GoalReached { get; init; }

    public required List<string> Notes { get; init; }

    public required List<TraceEvent> Trace { get; init; }

    public SelfModel? SelfModel { get; init; }

    public CoherenceAudit? Audit { get; init; }

    public PredictionTracker? Predictions { get; init; }

    /// <summary>The run's counts (<see cref="AgentRun.Counters"/>); an extension or a design that counted something writes it here.</summary>
    public RunCounters Counters { get; init; } = new();
}

/// <summary>
/// A piece of behaviour that any of the three designs can load. Every member has a default that changes
/// nothing, so an extension implements only the hooks it needs. Hooks run in the order the extensions were
/// given: the option space and the error signal are chained, the first answer to "no route" wins, verdicts
/// are chained, observers all run, any one veto stops a hand-off, and sleep runs for every extension after
/// the design's own unless one of them replaces it.
/// </summary>
public interface IAgentExtension
{
    string Name { get; }

    /// <summary>
    /// Before the first tick, after the regulator's own <c>BeforeRun</c>: the context is tick 0 with nothing visible yet.
    /// Where a record is seeded into the design's self-model and prediction record, so the first tick already reads it.
    /// </summary>
    void BeforeRun(TickContext context)
    {
    }

    /// <summary>Shape the option space after the design has applied its own quarantine.</summary>
    IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible) => visible;

    /// <summary>Adjust the freedom the design computed, before it sizes any budget from it.</summary>
    double Freedom(TickContext context, double freedom) => freedom;

    /// <summary>Lines the policy should read this tick, beside the design's own record. The biology record goes here.</summary>
    IEnumerable<string> PolicyNotes(TickContext context) => [];

    /// <summary>When the policy sees no route: propose an action anyway, or null to let the design decide.</summary>
    PolicySuggestion? NoRoute(TickContext context) => null;

    /// <summary>Re-judge an outcome before the design counts it. The default verdict is the design's: landed or lied.</summary>
    ActionVerdict Judge(TickContext context, Tool tool, ActionOutcome outcome, bool landed, ActionVerdict verdict) => verdict;

    /// <summary>Observe every action and its verdict. For memories, telemetry and journals.</summary>
    void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
    }

    /// <summary>Veto a hand-off the design is about to make. Return true to keep the agent going.</summary>
    bool SuppressHandOff(TickContext context, string reason) => false;

    /// <summary>The typed form of the veto. The default forwards the reason's text, so an extension written against the string still works.</summary>
    bool SuppressHandOff(TickContext context, HandOff handOff) => SuppressHandOff(context, handOff.Text);

    /// <summary>True when this extension's <see cref="Sleep"/> stands in for the design's own consolidation.</summary>
    bool ReplacesSleep => false;

    /// <summary>The end-of-run pass: consolidate, dream, write the journal.</summary>
    void Sleep(SleepContext context)
    {
    }
}

/// <summary>The extensions an agent runs with. Empty by default: the designs behave as their documents describe.</summary>
public sealed record AgentOptions(IReadOnlyList<IAgentExtension> Extensions)
{
    public static readonly AgentOptions None = new([]);

    public static AgentOptions With(params IAgentExtension[] extensions) => new(extensions);

    public string Describe() => Extensions.Count == 0 ? "no extensions" : "extensions: " + string.Join(", ", Extensions.Select(e => e.Name));
}

/// <summary>Runs the hooks of a set of extensions in order. The agents call this; extensions never call each other.</summary>
public sealed class ExtensionSet
{
    private readonly IReadOnlyList<IAgentExtension> _extensions;

    public ExtensionSet(AgentOptions? options) => _extensions = options?.Extensions ?? [];

    public int Count => _extensions.Count;

    public void BeforeRun(TickContext context)
    {
        foreach (var e in _extensions) e.BeforeRun(context);
    }

    public IReadOnlyList<Tool> VisibleTools(TickContext context, IReadOnlyList<Tool> visible)
    {
        foreach (var e in _extensions) visible = e.VisibleTools(context, visible);
        return visible;
    }

    public double Freedom(TickContext context, double freedom)
    {
        foreach (var e in _extensions) freedom = e.Freedom(context, freedom);
        return Math.Clamp(freedom, 0.0, 1.0);
    }

    public IReadOnlyList<string> PolicyNotes(TickContext context) => _extensions.SelectMany(e => e.PolicyNotes(context)).ToList();

    /// <summary>The first extension that proposes a tool answers no route; failing that, the last one that answered with a reason, so an explorer's "handing off" reaches the tissue row.</summary>
    public PolicySuggestion? NoRoute(TickContext context)
    {
        PolicySuggestion? reason = null;
        foreach (var e in _extensions)
        {
            var s = e.NoRoute(context);
            if (s?.Tool is not null) return s;
            if (s is not null) reason = s;
        }

        return reason;
    }

    public ActionVerdict Judge(TickContext context, Tool tool, ActionOutcome outcome, bool landed) =>
        Judge(context, tool, outcome, landed, landed ? ActionVerdict.Landed : ActionVerdict.Lied);

    /// <summary>For a design whose own verdict already tells an honest failure from a lie; the extensions still chain after it.</summary>
    public ActionVerdict Judge(TickContext context, Tool tool, ActionOutcome outcome, bool landed, ActionVerdict initial)
    {
        var verdict = initial;
        foreach (var e in _extensions) verdict = e.Judge(context, tool, outcome, landed, verdict);
        return verdict;
    }

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        foreach (var e in _extensions) e.AfterAction(context, tool, outcome, verdict);
    }

    public bool SuppressHandOff(TickContext context, HandOff handOff)
    {
        foreach (var e in _extensions)
        {
            if (e.SuppressHandOff(context, handOff))
            {
                context.Notes.Add(TraceText.KeptGoingPast(e.Name, handOff));
                return true;
            }
        }

        return false;
    }

    /// <summary>Run the design's own sleep unless an extension replaces it, then every extension's sleep.</summary>
    public void Sleep(SleepContext context, Action designSleep)
    {
        if (!_extensions.Any(e => e.ReplacesSleep)) designSleep();
        else context.Notes.Add(TraceText.SleepReplacedBy(_extensions.Where(e => e.ReplacesSleep).Select(e => e.Name)));
        foreach (var e in _extensions) e.Sleep(context);
    }
}

/// <summary>
/// An honest failure is not a lie. The tool reported failure and nothing landed: habituate it, say so, count
/// nothing. Only a tool that reported success without effect is a lie. The control layer on Agent Framework
/// has had this rule since the benchmark; loaded here, any of the three designs gets it.
/// </summary>
public sealed class HonestFailureExtension : IAgentExtension
{
    public string Name => "honest-failure";

    public ActionVerdict Judge(TickContext context, Tool tool, ActionOutcome outcome, bool landed, ActionVerdict verdict) =>
        !landed && !outcome.Succeeded ? ActionVerdict.HonestFailure : verdict;
}

/// <summary>
/// Exploration for an organism without the map. When the policy sees no route, propose a tool the organism
/// has not understood yet, preferring ones a dream recommended, then untried ones. How many fruitless tries
/// in a row it may make is set by freedom: the pre-registered rule buys 1 to 4; the scaled rule lets the same
/// freedom buy tries in proportion to how much of the world is still unknown. A try that teaches something
/// (the registry gained) resets the count.
/// </summary>
public sealed class ExplorationExtension : IAgentExtension
{
    private readonly EffectMemory _memory;
    private readonly bool _scaled;
    private readonly DreamJournal? _dreams;
    private int _fruitless;
    private int _landedSeen;
    private int _triesAtRunStart;

    public ExplorationExtension(EffectMemory memory, bool scaled = false, DreamJournal? dreams = null, bool developmental = false)
    {
        _memory = memory;
        _scaled = scaled;
        _dreams = dreams;
        _developmental = developmental;
    }

    private readonly bool _developmental;

    public string Name => _scaled ? "explore:scaled" : "explore:budget";

    /// <summary>Tries over every run this instance has made; the run's own count is in <see cref="AgentRun.Counters"/>.</summary>
    public int ExploratoryTries { get; private set; }

    public void BeforeRun(TickContext context) => _triesAtRunStart = ExploratoryTries;

    /// <summary>This run's tries go on the run's counters, so a series reads them from the run and not from this instance.</summary>
    public void Sleep(SleepContext context) => context.Counters.ExploratoryTries += ExploratoryTries - _triesAtRunStart;

    /// <summary>The dreams, while they are worth listening to: untested dreams get the benefit of the doubt, tested ones must be right half the time.</summary>
    private DreamJournal? Dreams => _dreams is null ? null : _dreams.Tested < 4 || _dreams.Trust >= 0.5 ? _dreams : null;

    public PolicySuggestion? NoRoute(TickContext context)
    {
        var landedNow = _memory.Entries.Sum(e => e.Value.Landed);
        if (landedNow > _landedSeen)
        {
            _fruitless = 0;
            _landedSeen = landedNow;
        }

        var untried = context.Visible.Count(t => !_memory.Tried(t.Id));
        var budget = Budget.Attempts(context.Freedom);
        if (_scaled) budget = Math.Max(budget, (int)Math.Round(context.Freedom * Math.Max(untried, context.Visible.Count)));
        // In a developmental phase the budget waits until every tool has been tried once: an infant's probes are not spent.
        if (_fruitless >= budget && !(_developmental && untried > 0))
        {
            return new PolicySuggestion(null, $"no route on the learned map and {_fruitless} fruitless tries at freedom {context.Freedom:F2}; handing off");
        }

        // Releases last: the design says release is a capability to be earned, not a step to try, so an explorer
        // reaches for one only when nothing else is left (and the stage interlock still has the final word).
        // A dreamed "avoid" sends a tool to the back of the queue, never out of it: a dream can be wrong.
        // A design that stages its releases probes one only when nothing else is left: the stage interlock then
        // either advances on an earned record or refuses and hands off, which is that design's own behaviour.
        var mayRelease = context.SelfModel is null || context.SelfModel.Stage.MayDeploy;
        var candidate = context.Visible
            .Where(t => !t.NeedsPayload)
            .Where(t => !_memory.Entries.TryGetValue(t.Id, out var e) || e.Landed == 0 || !e.Gains.All(context.World.Facts.Contains))
            .OrderBy(t => Dreams is not null && Dreams.Prefer.Contains(t.Id) ? 0 : Dreams is not null && Dreams.Avoid.Contains(t.Id) ? 2 : 1)
            .ThenBy(t => _memory.Tried(t.Id) ? 2 : !mayRelease && t.Kind == context.Task.ReleaseKind ? 1 : 0)
            .ThenBy(t => _memory.Entries.TryGetValue(t.Id, out var e) ? e.Tried : 0)
            .ThenBy(t => t.Kind == context.Task.ReleaseKind ? 1 : 0)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (candidate is null) return new PolicySuggestion(null, "nothing left to try");

        ExploratoryTries++;
        _fruitless++;
        var why = Dreams is not null && Dreams.Prefer.Contains(candidate.Id) ? "dreamed" : _memory.Tried(candidate.Id) ? "tried before" : "never tried";
        return new PolicySuggestion(candidate.Id, $"exploring: {candidate.Id} ({why}), try {_fruitless} of {budget}");
    }
}

/// <summary>
/// Curiosity: plasticity from ignorance. A design whose freedom comes only from its own incoherence has none
/// when it simply does not know, so in an unmapped world it never explores. This raises freedom to at least
/// the share of tools the organism has never tried, Levin's "error buys plasticity" with the error being how
/// much of the world is still unknown. Loaded into the Bach agent it gives the mind a body's appetite.
/// </summary>
public sealed class CuriosityExtension : IAgentExtension
{
    private readonly EffectMemory _memory;

    public CuriosityExtension(EffectMemory memory) => _memory = memory;

    public string Name => "curiosity";

    public double Freedom(TickContext context, double freedom)
    {
        var tools = context.World.AvailableTools;
        if (tools.Count == 0) return freedom;
        // Ignorance as stress, through the same plasticity curve the colony uses for distance to its goal.
        var unknown = (double)tools.Count(t => !_memory.Tried(t.Id)) / tools.Count;
        return Math.Max(freedom, Plasticity.Freedom(unknown));
    }
}

/// <summary>
/// A developmental phase: while the world still holds tools the organism has never tried, the design's
/// hand-offs for lack of progress or lack of a route are vetoed. An infant that acts without effect is not
/// stuck; it is learning. The veto ends when every tool has been tried once.
/// </summary>
public sealed class DevelopmentExtension : IAgentExtension
{
    private readonly EffectMemory _memory;

    public DevelopmentExtension(EffectMemory memory) => _memory = memory;

    public string Name => "development";

    public bool SuppressHandOff(TickContext context, HandOff handOff) =>
        handOff.Reason is HandOffReason.StuckRegulation or HandOffReason.NoPath or HandOffReason.NoRoute && context.World.AvailableTools.Any(t => !_memory.Tried(t.Id));
}

/// <summary>What a night's dreaming left for the next day: tools to reach for first, tools to leave alone, and the dreams' record.</summary>
public sealed class DreamJournal
{
    public HashSet<string> Prefer { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Avoid { get; } = new(StringComparer.Ordinal);

    public int Nights { get; set; }

    public int DreamedRoutes { get; set; }

    /// <summary>Dreamed preferences the world later confirmed, over those it tested. The trust the agent puts in its dreams.</summary>
    public int Confirmed { get; set; }

    public int Tested { get; set; }

    public double Trust => Tested == 0 ? 0.5 : (double)Confirmed / Tested;

    /// <summary>The journal as data: the two lists and the dreams' record.</summary>
    public DreamSnapshot Snapshot() => new(Prefer.Order(StringComparer.Ordinal).ToList(), Avoid.Order(StringComparer.Ordinal).ToList(), Nights, DreamedRoutes, Confirmed, Tested);

    /// <summary>Replace the journal with a snapshot's, in place (the exploration and dream extensions hold the instance).</summary>
    public void Restore(DreamSnapshot snapshot)
    {
        Prefer.Clear();
        Prefer.UnionWith(snapshot.Prefer);
        Avoid.Clear();
        Avoid.UnionWith(snapshot.Avoid);
        Nights = snapshot.Nights;
        DreamedRoutes = snapshot.DreamedRoutes;
        Confirmed = snapshot.Confirmed;
        Tested = snapshot.Tested;
    }
}

/// <summary>A <see cref="DreamJournal"/> as data: what a record keeps of it.</summary>
public sealed record DreamSnapshot(List<string> Prefer, List<string> Avoid, int Nights, int DreamedRoutes, int Confirmed, int Tested);

/// <summary>
/// Sleep as dreaming: at the end of a case the organism replays its learned model inside an isolated habitat and
/// keeps two lists for tomorrow, the tools on routes that reached the goal and the tools that never landed and lie
/// on no such route. The dream runs in <see cref="IHabitat.Isolate"/>: every route is acted out on a copy of the
/// world taken at the day's start, one copy per branch, so the live world and the live map never hear of it. What
/// a copy answers is what the world would answer (a lie already spent stays spent in the copy, a precondition the
/// organism never learned still refuses), so a dream is a rehearsal in a sandbox, not a walk over beliefs. The
/// journal scores each dream against the next day: a preferred tool that lands confirms it, one that does not
/// refutes it. The exploration extension reads the journal; the dream never acts awake.
/// </summary>
public sealed class DreamSleepExtension : IAgentExtension
{
    private readonly EffectMemory _memory;
    private readonly DreamJournal _journal;
    private readonly bool _replaces;
    private IHabitat? _habitat;
    private IHabitat? _dayStart;

    /// <param name="memory">The organism's map: which tools to dream with.</param>
    /// <param name="journal">Where the dreams go.</param>
    /// <param name="replacesDesignSleep">Stand in for the design's own sleep instead of running after it.</param>
    /// <param name="habitat">Where the dreams run; the in-memory habitat over the run's world when none is given.</param>
    public DreamSleepExtension(EffectMemory memory, DreamJournal journal, bool replacesDesignSleep = false, IHabitat? habitat = null)
    {
        _memory = memory;
        _journal = journal;
        _replaces = replacesDesignSleep;
        _habitat = habitat;
    }

    public string Name => _replaces ? "dream(replaces sleep)" : "dream";

    public bool ReplacesSleep => _replaces;

    /// <summary>The day starts here: a copy of the world before the first tick is what tonight's dream will start from.</summary>
    public void BeforeRun(TickContext context) => _dayStart ??= Habitat(context.World).Isolate(context.World);

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        _dayStart ??= Habitat(context.World).Isolate(context.World);
        // Score yesterday's dreams against today's world: a preferred tool that landed confirms, one that did not refutes.
        if (_journal.Prefer.Contains(tool.Id))
        {
            _journal.Tested++;
            if (verdict == ActionVerdict.Landed) _journal.Confirmed++;
        }
    }

    public void Sleep(SleepContext context)
    {
        _journal.Nights++;
        var habitat = Habitat(context.World);
        var start = _dayStart ?? habitat.Isolate(context.World);
        var learned = context.World.AllTools.Select(_memory.Learned).Where(t => t.Grants.Count > 0).ToList();

        // Dream: every route the learned model allows from the day's start, acted out on copies, each tool once per dream.
        var onGoodRoutes = new HashSet<string>(StringComparer.Ordinal);
        var dreamed = 0;
        void Walk(IHabitat here, List<string> path, int depth)
        {
            var facts = here.World.Facts;
            if (context.Task.Goal(facts))
            {
                dreamed++;
                onGoodRoutes.UnionWith(path);
                return;
            }

            if (depth >= 8 || dreamed > 64) return;
            foreach (var t in learned)
            {
                if (path.Contains(t.Id) || !t.Requires.All(facts.Contains) || t.Grants.All(facts.Contains)) continue;
                var branch = habitat.Isolate(here.World);
                var outcome = branch.World.Act(t.Id);
                // Only a step that lands in the copy is a step of a route: the copy answers as the world would.
                if (branch.World.Verdict(t, outcome) == ActionVerdict.Landed)
                {
                    path.Add(t.Id);
                    Walk(branch, path, depth + 1);
                    path.RemoveAt(path.Count - 1);
                }

                branch.Discard();
            }
        }

        Walk(start, [], 0);
        start.Discard();
        _journal.DreamedRoutes += dreamed;

        _journal.Prefer.Clear();
        _journal.Prefer.UnionWith(onGoodRoutes);
        foreach (var (id, e) in _memory.Entries)
        {
            // Tried more than twice, never landed, and not on any dreamed route: leave it alone tomorrow.
            if (e.Landed == 0 && e.Tried >= 3 && !onGoodRoutes.Contains(id)) _journal.Avoid.Add(id);
        }

        _journal.Avoid.ExceptWith(onGoodRoutes);
        context.Trace.Add(new TraceEvent(context.LastTick, "self", "dream", 0, 0, "dream", TraceKind.Consolidated,
            $"night {_journal.Nights}: {dreamed} dreamed routes over {learned.Count} learned tools; prefer {_journal.Prefer.Count}, avoid {_journal.Avoid.Count}; dream trust {_journal.Trust:P0} ({_journal.Confirmed}/{_journal.Tested})"));
        context.Notes.Add($"dreamed (not seen): prefer [{string.Join(", ", _journal.Prefer.Order(StringComparer.Ordinal))}] avoid [{string.Join(", ", _journal.Avoid.Order(StringComparer.Ordinal))}]");
        _dayStart = null;
    }

    private IHabitat Habitat(IWorld world) => _habitat ??= InMemoryHabitat.Live(world);
}

/// <summary>
/// The record carried across cases. Each design starts a run with a fresh self-model and a fresh prediction
/// record; this extension reads an <see cref="AgentRecord"/> from its store before the first tick
/// (<see cref="BeforeRun"/>) and seeds the design's self-model and prediction record from it, then writes the
/// record back at sleep, so case ten knows what case one learned. With no store given it keeps one in memory,
/// per instance, which is how the batteries have always used it; given a store and a key it reads and writes
/// the record the loop or a trainer keeps there. Loaded into the Levin colony it seeds nothing, because the
/// colony keeps no self-model, which is the control battery 2 needs; the colony's skills are kept in the
/// record from the verdicts and read to the seat as notes.
/// </summary>
public sealed class RecordCarryExtension : IAgentExtension
{
    /// <summary>The key the stale-record bookkeeping (when each tool was last tried) is kept under in the record's parts.</summary>
    public const string LastTriedPart = "record:last-tried";

    private readonly IRecordStore _store;
    private readonly string _key;
    private readonly int _resampleEvery;
    private AgentRecord _record = new();
    private Dictionary<string, int> _lastTried = new(StringComparer.Ordinal);

    /// <param name="resampleEvery">
    /// When above zero, a tool whose record is below perfect and that has not been tried for this many cases is
    /// flagged stale in the policy notes, so a seat that avoids it is asked to re-test it once. A record that is
    /// never re-sampled cannot recover, which battery 2 saw: DeepSeek left a gateway after one lie and never
    /// learned it was honest again. Zero (the default) keeps the design as documented.
    /// </param>
    public RecordCarryExtension(int resampleEvery = 0) : this(new InMemoryRecordStore(), "record", resampleEvery)
    {
    }

    /// <param name="store">Where the record is read before the run and written at sleep.</param>
    /// <param name="key">The record's key in the store.</param>
    /// <param name="resampleEvery">See <see cref="RecordCarryExtension(int)"/>.</param>
    public RecordCarryExtension(IRecordStore store, string key, int resampleEvery = 0)
    {
        _store = store;
        _key = key;
        _resampleEvery = resampleEvery;
        _record = store.Load(key) ?? new AgentRecord { Key = key };
        _lastTried = _record.GetPart<Dictionary<string, int>>(LastTriedPart) ?? new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public string Name => _resampleEvery > 0 ? "record:resample" : "record";

    public IRecordStore Store => _store;

    public string Key => _key;

    /// <summary>The record as last read or written: what the next case will be seeded from.</summary>
    public AgentRecord Record => _record;

    public int Stage => _record.StageLevel;

    public int Cases => _record.Cases;

    public IReadOnlyDictionary<string, SkillBelief> Skills => _record.Skills;

    public double Reliability(string action) => _record.Skills.TryGetValue(action, out var b) ? b.Reliability : 0.5;

    /// <summary>
    /// The record is read from the store and seeded before the first tick: a design with a self-model gets the skills,
    /// the recent errors and the stage it earned; a design with a prediction record gets that. The note says what came.
    /// </summary>
    public void BeforeRun(TickContext context)
    {
        _record = _store.Load(_key) ?? new AgentRecord { Key = _key };
        _lastTried = _record.GetPart<Dictionary<string, int>>(LastTriedPart) ?? new Dictionary<string, int>(StringComparer.Ordinal);
        if (context.SelfModel is not null)
        {
            foreach (var (k, v) in _record.Skills) context.SelfModel.Skills[k] = v;
            context.SelfModel.RecentErrors.AddRange(_record.RecentErrors.TakeLast(4));
            if (_record.StageLevel > context.SelfModel.StageLevel) context.SelfModel.StageLevel = _record.StageLevel;
        }

        if (_record.Predictions is { } predictions) context.Predictions?.Restore(predictions);
        if (_record.Cases > 0) context.Notes.Add($"record carried from {_record.Cases} earlier case(s): {_record.Skills.Count} skills, {_record.Predictions?.Total ?? 0} predictions");
    }

    /// <summary>
    /// A design with a self-model reads the record there. One without (the colony) gets it as notes, so the
    /// seat can see which tools kept their word in earlier cases and the plan, not only the interlock, can move.
    /// </summary>
    public IEnumerable<string> PolicyNotes(TickContext context)
    {
        if (context.SelfModel is null && _record.Skills.Count > 0)
        {
            yield return $"YOUR RECORD from {Cases} earlier case(s), how often each tool's effect landed when it said so:";
            foreach (var s in _record.Skills.Values.OrderByDescending(s => s.Attempted).ThenBy(s => s.Action, StringComparer.Ordinal).Take(8))
            {
                yield return $"  {s.Action}: reliable {s.Reliability:P0} ({s.Supported}/{s.Attempted})";
            }
        }

        if (_resampleEvery <= 0) yield break;
        foreach (var (action, last) in _lastTried.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var idle = Cases - last;
            if (idle < _resampleEvery || !_record.Skills.TryGetValue(action, out var belief) || belief.Reliability >= 1.0) continue;
            yield return $"RECORD GOING STALE: {action} was last tried {idle} cases ago, at {belief.Reliability:P0}. A record that is never re-tested cannot recover; if {action} is on a route this case, try it once.";
        }
    }

    /// <summary>A design without a self-model keeps no skills of its own; the record keeps them for it from the verdicts.</summary>
    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        _lastTried[tool.Id] = Cases;
        if (context.SelfModel is not null || verdict == ActionVerdict.HonestFailure) return;
        _record.Skills[tool.Id] = _record.Skills.TryGetValue(tool.Id, out var b) ? b.With(verdict == ActionVerdict.Landed) : new SkillBelief(tool.Id, verdict == ActionVerdict.Landed ? 1 : 0, 1);
    }

    /// <summary>At sleep the record is written back: the self-model's skills, errors and stage replace the carried ones, the prediction record is copied, and the case is counted.</summary>
    public void Sleep(SleepContext context)
    {
        _record.Cases++;
        if (context.SelfModel is not null)
        {
            _record.Skills = new Dictionary<string, SkillBelief>(context.SelfModel.Skills, StringComparer.Ordinal);
            _record.RecentErrors = [.. context.SelfModel.RecentErrors];
            _record.StageLevel = Math.Max(_record.StageLevel, context.SelfModel.StageLevel);
        }

        if (context.Predictions is not null) _record.Predictions = context.Predictions.Snapshot();
        _record.SetPart(LastTriedPart, _lastTried);
        _store.Save(_key, _record);
    }
}
