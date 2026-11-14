using System.Reflection;
using Forge.App.Agents;
using Forge.App.Composition;
using Xunit;

namespace Forge.App.Tests.Agents;

/// <summary>
/// Pins the composition root's half of the sub-agent dials: the <c>agents</c> section writes a preference, the section is
/// registered by the wave's own module, and the runtime manager is handed a factory that reads the live preference rather
/// than a snapshot taken at startup. Without this the two dials can silently stop reaching the runtime while every unit test
/// of the mapping, the section and the runtime still passes on its own.
/// </summary>
public sealed class SubAgentLimitsWiringTests
{
    [Fact]
    public async Task The_runtime_is_handed_the_dials_of_the_live_preferences()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-sub-agent-wiring-");
        try
        {
            var composition = AppComposition.Create();
            try
            {
                await composition.Initialization;

                var services = RuntimeServices(composition);
                Assert.NotNull(services.SubAgents);

                var before = services.SubAgents!();
                Assert.Equal(composition.Preferences.AgentsMaxParallelSubAgents, before.MaxParallel);
                Assert.Equal(composition.Preferences.AgentsSubAgentsEnabled, before.Enabled);

                // A value written after the graph was built is read by the next call: the factory must not have captured a
                // snapshot, which is exactly what a running session relies on between two turns.
                composition.Preferences.AgentsMaxParallelSubAgents = 2;
                composition.Preferences.AgentsSubAgentsEnabled = false;

                Assert.Equal(new SubAgentLimits(2, Enabled: false), services.SubAgents!());
            }
            finally
            {
                await composition.DisposeAsync();
            }
        }
        finally
        {
            previous.Restore();
            TestTempFiles.DeleteTree(root);
        }
    }

    /// <summary>
    /// The services the composition handed to the runtime manager. They are private to it, and this is the one reflection hop
    /// in the file: the factory they carry is the thing under test (the same pattern as the step-committer wiring test).
    /// </summary>
    private static AgentRuntimeServices RuntimeServices(AppComposition composition)
    {
        var field = typeof(AgentRuntimeManager).GetField("services", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AgentRuntimeManager.services is gone; update this test.");
        return (AgentRuntimeServices)(field.GetValue(composition.Runtimes)
            ?? throw new InvalidOperationException("the runtime manager holds no services"));
    }
}
