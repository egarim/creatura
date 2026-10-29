# Creatura.Cli

The `creatura` command, as a .NET tool: start a Creatura project, try a design with no code, check your setup, and run this machine's cell (claims, the signed work ledger, offers, sync and the Claude Code hooks).

```sh
dotnet tool install -g Creatura.Cli --prerelease
```

```sh
creatura doctor                                 # what is set up and what is missing, with the fix
creatura new agent --name MyAgent               # a project from a template (agent, world or sk)
creatura run --design bach --world permit       # try a design on a world, no code (creatura run --list)

creatura cell init                              # create this vessel's cell, endorsed by the owner key
creatura cell claim --goal "restore the service"
creatura cell progress --row "patched; waiting for the health check"
creatura cell release --evidence "alert resolved"
creatura cell ledger --open                     # the claims not released yet
creatura cell stream                            # who is working on what, as a page on 127.0.0.1
creatura cell install-hooks --settings ~/.claude/settings.json   # each Claude Code session becomes a claim
creatura cell --help                            # every command
```

Every `cell` command takes `--home <dir>` (default `~/.creatura`, or `$CREATURA_HOME`) and `--store json|sqlite`. Private keys and the ledger are written owner-only.

Self-contained builds that need no .NET are attached to each [GitHub release](https://github.com/egarim/creatura/releases).

More: the [README](https://github.com/egarim/creatura#cells), "Cells".

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
