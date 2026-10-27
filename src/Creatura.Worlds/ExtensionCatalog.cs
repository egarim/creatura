namespace Creatura.Worlds;

/// <summary>What an extension factory may read: its own name, every name on the switchboard, and the series' shared memory and journal.</summary>
public sealed class ExtensionRequest
{
    public ExtensionRequest(string name, IReadOnlyList<string> names, EffectMemory memory, DreamJournal journal)
    {
        Name = name;
        Names = names;
        Memory = memory;
        Journal = journal;
    }

    public string Name { get; }

    /// <summary>Every name being loaded, so one extension can see whether another rides with it (exploration dreams only when dream is on).</summary>
    public IReadOnlyList<string> Names { get; }

    public EffectMemory Memory { get; }

    public DreamJournal Journal { get; }

    public bool Has(string name) => Names.Contains(name, StringComparer.Ordinal);
}

/// <summary>
/// The switchboard as a registry: a name on the command line or in a golden's file name becomes an extension loaded
/// into whichever design runs. Seeded with the Core's names; a consumer registers what it owns (<c>biology</c> lives
/// with the model seats, <c>critic</c> with whoever owns the critic).
/// </summary>
public static class ExtensionCatalog
{
    private static readonly object Gate = new();
    private static readonly List<string> Order = [];
    private static readonly Dictionary<string, Func<ExtensionRequest, IAgentExtension>> ByName = new(StringComparer.Ordinal);

    static ExtensionCatalog()
    {
        Register("honest", _ => new HonestFailureExtension());
        Register("record", _ => new RecordCarryExtension());
        Register("record:resample", _ => new RecordCarryExtension(resampleEvery: 3));
        Register("explore:budget", r => new ExplorationExtension(r.Memory, scaled: false, Dreaming(r) ? r.Journal : null, r.Has("development")));
        Register("explore:scaled", r => new ExplorationExtension(r.Memory, scaled: true, Dreaming(r) ? r.Journal : null, r.Has("development")));
        Register("curiosity", r => new CuriosityExtension(r.Memory));
        Register("development", r => new DevelopmentExtension(r.Memory));
        Register("dream", r => new DreamSleepExtension(r.Memory, r.Journal));
        Register("dream:replace", r => new DreamSleepExtension(r.Memory, r.Journal, replacesDesignSleep: true));
    }

    private static bool Dreaming(ExtensionRequest r) => r.Has("dream") || r.Has("dream:replace");

    /// <summary>Adds or replaces a factory under its name.</summary>
    public static void Register(string name, Func<ExtensionRequest, IAgentExtension> make)
    {
        lock (Gate)
        {
            if (!ByName.ContainsKey(name)) Order.Add(name);
            ByName[name] = make;
        }
    }

    public static bool Knows(string name)
    {
        lock (Gate) return ByName.ContainsKey(name);
    }

    public static IReadOnlyList<string> Names
    {
        get { lock (Gate) return Order.ToList(); }
    }

    /// <summary>
    /// The extensions named, in order, as one <see cref="AgentOptions"/>. A name nobody registered goes to
    /// <paramref name="unknown"/> and is skipped, or throws when no handler is given. The memory and the journal are
    /// per series (the caller keeps them across runs); a fresh one is made when none is given.
    /// </summary>
    public static AgentOptions Parse(IEnumerable<string> names, EffectMemory? memory = null, DreamJournal? journal = null, Action<string>? unknown = null)
    {
        var list = names.ToList();
        memory ??= new EffectMemory();
        journal ??= new DreamJournal();
        var extensions = new List<IAgentExtension>();
        foreach (var name in list)
        {
            Func<ExtensionRequest, IAgentExtension>? make;
            lock (Gate) make = ByName.GetValueOrDefault(name);
            if (make is null)
            {
                if (unknown is null) throw new ArgumentException($"unknown extension '{name}'; known: {string.Join(", ", Names)}");
                unknown(name);
                continue;
            }

            extensions.Add(make(new ExtensionRequest(name, list, memory, journal)));
        }

        return new AgentOptions(extensions);
    }
}
