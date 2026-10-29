using Creatura;

namespace Creatura.Hosts.SemanticKernel.Tests;

/// <summary>
/// The Semantic Kernel host against the Agent Framework host's four goldens: <see cref="ControlledKernelAgent"/> run
/// offline (the planner in the model's seat) over the toy suite's four barriers. Tick = model request on both hosts, so
/// the rows agree without translation. Two strings in each file are the host describing itself, not the layer (the
/// agent's name and the first note's framework); with those two swapped for the Agent Framework host's, the run is that
/// host's golden byte for byte. The goldens are read here as files, from that host's test folder; nothing here references
/// its package.
/// </summary>
public class ControlledKernelGoldenTests
{
    public static TheoryData<string> Barriers => ["clean", "patchrevoked", "patchlies", "deployrevoked"];

    internal static World Toy(string barrier)
    {
        var world = RestoreServiceDomain.NewWorld();
        switch (barrier)
        {
            case "clean": break;
            case "patchrevoked": world.Revoke("apply-patch"); break;
            case "patchlies": world.MakeLiar("apply-patch"); break;
            case "deployrevoked": world.Revoke("deploy"); break;
            default: throw new ArgumentException(barrier);
        }

        return world;
    }

    [Theory]
    [MemberData(nameof(Barriers))]
    public async Task TheKernelHostRunsTheAgentFrameworkHostsGoldenByteForByte(string barrier)
    {
        var path = Path.Combine(AgentFrameworkGoldens, $"restore-{barrier}-controlled.jsonl");
        Assert.True(File.Exists(path), $"{path} is missing: the Agent Framework host's goldens live in tests/Creatura.Hosts.AgentFramework.Tests/golden");
        var golden = AgentRun.ReadJsonl(path);

        var run = await ControlledKernelAgent.Offline().RunAsync(Toy(barrier), 30);

        // The host's self-description, and only that, is its own.
        Assert.Equal("Controlled (SK)", run.Agent);
        Assert.Equal("policy bfs; framework Microsoft.SemanticKernel", run.Notes[0]);
        Assert.Equal("policy bfs; framework Microsoft.Agents.AI", golden.Notes[0]);

        var asAgentFramework = run with { Agent = golden.Agent, Notes = [golden.Notes[0], .. run.Notes.Skip(1)] };
        var written = Path.Combine(Path.GetTempPath(), $"creatura-skhost-{Guid.NewGuid():N}.jsonl");
        try
        {
            asAgentFramework.WriteJsonl(written);
            Assert.Equal(File.ReadAllLines(path), File.ReadAllLines(written));
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(written));
        }
        finally
        {
            File.Delete(written);
        }
    }

    /// <summary>tests/Creatura.Hosts.AgentFramework.Tests/golden, found from the solution file.</summary>
    private static string AgentFrameworkGoldens { get; } = Locate();

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Creatura.slnx"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("Creatura.slnx not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "tests", "Creatura.Hosts.AgentFramework.Tests", "golden");
    }
}

/// <summary>The two hosts stay apart: this one brings Semantic Kernel and nothing of the Agent Framework.</summary>
public class HostSeparationTests
{
    [Fact]
    public void TheKernelHostReferencesSemanticKernelAndNoAgentFramework()
    {
        var references = typeof(ControlledKernelAgent).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

        Assert.Contains(references, r => r.StartsWith("Microsoft.SemanticKernel", StringComparison.Ordinal));
        Assert.DoesNotContain(references, r => r.StartsWith("Microsoft.Agents.", StringComparison.Ordinal));
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name!.StartsWith("Microsoft.Agents.", StringComparison.Ordinal));
    }
}
