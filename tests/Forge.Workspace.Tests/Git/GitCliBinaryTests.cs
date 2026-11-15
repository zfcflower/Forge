using System.Diagnostics;
using System.Text;
using Forge.Workspace;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// <see cref="GitCli.GetFileBytesAtRevisionAsync"/> (wave W17a's image diff): a bounded read that returns the bytes,
/// reports oversize as null <em>promptly</em> — the writer must be stopped, or git blocks on a full pipe and the call waits
/// out the two-minute default timeout — and answers null for a missing object.
/// </summary>
public sealed class GitCliBinaryTests : IDisposable
{
    private readonly RemoteSandbox sandbox = new();
    private readonly GitCli git = new();

    public void Dispose() => sandbox.Dispose();

    [Fact]
    public async Task A_blob_inside_the_limit_comes_back_byte_for_byte()
    {
        var repo = sandbox.SeedTracked();
        var bytes = Enumerable.Range(0, 512).Select(static i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(repo, "blob.bin"), bytes);
        IsolatedGit.RunChecked(repo, "add", "-A");
        IsolatedGit.RunChecked(repo, "commit", "-q", "-m", "blob");

        var read = await git.GetFileBytesAtRevisionAsync(repo, "HEAD", "blob.bin", maxBytes: 4096, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(bytes, read);
    }

    [Fact]
    public async Task A_blob_over_the_limit_answers_null_without_waiting_for_the_default_timeout()
    {
        var repo = sandbox.SeedTracked();
        var bytes = new byte[512 * 1024];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(repo, "big.bin"), bytes);
        IsolatedGit.RunChecked(repo, "add", "-A");
        IsolatedGit.RunChecked(repo, "commit", "-q", "-m", "big");

        var started = Stopwatch.StartNew();
        var read = await git.GetFileBytesAtRevisionAsync(repo, "HEAD", "big.bin", maxBytes: 1024, CancellationToken.None);
        started.Stop();

        Assert.Null(read);
        // The default timeout is two minutes; an unread pipe would park here until then.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"the oversize read took {started.Elapsed}");
    }

    [Fact]
    public async Task A_missing_object_answers_null()
    {
        var repo = sandbox.SeedTracked();

        var read = await git.GetFileBytesAtRevisionAsync(repo, "HEAD", "never-there.bin", maxBytes: 4096, CancellationToken.None);

        Assert.Null(read);
    }
}