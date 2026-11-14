using Forge.App.Services;
using MermaidKinds = Mermaider.Models.DiagramTypes;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// Pins the render service's contract (wave W11): a valid source becomes a drawable picture, the same source + palette +
/// whitelist is one render shared by every caller (including four requests that arrive before it finished), a theme change
/// renders again, and a failure comes back as a result the block can show - never as a faulted task or a stale cache entry.
/// </summary>
public sealed class MermaidRenderServiceTests
{
    private const string Flow = "flowchart TD\n  A[Start] --> B{Done}\n";

    private static MermaidRenderRequest Request(
        string source,
        MermaidPalette? palette = null,
        MermaidKinds allowed = MermaidKinds.Flowchart,
        TimeSpan? timeout = null) =>
        new(source, palette ?? MermaidPalette.Fallback, allowed, timeout ?? TimeSpan.FromSeconds(10));

    [Fact]
    public async Task A_valid_diagram_renders_a_picture_with_a_natural_size()
    {
        var result = await new MermaidRenderService().RenderAsync(Request(Flow), CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Rendered, result.Outcome);
        var picture = Assert.IsType<MermaidPicture>(result.Picture);
        Assert.True(picture.Size.Width > 0);
        Assert.True(picture.Size.Height > 0);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task The_same_request_twice_reuses_one_picture()
    {
        var service = new MermaidRenderService();
        var request = Request(Flow);

        var first = await service.RenderAsync(request, CancellationToken.None);
        var second = await service.RenderAsync(request, CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Rendered, first.Outcome);
        Assert.Equal(MermaidRenderOutcome.Rendered, second.Outcome);
        Assert.Same(first.Picture, second.Picture);
    }

    [Fact]
    public async Task A_different_palette_renders_the_same_source_again()
    {
        var service = new MermaidRenderService();
        var light = await service.RenderAsync(Request(Flow), CancellationToken.None);
        var dark = await service.RenderAsync(
            Request(Flow, MermaidPalette.Fallback with { Background = "#000000", Foreground = "#FFFFFF" }),
            CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Rendered, light.Outcome);
        Assert.Equal(MermaidRenderOutcome.Rendered, dark.Outcome);
        Assert.NotSame(light.Picture, dark.Picture);
    }

    [Fact]
    public async Task ClearCache_makes_the_next_request_render_again()
    {
        var service = new MermaidRenderService();
        var request = Request(Flow);
        var before = await service.RenderAsync(request, CancellationToken.None);

        service.ClearCache();
        var after = await service.RenderAsync(request, CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Rendered, before.Outcome);
        Assert.Equal(MermaidRenderOutcome.Rendered, after.Outcome);
        Assert.NotSame(before.Picture, after.Picture);
    }

    [Fact]
    public async Task Four_requests_that_arrive_together_share_one_render()
    {
        var service = new MermaidRenderService();
        var request = Request(Flow);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ => Task.Run(() => service.RenderAsync(request, CancellationToken.None))));

        Assert.All(results, static result => Assert.Equal(MermaidRenderOutcome.Rendered, result.Outcome));
        Assert.All(results, result => Assert.Same(results[0].Picture, result.Picture));
    }

    [Fact]
    public async Task A_diagram_type_outside_the_whitelist_is_disabled_not_an_error()
    {
        var result = await new MermaidRenderService().RenderAsync(Request(Flow, allowed: 0), CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Failed, result.Outcome);
        Assert.Equal(MermaidRenderFailure.Disabled, result.Failure);
        Assert.Null(result.Picture);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task A_source_without_a_mermaid_header_fails_on_line_one()
    {
        var result = await new MermaidRenderService()
            .RenderAsync(Request("this is not mermaid at all"), CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Failed, result.Outcome);
        Assert.Equal(MermaidRenderFailure.Parse, result.Failure);
        Assert.Equal(1, result.ErrorLine);
        Assert.Contains("Invalid mermaid header", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_is_not_cached_so_the_retry_tries_again()
    {
        var service = new MermaidRenderService();
        var request = Request("this is not mermaid at all");

        var first = await service.RenderAsync(request, CancellationToken.None);
        var second = await service.RenderAsync(request, CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Failed, second.Outcome);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task A_source_longer_than_the_limit_is_rejected_without_rendering()
    {
        var result = await new MermaidRenderService().RenderAsync(
            Request(new string('x', MermaidRenderService.MaxSourceLength + 1)), CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Failed, result.Outcome);
        Assert.Equal(MermaidRenderFailure.TooLarge, result.Failure);
        Assert.Null(result.Picture);
    }

    [Fact]
    public async Task The_cache_holds_at_most_the_configured_number_of_pictures()
    {
        var service = new MermaidRenderService();
        var first = await service.RenderAsync(Request(Flow), CancellationToken.None);
        Assert.Equal(MermaidRenderOutcome.Rendered, first.Outcome);

        // Fill the cache with distinct sources; the first one is then evicted by insertion order.
        for (var i = 0; i < MermaidRenderService.MaxCacheEntries; i++)
        {
            var filled = await service.RenderAsync(
                Request($"flowchart TD\n  N{i}-->N{i + 1}"), CancellationToken.None);
            Assert.Equal(MermaidRenderOutcome.Rendered, filled.Outcome);
        }

        var evicted = await service.RenderAsync(Request(Flow), CancellationToken.None);
        Assert.Equal(MermaidRenderOutcome.Rendered, evicted.Outcome);
        Assert.NotSame(first.Picture, evicted.Picture);

        // The newest entry is still cached, so the bound is a cache and not a "clear on every miss".
        var newest = await service.RenderAsync(
            Request($"flowchart TD\n  N{MermaidRenderService.MaxCacheEntries - 1}-->N{MermaidRenderService.MaxCacheEntries}"),
            CancellationToken.None);
        Assert.Equal(MermaidRenderOutcome.Rendered, newest.Outcome);
        var newestAgain = await service.RenderAsync(
            Request($"flowchart TD\n  N{MermaidRenderService.MaxCacheEntries - 1}-->N{MermaidRenderService.MaxCacheEntries}"),
            CancellationToken.None);
        Assert.Same(newest.Picture, newestAgain.Picture);
    }

    [Theory]
    [InlineData("sequenceDiagram\n  Alice->>Bob: hello\n  Bob-->>Alice: hi")]
    [InlineData("stateDiagram-v2\n  [*] --> Idle\n  Idle --> Busy: start")]
    [InlineData("classDiagram\n  class Animal {\n    +int age\n  }\n  Animal <|-- Dog")]
    [InlineData("erDiagram\n  CUSTOMER ||--o{ ORDER : places")]
    public async Task Every_other_default_type_renders_a_picture(string source)
    {
        // The default whitelist is five types, and their SVGs tint differently: a mermaider output change in one of them must
        // fail here instead of turning that type black or into a permanent failure in the app.
        var service = new MermaidRenderService();
        var allowed = MermaidKinds.Flowchart | MermaidKinds.Sequence | MermaidKinds.State | MermaidKinds.Class | MermaidKinds.Er;

        var result = await service.RenderAsync(Request(source, allowed: allowed), CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Rendered, result.Outcome);
        Assert.NotNull(result.Picture);
        Assert.True(result.Picture!.Size.Width > 0);
        Assert.True(result.Picture.Size.Height > 0);
    }

    [Fact]
    public async Task A_diagram_past_mermaid_s_own_line_limit_is_reported_as_too_large()
    {
        // 10,050 one-character lines stay under the service's own 64 KiB bound; mermaider's MaxLines is what rejects it.
        var source = "flowchart TD\n" + string.Join('\n', Enumerable.Repeat("a", 10_050));

        var result = await new MermaidRenderService().RenderAsync(Request(source), CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Failed, result.Outcome);
        Assert.Equal(MermaidRenderFailure.TooLarge, result.Failure);
    }

    [Fact]
    public async Task A_render_that_exceeds_the_timeout_is_reported_as_timed_out()
    {
        // A 200-node flowchart takes tens of milliseconds to parse and lay out, so a 1 ms bound is the wall-clock guard by a
        // wide margin; a tiny diagram would render before the timer on a warm process and make this test a race.
        var source = "flowchart TD\n" + string.Join(
            '\n',
            Enumerable.Range(0, 200).Select(static i => $"  N{i}[node {i}] --> N{i + 1}[node {i + 1}]"));

        var result = await new MermaidRenderService().RenderAsync(
            Request(source, timeout: TimeSpan.FromMilliseconds(1)), CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Failed, result.Outcome);
        Assert.Equal(MermaidRenderFailure.Timeout, result.Failure);
        Assert.Null(result.Picture);
    }

    [Theory]
    [InlineData("unexpected token at line 12", 12)]
    [InlineData("Parse error at 3:4", 3)]
    [InlineData("Invalid mermaid header: \"whatever\"", 1)]
    [InlineData("Resource limit exceeded", null)]
    [InlineData("unexpected token at line 0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Error_line_is_read_from_the_message_s_own_number(string? message, int? expected) =>
        Assert.Equal(expected, MermaidRenderService.ErrorLine(message));

    private const string SvgOpen = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"40\" height=\"20\" viewBox=\"0 0 40 20\">";

    [Theory]
    [InlineData("<image href=\"http://evil.test/x.png\" width=\"4\" height=\"4\"/>")]
    [InlineData("<image xlink:href=\"https://evil.test/x.png\" width=\"4\" height=\"4\"/>")]
    [InlineData("<image href='file:///C:/secret.png' width='4' height='4'/>")]
    [InlineData("<image href=\"data:image/png;base64,AAAA\" width=\"4\" height=\"4\"/>")]
    [InlineData("<rect width=\"4\" height=\"4\" fill=\"url(http://evil.test/p.svg#a)\"/>")]
    [InlineData("<rect width=\"4\" height=\"4\" style=\"fill:url( 'http://evil.test/p.svg#a')\"/>")]
    [InlineData("<style>@import url(\"http://evil.test/a.css\");</style><rect width=\"4\" height=\"4\"/>")]
    public void An_svg_that_references_anything_outside_itself_is_refused_before_it_reaches_the_rasterizer(string body)
    {
        var result = MermaidRenderService.FromSvg(SvgOpen + body + "</svg>", MermaidPalette.Fallback);

        Assert.Equal(MermaidRenderOutcome.Failed, result.Outcome);
        Assert.Equal(MermaidRenderFailure.Internal, result.Failure);
        Assert.Null(result.Picture);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void References_to_an_element_of_the_same_svg_are_allowed()
    {
        const string body =
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#ff0000\"/></linearGradient>"
            + "<rect id=\"r\" width=\"4\" height=\"4\"/></defs>"
            + "<use href=\"#r\" fill=\"url(#g)\"/><use xlink:href=\"#r\" fill=\"url('#g')\"/>";
        var svg = SvgOpen + body + "</svg>";

        var result = MermaidRenderService.FromSvg(svg, MermaidPalette.Fallback);

        Assert.False(MermaidRenderService.HasExternalReference(svg));
        Assert.Equal(MermaidRenderOutcome.Rendered, result.Outcome);
    }

    [Fact]
    public async Task Mermaid_sources_that_try_to_pull_in_a_link_never_fetch_anything()
    {
        // Whatever Mermaider's sanitizer does with a click link, the outcome is a failure or a picture; the picture's SVG
        // passed the same external-reference check, so the network is not involved either way.
        const string source =
            "flowchart TD\n  A[Start] --> B[Done]\n  click A \"https://evil.test/a\"\n  click B href \"http://evil.test/b\"\n";

        var result = await new MermaidRenderService().RenderAsync(Request(source), CancellationToken.None);

        Assert.True(result.Outcome is MermaidRenderOutcome.Rendered or MermaidRenderOutcome.Failed);
        if (result.Outcome == MermaidRenderOutcome.Failed)
        {
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public async Task A_type_mermaider_rejects_as_not_allowed_is_disabled_not_a_parse_error()
    {
        // "sankey" (no -beta) is a header Mermaider takes; with only flowcharts allowed it answers "not in the allowed set".
        var result = await new MermaidRenderService().RenderAsync(Request("sankey\n  A,B,1\n"), CancellationToken.None);

        Assert.Equal(MermaidRenderOutcome.Failed, result.Outcome);
        Assert.Equal(MermaidRenderFailure.Disabled, result.Failure);
    }

    [Fact]
    public async Task A_header_mermaider_does_not_know_stays_a_parse_error_on_line_one()
    {
        var result = await new MermaidRenderService().RenderAsync(Request("flowchart-elk TD\n  A --> B\n"), CancellationToken.None);

        Assert.Equal(MermaidRenderFailure.Parse, result.Failure);
        Assert.Equal(1, result.ErrorLine);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_stops_the_wait_without_a_result()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var service = new MermaidRenderService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenderAsync(Request(Flow), cancelled.Token));

        // The shared render was not cancelled with the waiter: the next caller still gets the picture.
        var next = await service.RenderAsync(Request(Flow), CancellationToken.None);
        Assert.Equal(MermaidRenderOutcome.Rendered, next.Outcome);
    }

    [Fact]
    public async Task A_waiter_that_is_cancelled_mid_render_leaves_the_render_for_the_next_caller()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var runs = 0;
        var service = new MermaidRenderService
        {
            Renderer = request =>
            {
                Interlocked.Increment(ref runs);
                started.TrySetResult();
                release.Wait();
                return MermaidRenderService.RenderCore(request);
            },
        };
        using var cancellation = new CancellationTokenSource();

        var first = service.RenderAsync(Request(Flow), cancellation.Token);
        await started.Task;
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var second = service.RenderAsync(Request(Flow), CancellationToken.None);
        release.Set();

        Assert.Equal(MermaidRenderOutcome.Rendered, (await second).Outcome);
        Assert.Equal(1, runs);
    }
}
