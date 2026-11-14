using System.Reflection;
using Forge.Abstractions;
using Forge.App.Composition;
using Forge.App.Preferences;
using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests.Composition;

/// <summary>
/// The wiring in <c>RuntimeSettingsComposition</c>: editing <see cref="AppPreferences.AgentsMaxParallelSubAgents"/> pushes the
/// cap into every live host through <c>AgentRuntimeManager.RefreshSubAgentLimits</c>. The composition has no provider in a
/// test, so no turn can run; the host is built through the runtime own build step instead, which is the state a session is
/// in after its first turn.
/// </summary>
public sealed class SubAgentCapRefreshCompositionTests
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    private static AgentHost LiveHost(Forge.App.Agents.SessionRuntime runtime)
    {
        var build = typeof(Forge.App.Agents.SessionRuntime).GetMethod("Build", Private);
        Assert.NotNull(build);
        var built = Assert.IsType<AgentHost>(build.Invoke(runtime, null));
        var field = typeof(Forge.App.Agents.SessionRuntime).GetField("host", Private);
        Assert.NotNull(field);
        field.SetValue(runtime, built);
        return built;
    }

    [Fact]
    public async Task Editing_the_cap_setting_reaches_a_live_host_at_once()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-subagent-cap-composition-");
        try
        {
            var composition = AppComposition.Create();
            try
            {
                await composition.Initialization;
                var session = new SessionInfo("cap-s1", Path.Combine(root, "workspace"), "t", DateTimeOffset.UnixEpoch);
                var host = LiveHost(composition.Runtimes.Get(session));
                Assert.Equal(composition.Preferences.AgentsMaxParallelSubAgents, host.MaxParallelSubAgents);

                composition.Preferences.AgentsMaxParallelSubAgents = 9;
                Assert.Equal(9, host.MaxParallelSubAgents);

                composition.Preferences.AgentsMaxParallelSubAgents = 2;
                Assert.Equal(2, host.MaxParallelSubAgents);

                // The preference clamps to the section range, and the host follows the clamped value.
                composition.Preferences.AgentsMaxParallelSubAgents = 1000;
                Assert.Equal(AppPreferences.MaxAgentsMaxParallelSubAgents, host.MaxParallelSubAgents);
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

    [Fact]
    public async Task Editing_the_cap_with_no_live_session_is_harmless()
    {
        var (root, previous) = TempDataRoot.Redirect("forge-subagent-cap-composition-");
        try
        {
            var composition = AppComposition.Create();
            try
            {
                await composition.Initialization;

                var failure = Record.Exception(() => composition.Preferences.AgentsMaxParallelSubAgents = 6);

                Assert.Null(failure);
                Assert.Equal(6, composition.Preferences.AgentsMaxParallelSubAgents);
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
}
