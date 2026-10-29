// `creatura cell hook` is called by a host tool's hooks: it must print nothing and exit 0 whatever happens, so it is
// routed before anything else can write to the console or throw.
if (args.Length >= 2 && args[0] == "cell" && args[1] == "hook") return Creatura.Cells.HookCommand.Run(args, Console.In);
return args.FirstOrDefault() switch
{
    "new" => Creatura.Cli.NewCommand.Run(args, Console.Out, Console.Error, Console.In, !Console.IsInputRedirected),
    "run" => await Creatura.Cli.RunCommand.Run(args, Console.Out, Console.Error),
    "doctor" => Creatura.Cli.DoctorCommand.Run(args, Console.Out),
    "--version" => PrintVersion(),
    null or "--help" or "-h" or "help" => Help(args.Length == 0),
    _ => Creatura.Cells.CellCommand.Run(args, Console.Out, Console.Error),
};

static int PrintVersion()
{
    Console.WriteLine(Creatura.Cli.NewCommand.ToolVersion());
    return 0;
}

static int Help(bool bare)
{
    (bare ? Console.Error : Console.Out).WriteLine("""
        usage: creatura <command> [options]

          new [agent|world|sk]         make a project from a template (creatura new --help)
          run --design <d> --world <w> try a design on a world with no code (creatura run --list)
          doctor                       what is set up and what is missing, with the fix
          cell <command>               the cell on this vessel: claims, the ledger, offers, hooks, sync (creatura cell --help)
          --version                    this tool's version, which is the packages' version it makes projects with
        """);
    return bare ? 2 : 0;
}
