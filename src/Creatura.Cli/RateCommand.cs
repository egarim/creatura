using Creatura.Ratings;

namespace Creatura.Cli;

/// <summary>
/// <c>creatura rate</c> (docs/design-ratings.md): the leaderboards of the models, the designs and the prompts, read from
/// the rows under the paths given: the bench's <c>scoreboard.csv</c> and <c>results.csv</c>, a matrix's <c>report.json</c>,
/// a two-creatures run's <c>summary.json</c>. Exit 0 with rows, 2 for a usage error, 1 when nothing could be read.
/// </summary>
public static class RateCommand
{
    public const string Usage = """
        usage: creatura rate <path>... [--by model|design|prompt] [--json] [--out <file>]

          <path>                       a file, or a folder walked for scoreboard.csv, results.csv, report.json and summary.json
          --by model|design|prompt     one leaderboard (default: every axis with a rated seat)
          --json                       the numbers as JSON instead of the tables
          --out <file>                 write there instead of the console

        A seat is rated only against the seats that ran the same job with the other axes held fixed (docs/design-ratings.md).
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var paths = new List<string>();
        Axis? axis = null;
        var json = false;
        string? outFile = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                case "--json":
                    json = true;
                    break;
                case "--by":
                    if (i + 1 >= args.Length || !Enum.TryParse<Axis>(args[++i], ignoreCase: true, out var parsed)) return Fail(error, "--by takes model, design or prompt");
                    axis = parsed;
                    break;
                case "--out":
                    if (i + 1 >= args.Length) return Fail(error, "--out takes a file");
                    outFile = args[++i];
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal)) return Fail(error, $"unknown option {args[i]}");
                    paths.Add(args[i]);
                    break;
            }
        }

        if (paths.Count == 0) return Fail(error, "name at least one path with rows");
        var problems = new List<string>();
        var runs = Sources.Read(paths, problems);
        foreach (var problem in problems) error.WriteLine($"skipped {problem}");
        if (runs.Count == 0)
        {
            error.WriteLine("no rows: nothing named scoreboard.csv, results.csv, report.json or summary.json could be read under the paths");
            return 1;
        }

        var boards = axis is { } one ? [Leaderboard.Compute(runs, one)] : Leaderboard.All(runs);
        var text = json ? Leaderboard.ToJson(boards) : string.Join(Environment.NewLine, boards.Select(Leaderboard.Render));
        var sources = runs.GroupBy(r => r.Source).Select(g => $"{g.Count()} {g.Key}").ToList();
        if (outFile is not null)
        {
            File.WriteAllText(outFile, text);
            output.WriteLine($"{runs.Count} runs ({string.Join(", ", sources)}); written to {outFile}");
        }
        else
        {
            if (!json) output.WriteLine($"{runs.Count} runs ({string.Join(", ", sources)})" + Environment.NewLine);
            output.Write(text);
            if (!text.EndsWith('\n')) output.WriteLine();
        }

        return 0;
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine(Usage);
        return 2;
    }
}
