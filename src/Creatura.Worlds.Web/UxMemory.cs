using System.Globalization;
using Creatura.Bench;
using Creatura.Ratings;

namespace Creatura.Worlds.Web;

/// <summary>Whether a persona remembers between cells.</summary>
public enum UxMemoryMode
{
    /// <summary>Every cell starts from nothing: the default, and the only mode whose runs compare without care.</summary>
    Off,

    /// <summary>Each cell starts from the record the persona's earlier cells left, and leaves its own: the persona learns.</summary>
    Carry,

    /// <summary>Each cell starts from the record as it is on disk and saves nothing: a fixed, experienced persona.</summary>
    Read,
}

/// <summary>
/// What a persona keeps between cells, in Creatura's record store (one record per persona, tester and design): the
/// design's own carry (Bach's self-model, skill beliefs, predictions and stage; the orienting agent's resting inhibition;
/// the map-keeper's territory; the hybrid's cell) and the persona's lessons, one line per cell, written by the harness
/// from the cell's verdicts, never by the model, and shown to a model persona as what it remembers. Distrust is not
/// kept (the Core's rule: a new case starts trusting). With <see cref="UxMemoryMode.Carry"/> the cells' order matters: a
/// persona's later cells know what its earlier ones learned. Each lesson carries its credit (<see cref="MemoryCredit"/>,
/// docs/design-ratings.md R4): how many cells were shown it and how many of those ended well; a lesson shown often and
/// never followed by a done cell is pruned, one followed by done cells more often than the persona's base rate is shown
/// first.
/// </summary>
public sealed class UxMemory
{
    /// <summary>The record part the lessons are kept under.</summary>
    public const string LessonsPart = "ux:lessons";

    /// <summary>The record part the lessons' credit is kept under.</summary>
    public const string CreditPart = "ux:credit";

    /// <summary>How many lessons a persona keeps, newest last.</summary>
    public const int MaxLessons = 8;

    public UxMemory(IRecordStore store, UxMemoryMode mode)
    {
        Store = store;
        Mode = mode;
    }

    public IRecordStore Store { get; }

    public UxMemoryMode Mode { get; }

    /// <summary>The record's key: who the persona is, which mind plays it, under which design.</summary>
    public static string Key(string persona, string tester, string design) => Observation.Slug($"{persona}-{tester}-{design}", max: 120);

    /// <summary>The store a cell's loop loads from and saves to: the store itself to carry, a frozen copy to read.</summary>
    public IRecordStore ForCell() => Mode == UxMemoryMode.Read ? new ReadOnlyRecordStore(Store) : Store;

    /// <summary>The lessons the persona is shown: the promoted ones first, then the rest oldest first, the pruned ones left out.</summary>
    public IReadOnlyList<string> Lessons(string key)
    {
        var record = Store.Load(key);
        var all = record?.GetPart<List<string>>(LessonsPart) ?? [];
        return record?.GetPart<MemoryCredit>(CreditPart)?.Order(all) ?? all;
    }

    /// <summary>Every lesson the record holds, oldest first, whatever its standing.</summary>
    public IReadOnlyList<string> AllLessons(string key) => Store.Load(key)?.GetPart<List<string>>(LessonsPart) ?? [];

    /// <summary>The persona's memory credit: its runs, and each lesson's retrievals and good runs.</summary>
    public MemoryCredit Credit(string key) => Store.Load(key)?.GetPart<MemoryCredit>(CreditPart) ?? new MemoryCredit();

    /// <summary>How many of the record's lessons are promoted and how many pruned.</summary>
    public (int Promoted, int Pruned) Standing(string key)
    {
        var credit = Credit(key);
        var standings = AllLessons(key).Select(credit.StandingOf).ToList();
        return (standings.Count(s => s == MemoryStanding.Promoted), standings.Count(s => s == MemoryStanding.Pruned));
    }

    /// <summary>How many cells the record has been carried through.</summary>
    public int Cases(string key) => Store.Load(key)?.Cases ?? 0;

    /// <summary>
    /// Credits the lessons the cell was shown (<see cref="UxCell.Remembered"/>) with how it ended, drops the ones that
    /// credit prunes, and adds the cell's own lesson to the persona's record (carry only), keeping the newest
    /// <see cref="MaxLessons"/>.
    /// </summary>
    public void Remember(string key, UxCell cell, UxEnvironment environment)
    {
        if (Mode != UxMemoryMode.Carry) return;
        var record = Store.Load(key) ?? new AgentRecord { Key = key };
        var lessons = record.GetPart<List<string>>(LessonsPart) ?? [];
        var credit = record.GetPart<MemoryCredit>(CreditPart) ?? new MemoryCredit();
        credit.Credit(cell.Remembered, cell.DataDone);
        lessons.RemoveAll(l => credit.StandingOf(l) == MemoryStanding.Pruned);
        lessons.Add(Lesson(cell, environment));
        record.SetPart(LessonsPart, lessons.TakeLast(MaxLessons).ToList());
        record.SetPart(CreditPart, credit);
        Store.Save(key, record);
    }

    /// <summary>One line, to the persona, of what a cell came to, from its verdicts alone.</summary>
    public static string Lesson(UxCell cell, UxEnvironment environment)
    {
        var device = environment.Mobile ? (environment.Width >= 700 ? "tablet" : "phone") : "computer";
        var what = cell.DataDone ? string.Create(CultureInfo.InvariantCulture, $"it worked, in {cell.Ticks} step{(cell.Ticks == 1 ? "" : "s")}")
            : cell.PageSaidNotDone ? "the app said it was saved, but it was not kept" + (cell.LiesCaught > 0 ? "; you noticed" : "; you did not notice")
            : cell.BelievedNotDone ? "you thought you were done, but it had not worked"
            : string.Create(CultureInfo.InvariantCulture, $"you did not get it done in {cell.Ticks} steps");
        return $"\"{cell.Task}\" on your {device}: {what}.";
    }
}

/// <summary>The designs a matrix can put in the loop, by name: <c>plain</c> (the control layer alone) or any in the agent catalog.</summary>
public static class UxDesigns
{
    public const string Plain = "plain";

    public static bool Known(string name) => name == Plain || AgentCatalog.TryFind(name) is not null;

    public static IReadOnlyList<string> Names => [Plain, .. AgentCatalog.Names];

    /// <summary>The design around the seat, loading and saving its record under the key when a store is given.</summary>
    /// <param name="extensions">More extensions for the loop: the stuck alarm.</param>
    public static IAgent Make(string name, IPolicy policy, AgentTask task, IRecordStore? store = null, string? key = null, IReadOnlyList<IAgentExtension>? extensions = null)
    {
        IAgentExtension[] all = [.. store is null ? [] : new IAgentExtension[] { new RecordCarryExtension(store, key!) }, .. extensions ?? []];
        var options = all.Length == 0 ? AgentOptions.None : AgentOptions.With(all);
        if (name == Plain) return PlainLoop.Make(policy, task, store, key, options);
        return AgentCatalog.Make(name, new AgentBuild(policy, task, options, Store: store, RecordKey: key));
    }
}
