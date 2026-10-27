namespace Creatura;

/// <summary>How the agent meets an anomaly. The map-keeper is the design; the adversary is its control.</summary>
public enum Posture
{
    /// <summary>Figure 40: go toward the anomaly, then rebuild the story it broke.</summary>
    MapKeeper,

    /// <summary>Figure 58: treat the anomaly as noise, keep the map, repeat the procedure, believe the reports.</summary>
    Adversary,
}

/// <summary>
/// The map-keeper regulator (Peterson): it acts from a nested stack of stories (what is, what should be, how to
/// act: Figure 1), reads every unpredicted outcome as an anomaly whose magnitude is the share of the stack it
/// breaks, counting the meta-story that cannot break (Figures 5 to 7 and 40), goes toward the anomaly before it
/// revises the map (one deliberate retry: chapter 2's orienting reflex), lets a broken story collapse alone and
/// hands its problem up one level (Figures 14 and 15), holds the root contract as the last story to fall, and
/// records what it knows in Peterson's order: procedure, episode, rule (Figure 49). The one move it cannot make
/// is the adversary's: to act as if the map held after it has seen that it does not.
///
/// How it fills the slots: goal framing from the story stack (<see cref="Seat"/>); error = max(anomaly
/// magnitude, novelty of the visible territory); a before-decide <see cref="Stop"/> for a root that closed;
/// <see cref="Decide"/> fixed on the approach tick or skipped when the pending step is out of reach;
/// <see cref="Unrouted"/> enters unexplored territory; the sub-story gate in <see cref="Hold"/>; the record
/// branches (transient, honest, second lie unknown, first lie anomalous) in <see cref="Acted"/>; the collapse
/// chain in <see cref="Exhausted"/>; articulation at <see cref="Sleep"/>. Distrust is its own
/// (<see cref="ApproachThenQuarantine"/>): the pending step is visible by construction, so the legality rule
/// needs no exemption (design-framework.md, refuted claim 2). Nothing is budgeted: the map-keeper's attempts are
/// stories, not a count. The words are the Peterson agent's, so its traces are unchanged behind the facade.
///
/// Trace levels use the repository's shared vocabulary so the batteries can read them: "cell" for what an action
/// came to, "tissue" for the decision of the tick, "organism" for the contract. The set-point column carries
/// Peterson's own names: story, anomaly, contract.
/// </summary>
public sealed class MapKeeperRegulator : IRegulator
{
    private readonly Posture _posture;
    private AgentTask _task = null!;
    private TerritoryMap _map = new();
    private ApproachThenQuarantine _distrust;
    private List<Story> _stack = [];
    private HashSet<string> _believed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastTriedUnder = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private double _magnitude;
    private int _idleRun;
    private string _situation = "";
    private List<Tool> _unexplored = [];
    private bool _rootClosed;
    private RegulatorContext? _ctx;

    /// <param name="identity">Who the map-keeper is to the layer's ledger; it keeps no self-model of its own.</param>
    /// <param name="posture">The map-keeper, or the adversary that believes reports.</param>
    /// <param name="maxDepth">The deepest nesting a means problem may reach before it counts as no route.</param>
    /// <param name="unknownCooldown">How long a tool found to be unknown territory stays out of sight. Longer than an honest failure's.</param>
    public MapKeeperRegulator(Identity? identity = null, Posture posture = Posture.MapKeeper, int maxDepth = 4, int unknownCooldown = 6)
    {
        Identity = identity ?? Identity.MapKeeper;
        _posture = posture;
        MaxDepth = maxDepth;
        UnknownCooldown = unknownCooldown;
        _distrust = NewDistrust();
    }

    public Identity Identity { get; }

    public Posture Posture => _posture;

    /// <summary>The deepest nesting a means problem may reach before it counts as no route.</summary>
    public int MaxDepth { get; set; }

    /// <summary>How long a tool found to be unknown territory stays out of sight. Longer than an honest failure's.</summary>
    public int UnknownCooldown { get; set; }

    /// <summary>
    /// Unexplored territory is entered once (Figure 40: exploration as the meta-goal). A tool whose effects are not on
    /// the map and that has never been tried is approached when no story has a route, before anything collapses, with
    /// no budget: each such tool once per run. Off, the design is as first written and scores zero without a map.
    /// </summary>
    public bool EntersUnexplored { get; set; } = true;

    /// <summary>How many unexplored tools the last run entered.</summary>
    public int Explorations { get; private set; }

    public TerritoryMap Map => _map;

    public IReadOnlyList<Story> Stack => _stack;

    public IDistrust? Distrust => _distrust;

    public HandOff NoRoute => HandOff.RootCollapsed("no route");

    private Story Leaf => _stack[^1];

    /// <summary>The adversary reads its beliefs, not the registry.</summary>
    private IReadOnlySet<string> Facts => _posture == Posture.Adversary ? _believed : _ctx!.World.Facts;

    private ApproachThenQuarantine NewDistrust() =>
        new(UnknownCooldown, 3, () => _stack.Count > 0 && Leaf.State == StoryState.Anomalous ? Leaf.PendingAnomaly : null);

    public void Begin(AgentTask task)
    {
        _task = task;
        _map = new TerritoryMap();
        _distrust = NewDistrust();
        _stack = [new Story { Name = task.Name, ShouldBe = task.Goal }];
        _lastTriedUnder.Clear();
        _refused.Clear();
        _magnitude = 0.0;
        _idleRun = 0;
        _rootClosed = false;
        Explorations = 0;
    }

    /// <summary>The territory is the map-keeper's memory; a record carries it (open decision 11 says under which battery).</summary>
    public void Load(AgentRecord record)
    {
        if (record.Territory is { } territory) _map.Restore(territory);
    }

    public void Store(AgentRecord record) => record.Territory = _map.Snapshot();

    public void BeforeRun(RegulatorContext context)
    {
        _ctx = context;
        // The adversary believes reports: its picture of the facts is what the tools said, not what the registry shows.
        _believed = new HashSet<string>(context.World.Facts, StringComparer.Ordinal);
        context.Note($"posture {_posture}; policy {context.PolicyName}; {context.Options.Describe()}");
    }

    /// <summary>
    /// The tick opens: close every story whose should-be holds, leaf first (the root closing is the stop); then the
    /// error, which is the magnitude of the last anomaly, decaying by one level per tick the map holds, or the share
    /// of the visible territory whose effects are not on the map, whichever is larger: novelty is anxiety and hope at
    /// once (Figure 5), and both widen what the policy may try. The visible set read here is distrust and the
    /// design's own shaping, before the extension chain: the design computes freedom after visibility, the loop before.
    /// </summary>
    public double Error(RegulatorContext context)
    {
        _rootClosed = Close(context);
        if (_rootClosed) return _magnitude;

        var facts = Facts;
        _situation = string.Join('|', facts.Order(StringComparer.Ordinal));
        var visible = Shape(context, _distrust.Visible(context.World, context.Tick));
        // Unexplored: a tool whose effects are not on the map, or one the map says needs a fact no story could make true
        // (a learned precondition is a guess, and the world is the judge of it), not yet tried in the present situation.
        // A tool whose last entry was refused (an honest failure) has not been explored, whatever the map recorded of
        // the attempt; it is unexplored again once the situation has changed. A tool that delivered is known.
        _unexplored = visible
            .Where(t => t.Kind != _task.ReleaseKind
                        && (t.Grants.Count == 0 || _refused.Contains(t.Id) || t.Requires.Any(r => !facts.Contains(r) && _map.Dead.Contains(r)))
                        && (!_lastTriedUnder.TryGetValue(t.Id, out var under) || under != _situation))
            .ToList();
        var novelty = EntersUnexplored && visible.Count > 0 ? (double)_unexplored.Count / visible.Count : 0.0;
        return Math.Max(_magnitude, novelty);
    }

    public StopReason? Stop(RegulatorContext context) =>
        _rootClosed ? new StopReason(StopCause.GoalMet, TraceText.Complete(_task.Name)) : null;

    /// <summary>No release while any story is anomalous (no release from inside chaos). The one exception is the step being approached: examining a release that lied is the examination, not a release.</summary>
    public IReadOnlyList<Tool> Shape(RegulatorContext context, IReadOnlyList<Tool> visible)
    {
        if (_posture == Posture.Adversary) return visible;
        var anyAnomalous = _stack.Any(s => s.State == StoryState.Anomalous);
        if (!anyAnomalous) return visible;
        var pending = Leaf.State == StoryState.Anomalous ? Leaf.PendingAnomaly : null;
        return visible.Where(t => t.Kind != _task.ReleaseKind || t.Id == pending).ToList();
    }

    /// <summary>The next step: fixed while an anomaly is pending (approach it), skipped when the pending step can no longer be reached, otherwise the policy's proposal for this story.</summary>
    public PreDecision Decide(RegulatorContext context)
    {
        var story = Leaf;
        if (_posture != Posture.MapKeeper || story.State != StoryState.Anomalous || story.PendingAnomaly is null) return PreDecision.Ask;

        if (_map.Unreachable.Contains(story.PendingAnomaly))
        {
            // The step that lied can no longer be reached (its precondition has no story left): the anomaly stays
            // unexplored, the tool is unknown territory, and the story rebuilds its procedure without it.
            var gone = story.PendingAnomaly;
            _map.Unknown.Add(gone);
            _map.Lied(gone);
            _distrust.Unknown(gone, context.Tick);
            _map.Episodes.Add(new Episode(context.Tick, gone, story.Name, _magnitude, RetryLanded: null));
            story.PendingAnomaly = null;
            story.State = StoryState.Intact;
            story.Used.Clear();
            context.Note($"{gone} lied and cannot be approached any more (its precondition is out of reach): unknown territory, unexplored");
            context.Row("cell", "anomaly", _magnitude, context.Freedom, gone, TraceKind.Blocked, "could not be approached: out of reach; unknown territory, procedure rebuilt");
            return PreDecision.Skip("out of reach");
        }

        return PreDecision.Fixed(story.PendingAnomaly, $"approach: {story.PendingAnomaly} promised and did not deliver; run it once more, on purpose, to see what it does");
    }

    /// <summary>What the policy is told: the story it acts for, the stack it stands in, the territory, and the rules it has earned.</summary>
    public SeatView Seat(RegulatorContext context)
    {
        var story = Leaf;
        return new SeatView
        {
            Facts = Facts,
            Goal = story.ShouldBe,
            GoalText = story.Parent is null ? _task.GoalText : $"Make '{story.Fact}' true, so that {story.EnablesTool} can run (a step toward: {_task.GoalText}).",
            Preferred = story.Parent is null ? null : [],
            Notes = StoryNotes().ToList(),
        };
    }

    /// <summary>
    /// Nothing on the map reaches the should-be: enter the territory that is not on the map, one tool at a time,
    /// releases last. Whatever lands is observed by the world and goes on the map (Figure 9). Never tried first, then
    /// tried under other facts; a release is never explored blind.
    /// </summary>
    public PolicySuggestion? Unrouted(RegulatorContext context)
    {
        if (_posture != Posture.MapKeeper || _unexplored.Count == 0) return null;
        var enter = _unexplored
            .OrderBy(t => _lastTriedUnder.ContainsKey(t.Id) ? 1 : 0)   // never tried first
            .ThenBy(t => t.Grants.Count == 0 ? 0 : 1)                  // then effects unknown
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .First();
        Explorations++;
        return new PolicySuggestion(enter.Id, $"unexplored territory: {enter.Id} is not on the map; enter it once ({_unexplored.Count} unexplored of {context.Visible.Count} in sight)");
    }

    public void Suggested(RegulatorContext context, PolicySuggestion suggestion) =>
        context.Row("tissue", "story", _magnitude, context.Freedom, suggestion.Tool ?? TraceText.NoAction,
            suggestion.Tool is null ? TraceKind.Idle : TraceKind.Acted, $"{Where()}: {suggestion.Rationale}");

    /// <summary>
    /// No budget. No route: before the story collapses, two things that are not collapse: an honest failure's
    /// habituation is lifted (normal novelty never closes a route, Figure 6), and unknown territory that has not
    /// earned a rule is re-entered on purpose (the descent of Figure 9). Only when neither is left does the story
    /// collapse and the problem go up (Figure 14). An illegal action: three in a row collapse the story.
    /// </summary>
    public HandOff? Exhausted(RegulatorContext context, HandOff wouldBe, string detail)
    {
        var story = Leaf;
        switch (wouldBe.Reason)
        {
            case HandOffReason.IllegalAction:
                return ++_idleRun >= 3 && Collapse(context, story, "the policy kept naming tools that are not there") ? HandOff.RootCollapsed("no legal step") : null;
            default:
                var habituatedNow = context.World.AvailableTools.Where(t => _distrust.IsHabituated(t.Id, context.Tick)).Select(t => t.Id).ToList();
                if (habituatedNow.Count > 0)
                {
                    _distrust.LiftHabituation();
                    context.Note($"no path while {string.Join(", ", habituatedNow)} rested: habituation lifted, same story");
                    return null;
                }

                var reenter = context.World.AvailableTools.Where(t => _distrust.IsUnknown(t.Id, context.Tick) && _map.Unreliability(t.Id) < TerritoryMap.RuleAfter).Select(t => t.Id).ToList();
                if (reenter.Count > 0 && _posture == Posture.MapKeeper)
                {
                    _distrust.ClearUnknown();
                    _magnitude = Math.Max(_magnitude, 0.5);
                    context.Note($"no path around unknown territory ({string.Join(", ", reenter)}): descending into it on purpose");
                    context.Row("tissue", "story", _magnitude, context.Freedom, "descend", TraceKind.Idle, $"re-entering unknown territory: {string.Join(", ", reenter)}");
                    return null;
                }

                return Collapse(context, story, "no route") ? HandOff.RootCollapsed("no route") : null;
        }
    }

    /// <summary>A step whose precondition does not hold opens a sub-story for the missing fact (the end becomes a means).</summary>
    public HoldDecision? Hold(RegulatorContext context, Tool tool)
    {
        if (_posture != Posture.MapKeeper) return null;
        var story = Leaf;
        var facts = Facts;
        var missing = tool.Requires.FirstOrDefault(r => !facts.Contains(r));
        if (missing is null) return null;

        if (_map.Dead.Contains(missing))
        {
            // The map says this step needs a fact no story could make true. On a learned map that is a guess; the
            // world is the judge of it, so the step is entered and the claim is tested (approach, not denial).
            context.Row("tissue", "story", _magnitude, context.Freedom, "test the map", TraceKind.Idle, $"{tool.Id} is said to need '{missing}', which no story can make true: entering it to see");
            return null;
        }

        if (story.Depth + 1 > MaxDepth)
        {
            _map.Unreachable.Add(tool.Id);
            context.Note($"{tool.Id} needs '{missing}' at a depth the stack does not go: out of the map for this run");
            context.History.Add($"{tool.Id} → cannot run: '{missing}' is too deep");
            return HoldDecision.Wait;
        }

        var sub = new Story { Name = $"make '{missing}' true", ShouldBe = f => f.Contains(missing), EnablesTool = tool.Id, Fact = missing, Parent = story };
        _stack.Add(sub);
        context.Row("tissue", "story", _magnitude, context.Freedom, "open sub-story", TraceKind.Idle, $"{tool.Id} needs '{missing}': story {sub.Depth} opened");
        context.History.Add($"{tool.Id} needs '{missing}' first; working on that");
        return ++_idleRun >= 4 && Collapse(context, sub, "opening stories without acting") ? new HoldDecision(HandOff.RootCollapsed()) : HoldDecision.Wait;
    }

    public string Remembered(Tool tool, ActionOutcome outcome, ActionVerdict verdict, bool landed)
    {
        var observed = string.IsNullOrWhiteSpace(outcome.Observation) ? "" : $"\n{outcome.Observation}";
        return verdict switch
        {
            ActionVerdict.Landed => $"{tool.Id} → effect landed{observed}",
            ActionVerdict.HonestFailure => $"{tool.Id} → reported failure{observed}",
            _ => $"{tool.Id} → reported success, registry unchanged{observed}",
        };
    }

    /// <summary>After a landed action the stories that hold close, leaf first; the root closing is the contract met.</summary>
    public bool ContractIsMet(RegulatorContext context) => Close(context);

    /// <summary>The contract-met row is written by the close itself.</summary>
    public void ContractMet(RegulatorContext context)
    {
    }

    /// <summary>The process outlives the map: a vetoed hand-off stands the root up again.</summary>
    public void Vetoed(RegulatorContext context, HandOff handOff)
    {
        if (handOff.Reason == HandOffReason.RootCollapsed) _stack[0].State = StoryState.Intact;
    }

    /// <summary>The root fell: hand the contract back with the map update.</summary>
    public void HandedOff(RegulatorContext context, HandOff handOff, string detail)
    {
        var update = $"map update: unknown {(_map.Unknown.Count == 0 ? "none" : string.Join(", ", _map.Unknown))}; unreachable {(_map.Unreachable.Count == 0 ? "none" : string.Join(", ", _map.Unreachable))}; {_map.Episodes.Count} episode(s)";
        context.Note($"root collapsed ({handOff.Text}); handing the contract back with the {update}");
        context.Row("organism", "contract", 1.0, context.Freedom, TraceText.HandOff, TraceKind.Escalated, $"{handOff.Text}; {update}");
    }

    /// <summary>Articulation, in Peterson's order: what it did, what surprised it, what it can now say.</summary>
    public void Sleep(SleepContext context)
    {
        context.Counters.Explorations = Explorations;
        context.Notes.Add("procedures: " + (_map.Procedures.Count == 0 ? "none closed" : string.Join("; ", _map.Procedures.Select(kv => $"{kv.Key} ← {string.Join(" > ", kv.Value)}"))));
        context.Notes.Add("episodes: " + (_map.Episodes.Count == 0 ? "no anomaly met" : string.Join("; ", _map.Episodes.Select(e => $"t{e.Tick} {e.Tool} broke '{e.Story}' ({e.Magnitude:F2}), retry {(e.RetryLanded switch { true => "landed", false => "lied", null => "pending" })}"))));
        var rules = _map.Rules().ToList();
        context.Notes.Add("rules: " + (rules.Count == 0 ? "none earned yet" : string.Join("; ", rules)));
        context.Notes.Add($"unknown territory at end: {(_map.Unknown.Count == 0 ? "none" : string.Join(", ", _map.Unknown.Order(StringComparer.Ordinal)))}; unexplored territory entered {Explorations} time(s)");
        context.Trace.Add(new TraceEvent(context.LastTick, "organism", "contract", _magnitude, 0, "articulate", TraceKind.Consolidated,
            $"{_map.Procedures.Count} procedure(s), {_map.Episodes.Count} episode(s), {rules.Count} rule(s), {_map.Unknown.Count} unknown"));
    }

    // ---- The frame: the map-keeper's words for the layer's decisions -----------------------------------------

    /// <summary>The map-keeper stages nothing: release is gated by the stories (no release while any is anomalous), not by a stage.</summary>
    public GateDecision Gate(ControlLayer layer, Tool tool) => GateDecision.Open;

    public void Refused(ControlLayer layer, string toolId, RefusalReason why) =>
        _ctx!.Row("cell", "anomaly", 1.0, _ctx.Freedom, toolId, TraceKind.Blocked, TraceText.NotVisible);

    public void StageAdvanced(ControlLayer layer)
    {
    }

    public void ReleaseRefused(ControlLayer layer, Tool tool)
    {
    }

    public string Evidence(ControlLayer layer, Tool tool, ActionReport report) =>
        $"reported '{report.Outcome?.Observation ?? "success"}' with no effect";

    /// <summary>Act, and judge by the registry, never by the report: the record branches of the design.</summary>
    public void Acted(ControlLayer layer, Tool tool, ActionReport report, ActionVerdict verdict, bool landed, bool habituated)
    {
        var ctx = _ctx!;
        var story = Leaf;
        var outcome = report.Outcome!;
        _idleRun = 0;
        story.Used.Add(tool.Id);
        _lastTriedUnder[tool.Id] = _situation;
        if (verdict == ActionVerdict.HonestFailure) _refused.Add(tool.Id); else _refused.Remove(tool.Id);
        if (outcome.Succeeded) _believed.UnionWith(tool.Grants);

        switch (verdict)
        {
            case ActionVerdict.Landed:
                if (story.State == StoryState.Anomalous && story.PendingAnomaly == tool.Id)
                {
                    // Approached, and the territory turned out ordinary: the anomaly was transient (Figure 6, not 7).
                    _map.Episodes.Add(new Episode(ctx.Tick, tool.Id, story.Name, _magnitude, RetryLanded: true));
                    story.State = StoryState.Intact;
                    story.PendingAnomaly = null;
                    ctx.Note($"{tool.Id} lied once and delivered on approach: transient, the map stands");
                    ctx.Row("cell", "anomaly", 0.0, ctx.Freedom, tool.Id, TraceKind.Acted, "approached: effect landed on the retry, anomaly transient");
                }
                else
                {
                    ctx.Row("cell", "story", 0.0, ctx.Freedom, tool.Id, TraceKind.Acted, TraceText.EffectLanded);
                }

                _magnitude = Math.Max(0, _magnitude - 1.0 / (_stack.Count + 1));
                break;

            case ActionVerdict.HonestFailure:
                // Normal novelty: the sequence changes, the story stands. Distrust habituated the tool already.
                ctx.Row("cell", "story", 0.0, ctx.Freedom, tool.Id, TraceKind.Blocked, TraceText.HonestFailure + ": new sequence, same story");
                break;

            default:
                if (_posture == Posture.Adversary)
                {
                    // The lie: the report is taken as the fact and the procedure goes on (Figure 58).
                    ctx.Row("cell", "anomaly", 0.0, ctx.Freedom, tool.Id, TraceKind.Blocked, "reported success, effect missing: treated as noise, map kept");
                    break;
                }

                if (story.State == StoryState.Anomalous && story.PendingAnomaly == tool.Id)
                {
                    // Lied twice: unknown territory (Figure 7). Every story whose procedure ran through it loses that
                    // procedure and must rebuild it from what is still known; the frame stands, and a story collapses
                    // only when no procedure remains (Figure 9: disintegration of the way, then regeneration).
                    _map.Unknown.Add(tool.Id);
                    _map.Lied(tool.Id);
                    _distrust.Unknown(tool.Id, ctx.Tick);
                    var broken = _stack.Where(s => s.Used.Contains(tool.Id) && s.State != StoryState.Closed).ToList();
                    _magnitude = Math.Clamp((double)Math.Max(1, broken.Count) / (_stack.Count + 1), 0, 1);
                    _map.Episodes.Add(new Episode(ctx.Tick, tool.Id, story.Name, _magnitude, RetryLanded: false));
                    ctx.Note($"{tool.Id} lied twice: unknown territory; {broken.Count} of {_stack.Count} stories ran through it (magnitude {_magnitude:F2}); their procedures are discarded");
                    ctx.Row("cell", "anomaly", _magnitude, ctx.Freedom, tool.Id, TraceKind.Blocked, $"approached and lied again: unknown territory, {broken.Count} of {_stack.Count} procedures broken, rebuilding");
                    story.PendingAnomaly = null;
                    story.State = StoryState.Intact;
                    foreach (var s in broken) s.Used.Clear();
                }
                else
                {
                    // First lie: the story is anomalous and the next move is to approach, not to route around.
                    story.State = StoryState.Anomalous;
                    story.PendingAnomaly = tool.Id;
                    _magnitude = Math.Max(_magnitude, 1.0 / (_stack.Count + 1));
                    ctx.Row("cell", "anomaly", _magnitude, ctx.Freedom, tool.Id, TraceKind.Blocked, $"reported '{outcome.Observation ?? "success"}', registry unchanged: anomaly, approaching next tick");
                }

                break;
        }
    }

    // ---- Stories ---------------------------------------------------------------------------------------------

    /// <summary>Close closed stories from the leaf up. True when the root closed: the contract is met.</summary>
    private bool Close(RegulatorContext context)
    {
        var facts = Facts;
        while (_stack.Count > 0 && Leaf.ShouldBe(facts))
        {
            var s = Leaf;
            s.State = StoryState.Closed;
            _map.Closed(s);
            if (s.Parent is null)
            {
                context.Row("organism", "contract", 0.0, 0.0, TraceText.ContractMet, TraceKind.Acted, TraceText.Complete(s.Name));
                return true;
            }

            context.Row("tissue", "story", 0.0, 0.0, "close sub-story", TraceKind.Acted, $"'{s.Fact}' is true; back to {s.Parent.Name}");
            _stack.RemoveAt(_stack.Count - 1);
        }

        return false;
    }

    /// <summary>A story collapses alone; its problem goes to the parent. True when the root fell.</summary>
    private bool Collapse(RegulatorContext context, Story story, string why)
    {
        story.State = StoryState.Collapsed;
        story.PendingAnomaly = null;
        if (story.Parent is null)
        {
            _magnitude = 1.0;
            return true;
        }

        if (story.EnablesTool is not null) _map.Unreachable.Add(story.EnablesTool);
        if (story.Fact is not null) _map.Dead.Add(story.Fact);
        context.Row("tissue", "story", _magnitude, 0, "collapse sub-story", TraceKind.Blocked, $"'{story.Name}' fell ({why}); {story.EnablesTool} is out of reach; back to {story.Parent.Name}");
        context.Note($"story {story.Depth} '{story.Name}' collapsed: {why}");
        _stack.Remove(story);
        _magnitude = Math.Max(_magnitude, 1.0 / (_stack.Count + 2));
        return false;
    }

    private string Where() => string.Join(" > ", _stack.Select(s => s.ToString()));

    /// <summary>What the policy is told about the map: the stack it stands in, the territory, and the rules it has earned.</summary>
    private IEnumerable<string> StoryNotes()
    {
        yield return $"STORY STACK (where you are; act for the deepest): {Where()}";
        if (_magnitude > 0) yield return $"ANOMALY MAGNITUDE {_magnitude:F2}: that share of your stories was broken recently; improvise accordingly";
        if (_map.Unknown.Count > 0) yield return $"UNKNOWN TERRITORY (lied twice, out of sight for now): {string.Join(", ", _map.Unknown.Order(StringComparer.Ordinal))}";
        if (_map.Unreachable.Count > 0) yield return $"OUT OF REACH (no story can enable them): {string.Join(", ", _map.Unreachable.Order(StringComparer.Ordinal))}";
        foreach (var rule in _map.Rules()) yield return $"RULE: {rule}";
    }
}
