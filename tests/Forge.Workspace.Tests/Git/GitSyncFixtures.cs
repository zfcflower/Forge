namespace Forge.Workspace.Tests.Git;

/// <summary>
/// The two-clone arrangement the pull/push and branch-status tests start from: <c>alice</c> tracks <c>origin/main</c>
/// and <c>bob</c> has pushed one commit that alice has not fetched yet, so the two branches differ by exactly one
/// commit until the test fetches, pulls or rebases.
/// </summary>
internal static class RemoteSandboxSync
{
    /// <summary>Seeds <c>alice</c> tracking <c>origin/main</c>, then has <c>bob</c> commit
    /// <paramref name="file"/>/<paramref name="content"/> as <paramref name="message"/> and push it to <c>main</c>;
    /// returns both clones and the tip bob pushed.</summary>
    public static (string Alice, string Bob, string Tip) SeedRemoteAhead(
        this RemoteSandbox sandbox,
        string file = "remote.txt",
        string content = "remote\n",
        string message = "remote work")
    {
        var alice = sandbox.SeedTracked("alice");
        var bob = sandbox.Clone("bob");
        var tip = sandbox.CommitFile(bob, file, content, message);
        IsolatedGit.RunChecked(bob, "push", "-q", "origin", "main");
        return (alice, bob, tip);
    }
}
