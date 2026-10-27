using System.Globalization;

namespace Creatura;

/// <summary>
/// Who the creature is to itself, before any run: the role and the principal its self-model is created with,
/// what it has undertaken, how far it may reach, and the vessel it runs in. A regulator seeds its self-model
/// from this (<see cref="NewSelfModel"/>); nothing here changes during a run. The literals are the designs'
/// own strings, so a facade built on them renders what it always rendered.
/// </summary>
public sealed record Identity(string Role, string Principal)
{
    /// <summary>What it has undertaken and not yet closed; the self-model starts with these.</summary>
    public IReadOnlyList<string> OpenCommitments { get; init; } = [];

    /// <summary>How far it may reach (a light cone's scope, a tenant, a site). Null when the design states none, as every design today.</summary>
    public string? Scope { get; init; }

    /// <summary>The hardware and the model it runs on. Null until a host states it or an extension measures it.</summary>
    public Vessel? Vessel { get; init; }

    /// <summary>The Bach agent: a service operator for joche, committed to restoring the service.</summary>
    public static readonly Identity Bach = new("service operator", "joche") { OpenCommitments = ["restore the service to health"] };

    /// <summary>The colony: it keeps no self-model of its own, so this only names it to the layer's ledger.</summary>
    public static readonly Identity Levin = new("colony", "joche");

    /// <summary>The hybrid's organism: the colony's owner, with the same commitment.</summary>
    public static readonly Identity Hybrid = new("colony owner", "joche") { OpenCommitments = ["restore the service to health"] };

    /// <summary>The hybrid's cell: an effect cell whose principal is the colony.</summary>
    public static readonly Identity Cell = new("effect cell", "colony");

    /// <summary>The map-keeper: it keeps no self-model of its own (its memory is the territory map), so this only names it to the layer's ledger.</summary>
    public static readonly Identity MapKeeper = new("map-keeper", "joche");

    /// <summary>The Agent Framework host's operator. Its one commitment is the task's name, added at run time.</summary>
    public static readonly Identity Controlled = new("controlled operator", "joche");

    /// <summary>A fresh self-model seeded from this identity: role, principal, stage, the commitments and the vessel.</summary>
    public SelfModel NewSelfModel(int stageLevel = 3)
    {
        var self = new SelfModel { Role = Role, Principal = Principal, StageLevel = stageLevel, Vessel = Vessel };
        self.OpenCommitments.AddRange(OpenCommitments);
        return self;
    }
}

/// <summary>
/// The vessel: the model and the machine a creature runs in. The model half is what a provider publishes and
/// what the bench measured (name, protocol, window, speed, price); the machine half is where the process
/// lives (machine, operating system, network position), which open decision 16 wants an identity bound to.
/// Every part is optional and <see cref="Render"/> says only what is known, never zero for unknown.
/// </summary>
public sealed record Vessel
{
    public string? Model { get; init; }

    /// <summary>How the seat answers: "tools" (one function call) or "json" (one object).</summary>
    public string? Protocol { get; init; }

    public bool? Reasoning { get; init; }

    /// <summary>The context window in tokens, as run.</summary>
    public int? ContextWindow { get; init; }

    public double? SecondsPerCall { get; init; }

    public double? TokensPerSecond { get; init; }

    public decimal? InputPerMillion { get; init; }

    public decimal? OutputPerMillion { get; init; }

    /// <summary>The machine the process runs on (a host name, a device).</summary>
    public string? Machine { get; init; }

    /// <summary>The operating system of that machine.</summary>
    public string? Os { get; init; }

    /// <summary>Where the machine sits on the network (local, a mesh address, a cloud region).</summary>
    public string? Network { get; init; }

    /// <summary>The vessel on one line: the model with its known figures in brackets, then "on" the machine half.</summary>
    public string Render()
    {
        var figures = new List<string>();
        if (Protocol is not null) figures.Add(Protocol);
        if (Reasoning == true) figures.Add("reasoning");
        if (ContextWindow is { } window) figures.Add($"ctx {window.ToString("N0", CultureInfo.InvariantCulture)}");
        if (SecondsPerCall is { } seconds) figures.Add(seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s/call");
        if (TokensPerSecond is { } tps) figures.Add(tps.ToString("0", CultureInfo.InvariantCulture) + " tokens/s");
        if (InputPerMillion is not null || OutputPerMillion is not null)
        {
            figures.Add($"${(InputPerMillion ?? 0).ToString("0.00", CultureInfo.InvariantCulture)} in / ${(OutputPerMillion ?? 0).ToString("0.00", CultureInfo.InvariantCulture)} out per million tokens");
        }

        var model = Model ?? "unknown model";
        var head = figures.Count == 0 ? model : $"{model} ({string.Join(", ", figures)})";
        var place = new[] { Machine, Os, Network }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return place.Count == 0 ? head : $"{head} on {string.Join(", ", place)}";
    }

    public override string ToString() => Render();
}
