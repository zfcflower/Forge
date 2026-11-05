using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Pins SqliteProjectRegistry: path normalization, duplicate and singleton semantics,
/// kind conflicts, rename/move/remove and persistence across reopen.</summary>
public sealed class SqliteProjectRegistryTests
{
    [Fact]
    public async Task Add_normalizes_trailing_separator_and_relative_paths()
    {
        using var directory = new TempDirectory();
        await using var registry = new SqliteProjectRegistry(directory.Path);

        var target = directory.Combine("Repo");
        Directory.CreateDirectory(target);
        var withSeparator = await registry.AddAsync(target + Path.DirectorySeparatorChar, "Repo", ProjectKind.Repository, default);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)), withSeparator.RootPath);

        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(directory.Path);
        try
        {
            var relative = await registry.AddAsync("RelChild", "Rel", ProjectKind.Repository, default);
            Assert.True(Path.IsPathRooted(relative.RootPath));
            Assert.Equal(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath("RelChild")),
                relative.RootPath);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public async Task Duplicate_path_returns_the_same_entry()
    {
        using var directory = new TempDirectory();
        await using var registry = new SqliteProjectRegistry(directory.Path);

        var first = await registry.AddAsync(directory.Combine("a"), "One", ProjectKind.Repository, default);
        var second = await registry.AddAsync(directory.Combine("a"), "Two", ProjectKind.Repository, default);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("One", second.Name);
        Assert.Single(await registry.ListAsync(default));
    }

    [Fact]
    public async Task Case_sensitivity_follows_the_platform()
    {
        using var directory = new TempDirectory();
        await using var registry = new SqliteProjectRegistry(directory.Path);

        if (OperatingSystem.IsWindows())
        {
            var lower = directory.Combine("cased").ToLowerInvariant();
            var upper = directory.Combine("cased").ToUpperInvariant();
            var first = await registry.AddAsync(lower, "One", ProjectKind.Repository, default);
            var second = await registry.AddAsync(upper, "Two", ProjectKind.Repository, default);
            Assert.Equal(first.Id, second.Id);
        }
        else
        {
            var lower = await registry.AddAsync(directory.Combine("cased"), "One", ProjectKind.Repository, default);
            var upper = await registry.AddAsync(directory.Combine("CASED"), "Two", ProjectKind.Repository, default);
            Assert.NotEqual(lower.Id, upper.Id);
            Assert.Equal(2, (await registry.ListAsync(default)).Count);
        }
    }

    [Fact]
    public async Task General_is_a_singleton_and_repoint_keeps_identity()
    {
        using var directory = new TempDirectory();
        await using var registry = new SqliteProjectRegistry(directory.Path);

        var first = await registry.AddAsync(directory.Combine("gen-a"), "General", ProjectKind.General, default);
        var second = await registry.AddAsync(directory.Combine("gen-b"), "General moved", ProjectKind.General, default);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.AddedAt, second.AddedAt);
        Assert.Equal(first.SortOrder, second.SortOrder);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Combine("gen-b"))), second.RootPath);
        Assert.Equal("General moved", second.Name);
        Assert.Single(await registry.ListAsync(default), p => p.Kind == ProjectKind.General);
    }

    [Fact]
    public async Task Same_path_registered_as_another_kind_throws()
    {
        using var directory = new TempDirectory();
        await using var registry = new SqliteProjectRegistry(directory.Path);

        var path = directory.Combine("shared");
        await registry.AddAsync(path, "Repo", ProjectKind.Repository, default);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.AddAsync(path + Path.DirectorySeparatorChar, "General", ProjectKind.General, default));
        Assert.Contains("Repository", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_move_and_remove()
    {
        using var directory = new TempDirectory();
        await using var registry = new SqliteProjectRegistry(directory.Path);

        var project = await registry.AddAsync(directory.Combine("p"), "Old", ProjectKind.Repository, default);
        Assert.Equal(0, project.SortOrder);

        Assert.True(await registry.RenameAsync(project.Id, "New", default));
        Assert.Equal("New", (await registry.ListAsync(default)).Single(p => p.Id == project.Id).Name);
        Assert.False(await registry.RenameAsync(Guid.NewGuid().ToString("n"), "X", default));

        Assert.True(await registry.MoveAsync(project.Id, 7, default));
        Assert.Equal(7, (await registry.ListAsync(default)).Single(p => p.Id == project.Id).SortOrder);
        Assert.False(await registry.MoveAsync(Guid.NewGuid().ToString("n"), 1, default));

        Assert.True(await registry.RemoveAsync(project.Id, default));
        Assert.Empty(await registry.ListAsync(default));
        Assert.False(await registry.RemoveAsync(project.Id, default));
    }

    [Fact]
    public async Task Entries_persist_across_reopen()
    {
        using var directory = new TempDirectory();
        ProjectInfo added;
        await using (var registry = new SqliteProjectRegistry(directory.Path))
        {
            added = await registry.AddAsync(directory.Combine("keep"), "Keep", ProjectKind.Repository, default);
        }

        await using (var reopened = new SqliteProjectRegistry(directory.Path))
        {
            var loaded = Assert.Single(await reopened.ListAsync(default));
            Assert.Equal(added, loaded);
        }
    }
}
