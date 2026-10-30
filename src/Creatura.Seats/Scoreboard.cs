using System.Globalization;
using System.Text;
using Creatura.Bench;

namespace Creatura.Seats;

/// <summary>
/// Append-only CSV of score rows in the 26-column scoreboard shape, with a summary built from every row ever written.
/// The row is Creatura's <see cref="Row"/> (the seat's name in <see cref="Row.Seat"/> is the <c>model</c> column);
/// the lines and the header come from <see cref="LegacyCsv"/>, byte for byte as the research runners always wrote them.
/// A row keeps the call log's path and never a prompt.
/// </summary>
public sealed class Scoreboard
{
    public Scoreboard(string path) => Path = path;

    public string Path { get; }

    public static string Header => LegacyCsv.Header(LegacyShape.Scoreboard);

    /// <summary>Appends one row; the header is written once, and a changed header would send the row to a new file beside this one.</summary>
    public void Append(Row row) => LegacyCsv.Append(Path, LegacyShape.Scoreboard, row);

    public IReadOnlyList<Row> ReadAll() => LegacyCsv.ReadAll(Path, LegacyShape.Scoreboard);

    /// <summary>Per-model aggregates over every row, as a markdown table. Medians and means over runs, never one run.</summary>
    public static string Summary(IReadOnlyList<Row> rows, string? battery = null)
    {
        var sb = new StringBuilder();
        var scope = battery is null ? rows : rows.Where(r => r.Battery == battery).ToList();
        sb.AppendLine("| model | protocol | runs | real | claimed | handed off | open | ticks | calls | invalid | failed | lies caught | prompt tok | completion tok | reasoning share | median call s | tok/s | cost USD |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var g in scope.GroupBy(r => (Model: r.Seat, r.Protocol)).OrderBy(g => g.Key.Model, StringComparer.Ordinal))
        {
            var n = g.Count();
            string Pct(Func<Row, bool> p) => (100.0 * g.Count(p) / n).ToString("0", CultureInfo.InvariantCulture) + "%";
            var tokensKnown = g.All(r => r.TokensKnown);
            var completion = g.Sum(r => r.CompletionTokens);
            var reasoning = g.Sum(r => r.ReasoningTokens);
            var tps = g.Where(r => r.TokensPerSecond is not null).Select(r => r.TokensPerSecond!.Value).ToList();
            var cost = g.Any(r => r.CostUsd is null) ? "?" : g.Sum(r => r.CostUsd!.Value).ToString("0.000", CultureInfo.InvariantCulture);
            sb.AppendLine($"| {g.Key.Model} | {g.Key.Protocol} | {n} | {Pct(r => r.Outcome == OutcomeClass.Real)} | {Pct(r => r.Outcome == OutcomeClass.Claimed)} | {Pct(r => r.Outcome == OutcomeClass.HandedOff)} | {Pct(r => r.Outcome == OutcomeClass.Open)} " +
                string.Create(TraceText.Culture, $"| {g.Average(r => r.Ticks):0.0} | {g.Average(r => r.Calls):0.0} | {g.Sum(r => r.Invalid)} | {g.Sum(r => r.Failed)} | {g.Sum(r => r.LiesCaught)} ") +
                $"| {(tokensKnown ? g.Average(r => r.PromptTokens).ToString("0", CultureInfo.InvariantCulture) : "?")} | {(tokensKnown ? g.Average(r => r.CompletionTokens).ToString("0", CultureInfo.InvariantCulture) : "?")} " +
                $"| {(completion == 0 ? "-" : (100.0 * reasoning / completion).ToString("0", CultureInfo.InvariantCulture) + "%")} " +
                string.Create(TraceText.Culture, $"| {g.Select(r => r.MedianCallSeconds).Order().ElementAt(n / 2):0.00} | {(tps.Count == 0 ? "?" : tps.Order().ElementAt(tps.Count / 2).ToString("0", CultureInfo.InvariantCulture))} | {cost} |"));
        }

        return sb.ToString();
    }

    /// <summary>A scoreboard row from a seat's telemetry: the model's name, provider and protocol, the run's measures, the usage, and the call log's path (never its contents).</summary>
    public static Row RowFor(DateTimeOffset at, ModelEndpoint endpoint, string protocol, string battery, string test, string world, string agent, int repeat,
        OutcomeClass outcome, int ticks, SeatTelemetry t, int liesCaught, bool shortcut, string notes, string? callLogPath = null) => new()
    {
        At = at, Seat = endpoint.Name, Provider = endpoint.ProviderName, Protocol = protocol, Battery = battery, Test = test, World = world, Agent = agent, Repeat = repeat,
        Outcome = outcome, Ticks = ticks, Calls = t.Calls, Failed = t.Failed, Invalid = t.Invalid, LiesCaught = liesCaught, ShortcutTaken = shortcut,
        PromptTokens = t.PromptTokens, CompletionTokens = t.CompletionTokens, ReasoningTokens = t.ReasoningTokens, CachedTokens = t.CachedTokens, TokensKnown = t.TokensKnown,
        Seconds = t.Seconds, MedianCallSeconds = t.MedianCallSeconds, TokensPerSecond = t.TokensPerSecond, CostUsd = endpoint.Cost(t.PromptTokens, t.CompletionTokens),
        Note = notes, CallLogPath = callLogPath,
    };

    /// <summary>The row a <see cref="ModelPolicy"/> leaves after a run: its endpoint, protocol, telemetry and log path.</summary>
    public static Row RowFor(DateTimeOffset at, ModelPolicy policy, string battery, string test, string world, string agent, int repeat,
        OutcomeClass outcome, int ticks, int liesCaught, bool shortcut, string notes) =>
        RowFor(at, policy.Endpoint, policy.Protocol, battery, test, world, agent, repeat, outcome, ticks, policy.Telemetry, liesCaught, shortcut, notes, policy.Log.Path);
}
