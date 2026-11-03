using Creatura.Operator;
using Terminal.Gui.App;

// creatura-console: where a person watches the creatures work (docs/design-console.md). Slice U1: the Run view.
//
//   creatura-console run <replay.jsonl> [--follow] [--cell <name>] [--print]
//   creatura-console --watch <replay.jsonl>          the same as run --follow
//
// --print writes the view as text and leaves (also when the output is not a terminal). Nothing is ever written to disk.
return ConsoleProgram.Run(args, Console.Out, Console.Error, Console.IsOutputRedirected);

namespace Creatura.Operator
{
    public static class ConsoleProgram
    {
        public const string Usage = """
            creatura-console: watch the creatures work (docs/design-console.md)

              creatura-console run <replay.jsonl> [--follow] [--cell <name>] [--print]
                  one run, tick by tick: what each cell saw, did and was told, and the verdict; blocked rows,
                  refused actions and flags (a lie told or caught) marked. --follow keeps reading as the run
                  writes the file; --print writes the view as text and leaves.
              creatura-console --watch <replay.jsonl>
                  the same as run --follow.

            Keys: c next cell, f stop or resume following, q quit.
            """;

        public static int Run(string[] args, TextWriter output, TextWriter error, bool redirected)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                output.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }

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
                error.WriteLine("name a replay file (creatura.replay/1, as the convoy's --replay writes it)");
                return 2;
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
    }
}
