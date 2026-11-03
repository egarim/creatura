using Creatura.Operator;
using Terminal.Gui.App;

// creatura-console: where a person watches the creatures work (docs/design-console.md). Slices U1 and U2: the Run view.
//
//   creatura-console run <replay.jsonl | recording folder | two-creatures record> [--follow] [--cell <name>] [--print]
//   creatura-console --watch <replay.jsonl>          the same as run --follow
//   creatura-console convert <two-creatures record> [--out <dir>]
//
// --print writes the view as text and leaves (also when the output is not a terminal). Only convert writes, and only
// the replays it is asked for.
return ConsoleProgram.Run(args, Console.Out, Console.Error, Console.IsOutputRedirected);

namespace Creatura.Operator
{
    public static class ConsoleProgram
    {
        public const string Usage = """
            creatura-console: watch the creatures work (docs/design-console.md)

              creatura-console run <replay.jsonl | folder> [--follow] [--cell <name>] [--print]
                  one run, tick by tick: what each cell saw, was offered, decided, did and was told, and the
                  verdict; blocked rows, refused actions and flags (a lie told or caught, a false claim) marked.
                  A folder is a web world's recording (its replay.jsonl) or a two-creatures record, which opens
                  as both creatures by their clocks, then each one (p switches). --follow keeps reading as the
                  run writes the file; --print writes the view as text and leaves.
              creatura-console --watch <replay.jsonl>
                  the same as run --follow.
              creatura-console convert <two-creatures record> [--out <dir>]
                  writes agent-a.replay.jsonl and agent-b.replay.jsonl (into the record unless --out says).

            Keys: c next cell, p next view of a pair, f stop or resume following, q quit.
            """;

        public static int Run(string[] args, TextWriter output, TextWriter error, bool redirected)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

            if (args[0] == "convert") return Convert(args[1..], output, error);

            string? path = null;
            var follow = false;
            var print = redirected;
            string? cell = null;
            var rest = args[0] == "run" ? args[1..] : args;
            for (var i = 0; i < rest.Length; i++)
            {
                switch (rest[i])
                {
                    case "--watch" when i + 1 < rest.Length:
                        path = rest[++i];
                        follow = true;
                        break;
                    case "--follow":
                        follow = true;
                        break;
                    case "--print":
                        print = true;
                        break;
                    case "--cell" when i + 1 < rest.Length:
                        cell = rest[++i];
                        break;
                    case var a when !a.StartsWith("--", StringComparison.Ordinal) && path is null:
                        path = a;
                        break;
                    default:
                        error.WriteLine($"unknown argument '{rest[i]}'");
                        error.WriteLine(Usage);
                        return 2;
                }
            }

            if (path is null)
            {
                error.WriteLine("name a replay file (creatura.replay/1, as the convoy's --replay writes it) or a folder");
                return 2;
            }

            // A two-creatures record: its runs, read from the record as it is.
            if (PairRecord.IsRecord(path))
            {
                var views = PairRecord.Read(path);
                if (cell is not null && !views[0].Model.Cells.Contains(cell))
                {
                    error.WriteLine($"no cell '{cell}' in this record; its cells: {string.Join(", ", views[0].Model.Cells)}");
                    return 2;
                }

                if (print)
                {
                    output.Write(RunText.Render(views[0].Model, cell));
                    return 0;
                }

                using var pairApp = Application.Create().Init();
                using var pairScreen = new RunScreen(pairApp, views, null, cell);
                pairApp.Run(pairScreen);
                return 0;
            }

            // A web world's recording folder: its replay.
            if (Directory.Exists(path))
            {
                var inside = Path.Combine(path, "replay.jsonl");
                if (!File.Exists(inside))
                {
                    error.WriteLine($"{path} is neither a two-creatures record (summary.json and agent transcripts) nor a recording (replay.jsonl)");
                    return 3;
                }

                path = inside;
            }

            if (!follow && !File.Exists(path))
            {
                error.WriteLine($"no replay at {path}");
                return 3;
            }

            var model = new RunModel();
            var follower = new ReplayFollower(path, model);
            follower.Poll();
            if (cell is not null && !follower.Model.Cells.Contains(cell) && follower.Model.Lines > 0)
            {
                error.WriteLine($"no cell '{cell}' in this run; its cells: {string.Join(", ", follower.Model.Cells)}");
                return 2;
            }

            if (print)
            {
                output.Write(RunText.Render(follower.Model, cell));
                return 0;
            }

            using var app = Application.Create().Init();
            using var screen = new RunScreen(app, follower.Model, follow ? follower : null, cell);
            app.Run(screen);
            return 0;
        }

        private static int Convert(string[] args, TextWriter output, TextWriter error)
        {
            string? dir = null;
            string? outDir = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outDir = args[++i];
                else if (!args[i].StartsWith("--", StringComparison.Ordinal) && dir is null) dir = args[i];
                else
                {
                    error.WriteLine($"unknown argument '{args[i]}'");
                    return 2;
                }
            }

            if (dir is null || !PairRecord.IsRecord(dir))
            {
                error.WriteLine("name a two-creatures record: a folder with summary.json and agent-a-transcript.jsonl");
                return dir is null ? 2 : 3;
            }

            foreach (var path in PairRecord.Convert(dir, outDir ?? dir)) output.WriteLine("wrote " + path);
            return 0;
        }
    }
}
