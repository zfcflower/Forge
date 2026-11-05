using System.Globalization;
using System.Xml.Linq;
using Avalonia.Headless;
using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Workspace;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// Pins five small rules that a refactor can silently drop: the running state of a sub-agent clears its waiting note, a
/// refresh requested during a project-tree refresh is coalesced instead of dropped, the finished menu opens the same entry
/// again when it is chosen twice, a closed streaming segment is never rewritten, and the primary button keeps WCAG AA
/// contrast in both themes.
/// </summary>
[Collection("forge-app-ui")]
public sealed class MinorRegressionTests
{
    private const int PumpIterations = 400;
    private const int PumpDelayMilliseconds = 5;
    private const string SessionId = "session";
    private const string AgentId = "agent";
    private const string ChildSessionId = "child";
    private const string AgentKind = "Explore";
    private const string AgentTask = "look at the diff";
    private const string Model = "model";
    private static readonly DateTimeOffset At = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>WCAG 2.x AA for normal text.</summary>
    private const double WcagAaMinimumContrastRatio = 4.5;

    private const string LineChunk = "a line of streamed reply text\n";

    /// <summary>Enough repeats to close many segments: the segment limit in the view model is far below this.</summary>
    private const int Repeats = 4000;

    // ---------------------------------------------------------------- sub-agent status

    [Fact]
    public void Running_a_sub_agent_clears_its_waiting_note()
    {
        var agent = NewAgent();
        Assert.True(agent.HasStatusNote, "a queued sub-agent starts with the waiting note");

        agent.MarkRunning(At);

        Assert.False(agent.HasStatusNote);
    }

    [Fact]
    public void A_running_sub_agent_shows_the_running_status_it_is_stated_as()
    {
        var agent = NewAgent();

        agent.MarkRunning(At);

        Assert.Equal(SessionStrings.SubAgentRunning, agent.StatusText);
        Assert.True(agent.IsLive);
    }

    // ---------------------------------------------------------------- project tree refresh

    [Fact]
    public async Task A_refresh_requested_during_a_refresh_is_merged_into_one_follow_up_refresh()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var passes = await session.Dispatch(
            async () =>
            {
                var registry = new EmptyRegistry();
                var catalog = new CountingCatalog();
                var tree = NewTree(registry, catalog);

                var running = tree.RefreshAsync(CancellationToken.None);
                var during = tree.RefreshAsync(CancellationToken.None);
                var merged = during.IsCompleted;
                await running;
                await during;
                var afterFirst = catalog.Queries;

                await tree.RefreshAsync(CancellationToken.None);
                return (Merged: merged, AfterFirst: afterFirst, AfterSecond: catalog.Queries);
            },
            CancellationToken.None);

        Assert.True(passes.Merged, "a refresh requested while one runs returns at once");
        Assert.Equal(2, passes.AfterFirst);
        Assert.Equal(3, passes.AfterSecond);
    }

    // ---------------------------------------------------------------- finished sub-agent menu

    [Fact]
    public async Task Choosing_the_same_finished_sub_agent_twice_opens_it_twice()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(App));
        var outcome = await session.Dispatch(
            async () =>
            {
                var agent = NewAgent();
                var menu = new FinishedSubAgentsViewModel();
                var opened = new List<SubAgentViewModel>();
                menu.Bind([agent], opened.Add);

                menu.Selected = agent;
                Assert.True(await PumpUntilAsync(() => menu.Selected is null), "the menu never reset its selection");
                menu.Selected = agent;

                return (Opened: opened, Agent: agent);
            },
            CancellationToken.None);

        Assert.Equal(2, outcome.Opened.Count);
        Assert.All(outcome.Opened, item => Assert.Same(outcome.Agent, item));
    }

    // ---------------------------------------------------------------- streaming text segments

    [Fact]
    public void A_closed_text_segment_is_never_rewritten_by_later_streaming()
    {
        var text = new TurnTextViewModel();
        for (var i = 0; i < Repeats; i++)
        {
            text.Append(LineChunk);
        }

        var closed = text.Segments.Take(text.Segments.Count - 1).ToList();
        Assert.NotEmpty(closed);
        var before = closed.Select(static segment => segment.Text).ToList();
        var rewrites = 0;
        foreach (var segment in closed)
        {
            segment.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(TextSegmentViewModel.Text))
                {
                    rewrites++;
                }
            };
        }

        for (var i = 0; i < Repeats; i++)
        {
            text.Append(LineChunk);
        }

        Assert.Equal(before, closed.Select(static segment => segment.Text));
        Assert.Equal(0, rewrites);
    }

    [Fact]
    public void A_closed_text_segment_keeps_its_place_in_the_segment_list()
    {
        var text = new TurnTextViewModel();
        for (var i = 0; i < Repeats; i++)
        {
            text.Append(LineChunk);
        }

        var snapshot = text.Segments.ToList();
        Assert.True(snapshot.Count > 1, "the streamed text must have closed at least one segment");

        text.Append(LineChunk);

        // Closed segments keep both their instance and their place; only the open one at the end may grow.
        for (var index = 0; index < snapshot.Count - 1; index++)
        {
            Assert.Same(snapshot[index], text.Segments[index]);
        }
    }

    // ---------------------------------------------------------------- theme contrast

    [Fact]
    public void Primary_button_text_keeps_wcag_aa_contrast_in_the_light_theme() =>
        AssertPrimaryButtonContrast("Light");

    [Fact]
    public void Primary_button_text_keeps_wcag_aa_contrast_in_the_dark_theme() =>
        AssertPrimaryButtonContrast("Dark");

    private static void AssertPrimaryButtonContrast(string theme)
    {
        var document = LoadTokens();
        // Primitive colours come first in the file and are the ones a brush refers to; the per-theme colours repeat keys.
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in document.Descendants().Where(static element => element.Name.LocalName == "Color"))
        {
            if (KeyOf(element) is { } key)
            {
                colors.TryAdd(key, element.Value.Trim());
            }
        }

        var dictionary = document.Descendants()
            .First(element => element.Name.LocalName == "ResourceDictionary" && string.Equals(KeyOf(element), theme, StringComparison.Ordinal));

        var background = ColorOf(dictionary, "PrimaryButtonBackgroundBrush", colors);
        var foreground = ColorOf(dictionary, "TextOnAccentBrush", colors);
        var ratio = ContrastRatio(background, foreground);

        Assert.True(
            ratio >= WcagAaMinimumContrastRatio,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0} primary button text contrast is {1:0.00}:1, below the WCAG AA minimum of {2:0.0}:1",
                theme,
                ratio,
                WcagAaMinimumContrastRatio));
    }

    /// <summary>The brush colour of one key inside one theme dictionary, following a <c>StaticResource</c> reference.</summary>
    private static (byte Red, byte Green, byte Blue) ColorOf(
        XElement dictionary,
        string brushKey,
        IReadOnlyDictionary<string, string> colors)
    {
        var brush = dictionary.Descendants().First(
            element => element.Name.LocalName == "SolidColorBrush" && string.Equals(KeyOf(element), brushKey, StringComparison.Ordinal));
        var value = brush.Attribute("Color")?.Value.Trim() ?? throw new InvalidOperationException(brushKey + " has no Color");
        if (value.StartsWith("{StaticResource ", StringComparison.Ordinal))
        {
            var key = value["{StaticResource ".Length..].TrimEnd('}').Trim();
            value = colors[key];
        }

        var hex = value.TrimStart('#');
        return (byte.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    /// <summary>WCAG 2.x relative luminance and contrast ratio.</summary>
    private static double ContrastRatio((byte Red, byte Green, byte Blue) first, (byte Red, byte Green, byte Blue) second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance((byte Red, byte Green, byte Blue) color) =>
        (0.2126 * Channel(color.Red)) + (0.7152 * Channel(color.Green)) + (0.0722 * Channel(color.Blue));

    private static double Channel(byte value)
    {
        var srgb = value / 255.0;
        return srgb <= 0.03928 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);
    }

    private static string? KeyOf(XElement element) =>
        element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value;

    /// <summary>The theme token file of the app, found from the test binaries upwards.</summary>
    private static XDocument LoadTokens()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Forge.App", "Resources", "Tokens.axaml");
            if (File.Exists(candidate))
            {
                return XDocument.Load(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("src/Forge.App/Resources/Tokens.axaml was not found above " + AppContext.BaseDirectory);
    }

    // ---------------------------------------------------------------- helpers

    private static SubAgentViewModel NewAgent() =>
        new(SessionId, AgentId, ChildSessionId, AgentKind, AgentTask, Model, stopRequested: null);

    private static ProjectTreeViewModel NewTree(IProjectRegistry registry, ISessionCatalog catalog) =>
        new(
            registry,
            catalog,
            new WorktreeService(new GitCli()),
            (_, _) => Task.CompletedTask,
            _ => Task.FromResult(true),
            (_, _) => Task.FromResult<string?>(null),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);

    private static async Task<bool> PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < PumpIterations; i++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(PumpDelayMilliseconds);
        }

        return condition();
    }

    private sealed class EmptyRegistry : IProjectRegistry
    {
        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ProjectInfo>>(
                [new ProjectInfo("general", "general", Path.GetTempPath(), ProjectKind.General, 0, At)]);

        public Task<ProjectInfo> AddAsync(string rootPath, string name, ProjectKind kind, CancellationToken ct) =>
            Task.FromResult(new ProjectInfo("general", name, rootPath, kind, 0, At));

        public Task<bool> RemoveAsync(string projectId, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> RenameAsync(string projectId, string name, CancellationToken ct) => Task.FromResult(false);

        public Task<bool> MoveAsync(string projectId, int sortOrder, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class CountingCatalog : ISessionCatalog
    {
        private readonly Lock gate = new();
        private int queries;

        public int Queries
        {
            get
            {
                lock (gate)
                {
                    return queries;
                }
            }
        }

        public Task<SessionInfo> CreateAsync(SessionCreate request, CancellationToken ct) =>
            Task.FromResult(new SessionInfo("created", request.WorkspacePath, request.Title, At));

        public Task<IReadOnlyList<SessionInfo>> QueryAsync(SessionQuery query, CancellationToken ct)
        {
            lock (gate)
            {
                queries++;
            }

            return Task.FromResult<IReadOnlyList<SessionInfo>>([]);
        }

        public Task<bool> SetStatusAsync(string sessionId, SessionStatus status, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> RenameAsync(string sessionId, string title, CancellationToken ct) => Task.FromResult(true);

        public Task TouchAsync(string sessionId, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
    }
}
