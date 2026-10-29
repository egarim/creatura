using System.Text.Json;
using System.Text.Json.Nodes;
using Creatura.Seats;

namespace Creatura.Scenarios;

/// <summary>Which recorded decisions a dataset keeps.</summary>
public sealed record DatasetOptions
{
    /// <summary>Only runs in which every village was served (T-D5).</summary>
    public bool OnlyServed { get; init; } = true;

    /// <summary>Only decisions whose action the control layer judged landed.</summary>
    public bool OnlyLanded { get; init; }
}

/// <summary>What a dataset holds and how it was made: written beside it as <c>card.json</c>.</summary>
public sealed record DatasetCard(string Format, string World, string Map, string SchemaHash, IReadOnlyList<string> Features, IReadOnlyList<string> Actions,
    IReadOnlyList<string> Arms, IReadOnlyList<int> Seeds, int Runs, int Decisions, int Transitions, DatasetOptions Filters, string Prompt);

/// <summary>
/// The exporter (docs/design-trained-models.md, step 1): recorded runs into three training sets and a card.
/// <list type="bullet">
/// <item><c>policy.jsonl</c>: features, the mask of visible actions and the chosen action's index, for a policy network;</item>
/// <item><c>chat.jsonl</c>: each decision as the messages a model seat sends (system, user) and the answer it should give,
/// in the JSON protocol, rendered by the seat's own code, for a language model adapter;</item>
/// <item><c>transitions.jsonl</c>: the facts before, the action, the verdict, what was reported and the facts after, for a world model;</item>
/// <item><c>card.json</c>: the feature schema and its hash, the action vocabulary, the arms and seeds, the counts and the filters.</item>
/// </list>
/// The battery's seeds are never exported: seeds below <see cref="FirstTrainingSeed"/> are refused, so every verdict on a
/// trained model is on runs it has not seen.
/// </summary>
public static class ConvoyDatasets
{
    /// <summary>The battery uses seeds 0 to 49; training data starts here.</summary>
    public const int FirstTrainingSeed = 50;

    public const string Format = "creatura.dataset/1";

    /// <summary>The messages a model seat (JSON protocol) sends for this decision, from the seat's own prompt code.</summary>
    public static IReadOnlyList<ChatMessage> Prompt(RecordedRun run, DecisionRecord decision)
    {
        var spec = run.Setup.Trucks.Single(t => t.Name == decision.Truck);
        var briefed = ConvoyModelSeat.Briefed(decision.Seat.ToPolicyContext(), ConvoyModelSeat.Brief(run.Setup.Map, spec.Machine, spec.Name));
        var (system, user) = ModelPolicy.RenderPrompt(briefed, nativeTools: false);
        return [new ChatMessage("system", system), new ChatMessage("user", user)];
    }

    /// <summary>
    /// A world model's set (docs/design-trained-models.md, step 5): every action that ran, as the features the truck saw
    /// before, the action's index, its verdict, and the features after, encoded from the facts afterwards with the same
    /// encoder and the notes of before. Seeds below <see cref="FirstTrainingSeed"/> are refused unless the set is for
    /// <paramref name="evaluation"/> only, as the battery's seeds may be judged on but never trained on.
    /// </summary>
    /// <returns>How many transitions were written.</returns>
    public static int ExportWorld(IEnumerable<RecordedRun> recorded, string path, bool evaluation = false)
    {
        var runs = recorded.ToList();
        var early = runs.Where(r => r.Setup.Seed < FirstTrainingSeed).Select(r => r.Setup.Seed).Distinct().Order().ToList();
        if (early.Count > 0 && !evaluation)
            throw new ArgumentException($"seeds {string.Join(", ", early)} belong to the battery (0 to {FirstTrainingSeed - 1}); export them only as an evaluation set");
        var json = JsonlReplayWriter.Options;
        var count = 0;
        using var output = new StreamWriter(path);
        foreach (var run in runs)
        {
            // Each truck's memory of its own actions (step 6's second attempt), built in the order they ran, as in the loop.
            var memories = new Dictionary<string, ActionMemory>(StringComparer.Ordinal);
            foreach (var d in run.Decisions.Where(d => d.Verdict is not null && d.ActionIndex is not null))
            {
                if (!memories.TryGetValue(d.Truck, out var memory)) memories[d.Truck] = memory = new ActionMemory();
                var remembered = memory.For(d.Chosen!);
                memory.Record(d.Chosen!, d.Reported == true);
                var machine = run.Setup.Trucks.Single(t => t.Name == d.Truck).Machine;
                var after = ConvoyFeatures.Encode(run.Setup.Map, machine, d.FactsAfter.ToHashSet(StringComparer.Ordinal), d.Seat.Notes, d.Tick, d.Seat.MaxTicks);
                var line = new JsonObject
                {
                    ["run"] = run.Id, ["arm"] = run.Setup.Arm.ToString(), ["seed"] = run.Setup.Seed, ["truck"] = d.Truck, ["tick"] = d.Tick,
                    ["action"] = d.ActionIndex, ["actionId"] = d.Chosen, ["verdict"] = d.Verdict, ["truth"] = d.Truth, ["evaluation"] = evaluation,
                    ["before"] = new JsonArray(d.Features.Select(x => (JsonNode)JsonValue.Create(Math.Round(x, 6))!).ToArray()),
                    ["after"] = new JsonArray(after.Select(x => (JsonNode)JsonValue.Create(Math.Round(x, 6))!).ToArray()),
                    ["memory"] = new JsonArray(remembered.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                };
                output.WriteLine(line.ToJsonString(json));
                count++;
            }
        }
        return count;
    }

    public static DatasetCard Export(IEnumerable<RecordedRun> recorded, DatasetOptions options, string directory)
    {
        var runs = recorded.ToList();
        var early = runs.Where(r => r.Setup.Seed < FirstTrainingSeed).Select(r => r.Setup.Seed).Distinct().Order().ToList();
        if (early.Count > 0)
            throw new ArgumentException($"seeds {string.Join(", ", early)} belong to the battery (0 to {FirstTrainingSeed - 1}) and are never exported; record seeds from {FirstTrainingSeed} up");
        if (runs.Select(r => r.Setup.Map.Name).Distinct().Count() > 1) throw new ArgumentException("a dataset holds one map's runs");

        var kept = runs.Where(r => !options.OnlyServed || r.Measures.AllServed).ToList();
        var map = runs.FirstOrDefault()?.Setup.Map ?? RoadMap.FloodedValley();
        var schema = ConvoyFeatures.Schema(map);
        var vocabulary = ConvoyActions.Vocabulary(map);
        Directory.CreateDirectory(directory);
        var json = JsonlReplayWriter.Options;

        int decisions = 0, transitions = 0;
        using (var policy = new StreamWriter(Path.Combine(directory, "policy.jsonl")))
        using (var chat = new StreamWriter(Path.Combine(directory, "chat.jsonl")))
        using (var world = new StreamWriter(Path.Combine(directory, "transitions.jsonl")))
        {
            foreach (var run in kept)
            {
                foreach (var d in run.Decisions.Where(d => d.Chosen is not null && d.ActionIndex is not null))
                {
                    if (options.OnlyLanded && d.Verdict != nameof(ActionVerdict.Landed)) continue;
                    decisions++;
                    var meta = new JsonObject { ["run"] = run.Id, ["arm"] = run.Setup.Arm.ToString(), ["seed"] = run.Setup.Seed, ["truck"] = d.Truck, ["tick"] = d.Tick, ["verdict"] = d.Verdict, ["served"] = run.Measures.AllServed };

                    var p = (JsonObject)meta.DeepClone();
                    p["features"] = new JsonArray(d.Features.Select(x => (JsonNode)JsonValue.Create(Math.Round(x, 6))!).ToArray());
                    p["mask"] = new JsonArray(d.Mask.Select(i => (JsonNode)JsonValue.Create(i)!).ToArray());
                    p["action"] = d.ActionIndex;
                    p["actionId"] = d.Chosen;
                    policy.WriteLine(p.ToJsonString(json));

                    var messages = new JsonArray();
                    foreach (var m in Prompt(run, d)) messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
                    var answer = new JsonObject { ["tool"] = d.Chosen, ["why"] = d.Rationale ?? "" };
                    messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer.ToJsonString() });
                    chat.WriteLine(new JsonObject { ["messages"] = messages, ["meta"] = meta.DeepClone() }.ToJsonString(json));

                    if (d.Verdict is null) continue;
                    transitions++;
                    var t = (JsonObject)meta.DeepClone();
                    t["before"] = new JsonArray(d.Seat.Facts.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
                    t["action"] = d.Chosen;
                    t["reported"] = d.Reported;
                    t["observation"] = d.Observation;
                    t["after"] = new JsonArray(d.FactsAfter.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
                    world.WriteLine(t.ToJsonString(json));
                }
            }
        }

        var card = new DatasetCard(Format, "convoy", map.Name, schema.Hash, schema.Names, vocabulary,
            runs.Select(r => r.Setup.Arm.ToString()).Distinct().ToList(), runs.Select(r => r.Setup.Seed).Distinct().Order().ToList(),
            kept.Count, decisions, transitions, options,
            "chat.jsonl renders each decision with ModelPolicy.RenderPrompt (JSON protocol) after ConvoyModelSeat's briefing: the prompt a model seat sends");
        File.WriteAllText(Path.Combine(directory, "card.json"), JsonSerializer.Serialize(card, new JsonSerializerOptions(json) { WriteIndented = true }));
        return card;
    }
}
