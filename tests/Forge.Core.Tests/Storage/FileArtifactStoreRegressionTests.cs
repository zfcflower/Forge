using Forge.Core.Storage;
using Xunit;

namespace Forge.Core.Tests.Storage;

public class FileArtifactStoreRegressionTests
{
    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("nested/session")]
    [InlineData("nested\\session")]
    [InlineData("C:\\absolute")]
    public async Task Put_rejects_session_ids_that_are_or_contain_path_segments(string sessionId)
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(sessionId, "blob", new byte[] { 1 }, default));

        Assert.False(File.Exists(Path.Combine(directory.Path, "escape")));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("a/b")]
    [InlineData("..")]
    public async Task Lookups_and_reads_reject_path_like_artifact_ids(string artifactId)
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.GetInfoAsync(artifactId, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadAsync(artifactId, 0, null, default));
    }

    [Theory]
    [InlineData("...")]
    [InlineData("....")]
    public async Task Put_rejects_all_dot_session_ids(string sessionId)
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(sessionId, "blob", new byte[] { 1 }, default));
    }

    [Fact]
    public async Task Artifact_ids_returned_by_put_are_accepted_by_lookup_and_read()
    {
        using var directory = new TempDirectory();
        await using var store = FileArtifactStore.ForDataDirectory(directory.Path);

        var info = await store.PutAsync("session1", "log", new byte[] { 7, 8, 9 }, default);

        Assert.Equal(info, await store.GetInfoAsync(info.Id, default));
        Assert.Equal(new byte[] { 8, 9 }, (await store.ReadAsync(info.Id, 1, null, default)).ToArray());
    }
}
