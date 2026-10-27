// `creatura cell hook` is called by a host tool's hooks: it must print nothing and exit 0 whatever happens, so it is
// routed before anything else can write to the console or throw.
if (args.Length >= 2 && args[0] == "cell" && args[1] == "hook") return Creatura.Cells.HookCommand.Run(args, Console.In);
return Creatura.Cells.CellCommand.Run(args, Console.Out, Console.Error);
