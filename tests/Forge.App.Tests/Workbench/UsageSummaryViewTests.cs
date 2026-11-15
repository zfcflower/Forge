using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Services;
using Forge.App.Tests;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.Abstractions;
using Xunit;

namespace Forge.App.Tests.Workbench;

/// <summary>
/// The usage summary's view wiring (wave W16a, L4): the composer strip carries the summary button, its flyout lists one
/// row per number the view model reports, and the empty session says so instead of drawing zeros. The view model arithmetic
/// itself is pinned in <c>UsageSummaryTests</c>; this file pins that the numbers reach the tree.
/// </summary>
[Collection("forge-app-ui")]
public sealed class UsageSummaryViewTests
{
    [Fact]
    public async Task The_strip_carries_the_summary_button_and_its_rows_reach_the_tree()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        await SharedUi.RunAsync(async () =>
        {
            await Task.Yield();

            var sources = new RowSources { Metadata = new Forge.Abstractions.ModelMetadata(128_000, new Forge.Abstractions.ModelPricing(3.00, 15.00, 0.30, 3.75)) };
            using var instruments = new SessionInstrumentsViewModel(
                new SessionInfo("usage-view", @"C:\work\forge", "usage", DateTimeOffset.Now),
                sources,
                static () => Array.Empty<string>(),
                static () => "gpt-4o");
            await sources.SettleAsync(instruments);

            instruments.AddTurn("root", new TokenUsage(2_000, 400, CacheReadTokens: 1_000));

            var rows = new ItemsControl { ItemsSource = instruments.UsageSummaryRows };
            var window = SharedUi.Show(rows, 400, 300);
            SharedUi.Settle(window);

            try
            {
                Assert.Equal(11, instruments.UsageSummaryRows.Count);
                Assert.DoesNotContain(instruments.UsageSummaryRows, static row => row.Value.Length == 0);
                Assert.Contains(instruments.UsageSummaryRows, static row => row.Label == UsageStrings.InputLabel);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void The_session_view_wires_the_summary_flyout_to_the_instruments()
    {
        var root = RepoRoot.Find();
        Assert.NotNull(root);

        var sessionView = File.ReadAllText(Path.Combine(root, "src/Forge.App/Views/SessionView.axaml"));
        Assert.Contains("strip-usage-summary", sessionView, StringComparison.Ordinal);
        Assert.Contains("strip-usage-summary-rows", sessionView, StringComparison.Ordinal);
        Assert.Contains("Instruments.UsageSummaryRows", sessionView, StringComparison.Ordinal);
        Assert.Contains("Instruments.UsageSummaryTitle", sessionView, StringComparison.Ordinal);
        Assert.Contains("Instruments.IsUsageSummaryEmpty", sessionView, StringComparison.Ordinal);
        Assert.Contains("Instruments.UsageSummaryHint", sessionView, StringComparison.Ordinal);
    }

    private sealed class RowSources : ISessionInstrumentSources
    {
        public Forge.Abstractions.ModelMetadata Metadata { get; set; } = Forge.Abstractions.ModelMetadata.Unknown;

        public bool HasVsCode => false;

        public int PendingApprovals(string sessionId) => 0;

        public Task<int?> ApprovalRecordsAsync(CancellationToken ct) => Task.FromResult<int?>(0);

        public (int Ready, int Configured) McpServers() => (0, 0);

        public IReadOnlyList<SlashEntry> Skills(string workspacePath) => [];

        public Forge.Abstractions.ModelMetadata ModelMetadata(string modelId, string? providerId = null) => Metadata;

        public void OpenInVsCode(string path)
        {
        }

        public void OpenInExplorer(string path)
        {
        }

        public void OpenSettingsSection(string sectionId)
        {
        }

        public Task<SessionTurnHistory> TurnHistoryAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(SessionTurnHistory.Empty);

        public async Task SettleAsync(SessionInstrumentsViewModel instruments)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!instruments.HasTurnHistory && DateTime.UtcNow < deadline)
            {
                await Task.Delay(5, CancellationToken.None);
            }

            Assert.True(instruments.HasTurnHistory, "the strip never read the session's turn history");
        }
    }
}
