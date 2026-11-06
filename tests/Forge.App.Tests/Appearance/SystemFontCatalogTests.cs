using Forge.App.Appearance;
using Xunit;

namespace Forge.App.Tests.Appearance;

/// <summary>
/// Pins the catalog contract of TASK-fonts: one enumeration however often and however concurrently it is asked, distinct
/// names sorted ordinal-ignore-case, and a failing source that yields an empty list instead of an exception.
/// </summary>
public sealed class SystemFontCatalogTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_source_is_enumerated_once_and_the_same_task_is_reused()
    {
        var calls = 0;
        var catalog = new SystemFontCatalog(() =>
        {
            Interlocked.Increment(ref calls);
            return ["Beta", "Alpha"];
        });

        var first = catalog.GetFamiliesAsync();
        var second = catalog.GetFamiliesAsync();
        var listFirst = await first.WaitAsync(Budget);
        var listSecond = await catalog.GetFamiliesAsync().WaitAsync(Budget);

        Assert.Same(first, second);
        Assert.Same(listFirst, listSecond);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Concurrent_first_requests_still_enumerate_once()
    {
        var calls = 0;
        var catalog = new SystemFontCatalog(() =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(50);
            return ["Alpha"];
        });

        var starters = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(() => catalog.GetFamiliesAsync()))
            .ToArray();
        var lists = await Task.WhenAll(starters).WaitAsync(Budget);

        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(lists, list => Assert.Same(lists[0], list));
    }

    [Fact]
    public void Creating_the_catalog_does_not_enumerate()
    {
        var calls = 0;

        _ = new SystemFontCatalog(() =>
        {
            calls++;
            return [];
        });

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Names_are_distinct_trimmed_and_sorted_ignoring_case()
    {
        var catalog = new SystemFontCatalog(() => ["zeta", "Alpha", "alpha", "  Beta ", "BETA", "", "   ", "gamma", "Zeta"]);

        var families = await catalog.GetFamiliesAsync().WaitAsync(Budget);

        Assert.Equal(4, families.Count);
        Assert.Equal(["Alpha", "Beta", "gamma", "zeta"], families, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(families.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase), families);
        Assert.All(families, name => Assert.Equal(name.Trim(), name));
    }

    [Fact]
    public async Task Null_entries_are_skipped()
    {
        var catalog = new SystemFontCatalog(() => new string?[] { "B", null, "A" }!);

        var families = await catalog.GetFamiliesAsync().WaitAsync(Budget);

        Assert.Equal(["A", "B"], families);
    }

    [Fact]
    public async Task An_empty_source_yields_an_empty_list()
    {
        var catalog = new SystemFontCatalog(static () => []);

        var families = await catalog.GetFamiliesAsync().WaitAsync(Budget);

        Assert.Empty(families);
    }

    [Fact]
    public async Task A_source_that_throws_yields_an_empty_list_and_is_not_retried()
    {
        var calls = 0;
        var catalog = new SystemFontCatalog(() =>
        {
            calls++;
            throw new InvalidOperationException("font backend down");
        });

        var families = await catalog.GetFamiliesAsync().WaitAsync(Budget);
        var again = await catalog.GetFamiliesAsync().WaitAsync(Budget);

        Assert.Empty(families);
        Assert.Same(families, again);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_source_that_fails_midway_yields_an_empty_list_not_a_partial_one()
    {
        var catalog = new SystemFontCatalog(Failing);

        var families = await catalog.GetFamiliesAsync().WaitAsync(Budget);

        Assert.Empty(families);

        static IEnumerable<string> Failing()
        {
            yield return "Alpha";
            throw new IOException("enumeration broke");
        }
    }

    [Fact]
    public void A_null_source_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new SystemFontCatalog(null!));
    }
}
