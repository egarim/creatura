# Creatura.Templates

`dotnet new` templates for [Creatura](https://github.com/egarim/creatura): one loop, a control layer between the model
and its tools, and worlds that can lie.

```bash
dotnet new install Creatura.Templates
dotnet new creatura-agent -n MyAgent      # the designs, then your own regulator, on the toy world; offline
dotnet new creatura-world -n MyWorld      # your own world: facts, tools, one that lies; offline
dotnet new creatura-sk -n MyKernel        # the control layer on Semantic Kernel; offline until a key is set
cd MyAgent && dotnet run
```

`creatura-sk` takes `--keyEnv` (the name of the variable with the key, default `OPENAI_API_KEY`), `--baseUrl` and
`--model`. The project holds the variable's name, never the key.

The `creatura` tool makes the same projects with `creatura new`. More in the
[README](https://github.com/egarim/creatura#readme).
