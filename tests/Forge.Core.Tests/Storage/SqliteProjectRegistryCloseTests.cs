using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

/// <summary>Soft close of a project (<c>projects.closed_at</c>) and its reopen through <see cref="SqliteProjectRegistry.AddAsync"/>.</summary>
public sealed class SqliteProjectRegistryCloseTests
{
    [Fact]
    public async Task Closed_project_leaves_the_list_and_re_adding_it_reopens_the_same_row_with_its_sessions()
    {
        using var directory = new TempDirectory();
        await using var projects = new SqliteProjectRegistry(directory.Path);
        await using var store = new SqliteSessionStore(directory.Path);
        ISessionCatalog catalog = store;
        var root = directory.Combine("repo");
        var project = await projects.AddAsync(root, "repo", ProjectKind.Repository, default);
        var other = await projects.AddAsync(directory.Combine("other"), "other", ProjectKind.Repository, default);
        var session = await catalog.CreateAsync(new SessionCreate(root, "t") { ProjectId = project.Id }, default);

        Assert.True(await projects.CloseAsync(project.Id, default));
        Assert.True(await projects.CloseAsync(project.Id, default)); // already closed: still exists
        Assert.Equal([other.Id], (await projects.ListAsync(default)).Select(static p => p.Id));
        Assert.NotNull(await store.GetSessionAsync(session.Id, default)); // closing never touches sessions

        var reopenPath = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
        var reopened = await projects.AddAsync(reopenPath, "renamed", ProjectKind.Repository, default);
        Assert.Equal(project.Id, reopened.Id);
        Assert.Equal("repo", reopened.Name);
        Assert.Contains(project.Id, (await projects.ListAsync(default)).Select(static p => p.Id));
        Assert.Equal([session.Id], (await catalog.QueryAsync(new SessionQuery { ProjectId = reopened.Id }, default)).Select(static s => s.Id));
    }

    [Fact]
    public async Task Closing_an_unknown_project_returns_false()
    {
        using var directory = new TempDirectory();
        await using var projects = new SqliteProjectRegistry(directory.Path);
        Assert.False(await projects.CloseAsync("missing", default));
        Assert.Empty(await projects.ListAsync(default));
    }
}
