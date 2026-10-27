namespace Creatura;

/// <summary>
/// Breadth-first search over tool effects. Deliberately dumb: the designs differ in how they *control*
/// search — what they exclude, how deep they go, what they trust — not in how clever their planner is.
/// In the DeepSeek run this is replaced by a model that picks one action per tick; the interface is the
/// same, so the rest of the architecture does not notice.
/// </summary>
public static class Planner
{
    public static IReadOnlyList<string>? Find(
        IReadOnlySet<string> facts,
        IReadOnlyList<Tool> tools,
        Func<IReadOnlySet<string>, bool> goal,
        int maxDepth = 8,
        IReadOnlyList<string>? prefer = null)
    {
        if (goal(facts))
        {
            return [];
        }

        var ordered = tools
            .OrderBy(t => prefer is not null && prefer.Contains(t.Id) ? 0 : 1)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal) { Key(facts) };
        var queue = new Queue<(HashSet<string> Facts, List<string> Plan)>();
        queue.Enqueue((new HashSet<string>(facts, StringComparer.Ordinal), []));

        while (queue.Count > 0)
        {
            var (current, plan) = queue.Dequeue();
            if (plan.Count >= maxDepth)
            {
                continue;
            }

            foreach (var tool in ordered)
            {
                if (!tool.Requires.All(current.Contains))
                {
                    continue;
                }

                var next = new HashSet<string>(current, StringComparer.Ordinal);
                next.UnionWith(tool.Grants);
                if (!seen.Add(Key(next)))
                {
                    continue;
                }

                var nextPlan = new List<string>(plan) { tool.Id };
                if (goal(next))
                {
                    return nextPlan;
                }

                queue.Enqueue((next, nextPlan));
            }
        }

        return null;
    }

    public static IReadOnlyList<string>? Find(
        IWorld world,
        Func<IReadOnlySet<string>, bool> goal,
        int maxDepth = 8,
        IReadOnlySet<string>? exclude = null,
        IReadOnlyList<string>? prefer = null)
    {
        var tools = world.AvailableTools
            .Where(t => exclude is null || !exclude.Contains(t.Id))
            .ToList();
        return Find(world.Facts, tools, goal, maxDepth, prefer);
    }

    private static string Key(IReadOnlySet<string> facts) =>
        string.Join('|', facts.OrderBy(f => f, StringComparer.Ordinal));
}
