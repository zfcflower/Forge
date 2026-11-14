using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Forge.Abstractions;
using Forge.App.Appearance;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests.Logging;
using Forge.App.ViewModels;
using Forge.App.Views;
using Forge.App.Workbench.Controls;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.Appearance;

/// <summary>
/// The reduce-motion switch end to end (forge-app spec "聊天时间轴(W13) §2.4"): the appearance section's checkbox writes the
/// preference, the applier pushes it into the one motion gate both the tree and the chat timeline read, and live rows react
/// at once — a running timeline node drops its pulse and picks it up again, and a running agent row of the project tree
/// re-reads the gate when the switch flips.
/// </summary>
[Collection("forge-app-ui")]
public sealed class ReduceMotionTests
{
    [Fact]
    public async Task The_settings_switch_writes_the_preference()
    {
        await SharedUi.RunAsync(async () =>
        {
            var preferences = new AppPreferences();
            var model = new AppearanceSettingsViewModel(preferences, new SystemFontCatalog(static () => []));
            try
            {
                await model.FamiliesLoaded;
                Assert.False(model.ReduceMotion);

                model.ReduceMotion = true;
                Assert.True(preferences.ReduceMotion);

                // An external edit is echoed back into the switch.
                preferences.ReduceMotion = false;
                Assert.False(model.ReduceMotion);
            }
            finally
            {
                model.Dispose();
            }
        });
    }

    [Fact]
    public void The_applier_sets_the_gate_from_the_loaded_preference_and_raises_changed_exactly_on_a_change()
    {
        var previous = TreeMotion.ReducedMotion;
        TreeMotion.ReducedMotion = false;
        var preferences = new AppPreferences { ReduceMotion = true };
        var changes = 0;
        void Count(object? sender, EventArgs e) => changes++;
        TreeMotion.Changed += Count;
        try
        {
            // The gate starts on the stored preference, over a stand-alone dictionary like the composition root's target.
            using var applier = new AppearanceApplier(preferences, new ResourceDictionary());
            Assert.True(TreeMotion.ReducedMotion);
            Assert.Equal(1, changes);

            preferences.ReduceMotion = true;
            Assert.Equal(1, changes);
            Assert.True(TreeMotion.ReducedMotion);

            preferences.ReduceMotion = false;
            Assert.Equal(2, changes);
            Assert.False(TreeMotion.ReducedMotion);
        }
        finally
        {
            TreeMotion.Changed -= Count;
            TreeMotion.ReducedMotion = previous;
        }
    }

    [Fact]
    public async Task The_appearance_section_exposes_the_reduce_motion_checkbox()
    {
        await SharedUi.RunAsync(async () =>
        {
            var preferences = new AppPreferences();
            var model = new AppearanceSettingsViewModel(preferences, new SystemFontCatalog(static () => []));
            var window = new Window { Content = new AppearanceSettingsView { DataContext = model } };
            try
            {
                await model.FamiliesLoaded;
                window.Show();
                SharedUi.Settle(window);

                var checkbox = window.GetVisualDescendants().OfType<CheckBox>().Single(control =>
                    AutomationProperties.GetAutomationId(control) == "appearance-reduce-motion");
                Assert.NotEqual(true, checkbox.IsChecked);

                preferences.ReduceMotion = true;
                SharedUi.Settle(window);
                Assert.True(checkbox.IsChecked, "the switch mirrors the preference");
            }
            finally
            {
                window.Close();
                model.Dispose();
            }
        });
    }

    [Fact]
    public async Task The_switch_stops_and_restores_the_running_nodes_pulse()
    {
        var previous = TreeMotion.ReducedMotion;
        TreeMotion.ReducedMotion = false;
        try
        {
            await SharedUi.RunAsync(() =>
            {
                var preferences = new AppPreferences();
                var applier = new AppearanceApplier(preferences, new ResourceDictionary());
                var gutter = new TimelineGutter
                {
                    Role = TimelineRowRole.Block,
                    Kind = TimelineNodeKind.Assistant,
                    IsRunning = true,
                };
                var window = new Window { Content = gutter };
                try
                {
                    window.Show();
                    SharedUi.Settle(window);
                    var icon = Icon(gutter);
                    Assert.Contains("running", icon.Classes);
                    Assert.Contains("pulsing", icon.Classes);

                    preferences.ReduceMotion = true;
                    Assert.True(TreeMotion.ReducedMotion, "the applier pushes the preference into the gate");
                    Assert.Contains("running", icon.Classes);
                    Assert.DoesNotContain("pulsing", icon.Classes);

                    preferences.ReduceMotion = false;
                    Assert.Contains("running", icon.Classes);
                    Assert.Contains("pulsing", icon.Classes);
                    return Task.CompletedTask;
                }
                finally
                {
                    window.Close();
                    applier.Dispose();
                }
            });
        }
        finally
        {
            TreeMotion.ReducedMotion = previous;
        }
    }

    [Fact]
    public async Task The_motion_gate_flips_a_running_agent_row_of_the_project_tree()
    {
        var previous = TreeMotion.ReducedMotion;
        TreeMotion.ReducedMotion = false;
        try
        {
            await SharedUi.RunAsync(() =>
            {
                var preferences = new AppPreferences();
                var applier = new AppearanceApplier(preferences, new ResourceDictionary());
                var tree = new ProjectTreeViewModel(
                    new EmptyRegistry(),
                    new EmptyCatalog(),
                    new WorktreeService(new GitCli()),
                    (_, _) => Task.FromResult(new GitHead(GitHeadKind.Branch, "main")),
                    (_, _) => Task.CompletedTask,
                    _ => Task.FromResult(true),
                    (_, _) => Task.FromResult<string?>(null),
                    (_, _) => Task.FromResult<NewBranchChoice?>(null),
                    _ => Task.CompletedTask,
                    _ => Task.CompletedTask);
                try
                {
                    var row = new TreeNodeViewModel(TreeNodeKind.SubAgent)
                    {
                        StatusText = SessionStrings.SubAgentRunning,
                    };
                    Assert.True(row.IsAgentRunning);
                    Assert.True(row.IsAgentRunningAnimated, "the pulse plays while the gate is off");

                    // The tree is the one that hears the switch: every running row re-reads the gate.
                    tree.Rows.Add(row);
                    preferences.ReduceMotion = true;

                    Assert.True(TreeMotion.ReducedMotion);
                    Assert.False(row.IsAgentRunningAnimated, "the row re-read the gate through the tree's subscription");

                    preferences.ReduceMotion = false;
                    Assert.True(row.IsAgentRunningAnimated, "the row picks the pulse up again");
                    return Task.CompletedTask;
                }
                finally
                {
                    tree.Dispose();
                    applier.Dispose();
                }
            });
        }
        finally
        {
            TreeMotion.ReducedMotion = previous;
        }
    }

    private static FluentIcon Icon(TimelineGutter gutter) =>
        gutter.GetVisualDescendants().OfType<FluentIcon>().Single();

    private sealed class EmptyRegistry : IProjectRegistry
    {
        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ProjectInfo>>([]);

        public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) =>
            Task.FromResult(new ProjectInfo("general", name, rootPath, kind, 0, DateTimeOffset.UtcNow));

        public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class EmptyCatalog : SessionCatalogDouble
    {
        public override Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, DateTimeOffset.UtcNow));

        public override Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>([]);
    }
}
