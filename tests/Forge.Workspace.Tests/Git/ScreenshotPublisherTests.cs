using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>
/// <see cref="ScreenshotPublisher"/> against a real local bare remote that is addressed like a GitHub repository: the images
/// become a parentless commit on <c>refs/heads/assets/&lt;slug&gt;</c>, never touch the session branch, and the pull request
/// description gets a <c>## Screenshots</c> table pinned to that commit.
/// </summary>
public sealed class ScreenshotPublisherTests
{
    private const string AssetsBranch = "assets/forge-feature";
    private const string AssetsRef = "refs/heads/" + AssetsBranch;

    private static ScreenshotPublisher Publisher(ScreenshotFixture fixture) =>
        new(fixture.Git, fixture.Platform, new GitToolSettings());

    private static async Task<IReadOnlyList<ScreenshotPair>> CapturedPairsAsync(ScreenshotFixture fixture)
    {
        var capture = await fixture.CaptureAsync(ScreenshotTestData.CaptureCommand);
        Assert.True(capture.Success, capture.Message);
        return capture.Pairs;
    }

    private static ScreenshotPair Pair(ScreenshotFixture fixture, string name, string? beforeTag, string? afterTag)
    {
        string? Make(string? tag, string side)
        {
            if (tag is null)
            {
                return null;
            }

            var path = Path.Combine(fixture.Markers, $"{name}.{side}.{tag}.png");
            File.WriteAllBytes(path, ScreenshotTestData.Png(tag));
            return path;
        }

        return new ScreenshotPair(name, Make(beforeTag, "before"), Make(afterTag, "after"));
    }

    private static void Reach(ScreenshotFixture fixture, string address)
    {
        IsolatedGit.RunChecked(fixture.Work, "config", "--local", "remote.origin.url", address);
        IsolatedGit.RunChecked(
            fixture.Work,
            "config",
            "--local",
            "--add",
            "url." + fixture.Origin.Replace('\\', '/') + ".insteadOf",
            address);
    }

    private static string[] TreeNames(string repository, string commit) =>
        IsolatedGit.RunChecked(repository, "ls-tree", "--name-only", commit)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public async Task Publish_pushes_a_parentless_commit_that_holds_exactly_the_images()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        var pairs = await CapturedPairsAsync(fixture);

        var result = await Publisher(fixture).PublishAsync(fixture.Work, pairs, "origin/main", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(ScreenshotPublishStage.Done, result.Stage);
        Assert.Equal(AssetsRef, result.AssetsRef);
        var commit = Assert.IsType<string>(result.AssetsCommit);
        Assert.Equal(commit, fixture.OriginTip(AssetsBranch));

        var raw = IsolatedGit.RunChecked(fixture.Origin, "cat-file", "-p", commit);
        Assert.DoesNotContain("\nparent ", "\n" + raw, StringComparison.Ordinal);
        Assert.Equal("Screenshots for forge/feature", IsolatedGit.RunChecked(fixture.Origin, "log", "-1", "--format=%s", commit).Trim());
        Assert.Contains("- home", IsolatedGit.RunChecked(fixture.Origin, "log", "-1", "--format=%b", commit), StringComparison.Ordinal);
        Assert.Equal(
            ["extra-after.png", "home-after.png", "home-before.png", "removed-before.png"],
            TreeNames(fixture.Origin, commit));
        Assert.Equal("1", IsolatedGit.RunChecked(fixture.Origin, "rev-list", "--count", commit).Trim());

        foreach (var pair in pairs)
        {
            if (pair.BeforePath is { } before)
            {
                Assert.Equal(
                    IsolatedGit.RunChecked(fixture.Work, "hash-object", "--no-filters", "--", before).Trim(),
                    IsolatedGit.RunChecked(fixture.Origin, "rev-parse", $"{commit}:{pair.BeforeFileName}").Trim());
            }

            if (pair.AfterPath is { } after)
            {
                Assert.Equal(
                    IsolatedGit.RunChecked(fixture.Work, "hash-object", "--no-filters", "--", after).Trim(),
                    IsolatedGit.RunChecked(fixture.Origin, "rev-parse", $"{commit}:{pair.AfterFileName}").Trim());
            }
        }
    }

    [Fact]
    public async Task Publish_leaves_the_session_branch_and_its_worktree_alone()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        var pairs = await CapturedPairsAsync(fixture);
        var head = IsolatedGit.Head(fixture.Work);
        var branches = IsolatedGit.BranchList(fixture.Work);
        File.WriteAllText(Path.Combine(fixture.Work, "scratch.txt"), "untracked work");
        var status = fixture.Status();

        var result = await Publisher(fixture).PublishAsync(fixture.Work, pairs, "origin/main", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(head, IsolatedGit.Head(fixture.Work));
        Assert.Equal(branches, IsolatedGit.BranchList(fixture.Work));
        Assert.Equal(status, fixture.Status());
        Assert.Equal("forge/feature", IsolatedGit.CurrentBranch(fixture.Work));
        Assert.Null(fixture.OriginTip("forge/feature"));
        Assert.Single(fixture.Worktrees());
        Assert.DoesNotContain("png", IsolatedGit.RunChecked(fixture.Work, "ls-files", "--", "assets"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_renders_the_screenshots_section_into_the_description_and_keeps_the_rest()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        var pairs = await CapturedPairsAsync(fixture);

        var result = await Publisher(fixture).PublishAsync(fixture.Work, pairs, "origin/main", CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var commit = Assert.IsType<string>(result.AssetsCommit);
        var edit = Assert.Single(fixture.Platform.Edits);
        Assert.Equal(5, edit.Number);
        Assert.Null(edit.Title);
        var body = Assert.IsType<string>(edit.Body);
        Assert.Equal(body, fixture.Platform.Current!.Body);

        Assert.Contains("## Summary\nOld summary.", body, StringComparison.Ordinal);
        Assert.Contains("## Testing\nOld testing.", body, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(body, "## Screenshots"));
        Assert.Contains("| Screen | Before | After |", body, StringComparison.Ordinal);
        Assert.Contains($"![home before](https://github.com/acme/forge/blob/{commit}/home-before.png?raw=true)", body, StringComparison.Ordinal);
        Assert.Contains($"![home after](https://github.com/acme/forge/blob/{commit}/home-after.png?raw=true)", body, StringComparison.Ordinal);
        Assert.Contains($"| extra | _no image on the base_ | ![extra after](https://github.com/acme/forge/blob/{commit}/extra-after.png?raw=true) |", body, StringComparison.Ordinal);
        Assert.Contains($"| removed | ![removed before](https://github.com/acme/forge/blob/{commit}/removed-before.png?raw=true) | _no image after the change_ |", body, StringComparison.Ordinal);
        Assert.Contains("Before: `origin/main`. After: `forge/feature`.", body, StringComparison.Ordinal);
        Assert.Contains("`assets/forge-feature`", body, StringComparison.Ordinal);
        Assert.Contains("not part of this pull request's files", body, StringComparison.Ordinal);

        Assert.Equal(result.Markdown, body[(body.IndexOf("| Screen |", StringComparison.Ordinal))..].TrimEnd('\n') + "\n" is var tail && result.Markdown!.Length > 0 ? result.Markdown : null);
        Assert.Equal("https://example.test/acme/forge/pull/5", result.PullRequestUrl);
        Assert.StartsWith("Pushed 3 screen(s) to origin refs/heads/assets/forge-feature (", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_again_replaces_the_section_and_the_assets_branch_with_the_new_images()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        var publisher = Publisher(fixture);
        var first = await publisher.PublishAsync(
            fixture.Work,
            [Pair(fixture, "home", "base-one", "feature-one"), Pair(fixture, "extra", null, "extra-one")],
            "origin/main",
            CancellationToken.None);
        Assert.True(first.Success, first.Message);

        var second = await publisher.PublishAsync(
            fixture.Work,
            [Pair(fixture, "settings", "base-two", "feature-two")],
            "origin/main",
            CancellationToken.None);

        Assert.True(second.Success, second.Message);
        Assert.NotEqual(first.AssetsCommit, second.AssetsCommit);
        Assert.Equal(second.AssetsCommit, fixture.OriginTip(AssetsBranch));
        Assert.Equal(["settings-after.png", "settings-before.png"], TreeNames(fixture.Origin, second.AssetsCommit!));
        Assert.Equal("1", IsolatedGit.RunChecked(fixture.Origin, "rev-list", "--count", second.AssetsCommit!).Trim());

        var body = fixture.Platform.Current!.Body;
        Assert.Equal(1, Occurrences(body, "## Screenshots"));
        Assert.Contains(second.AssetsCommit!, body, StringComparison.Ordinal);
        Assert.DoesNotContain(first.AssetsCommit!, body, StringComparison.Ordinal);
        Assert.DoesNotContain("extra-after.png", body, StringComparison.Ordinal);
        Assert.Contains("## Summary", body, StringComparison.Ordinal);
        Assert.Contains("## Testing", body, StringComparison.Ordinal);
        Assert.Equal(2, fixture.Platform.Edits.Count);
    }

    [Fact]
    public async Task Publish_overwrites_an_assets_branch_that_someone_else_rewrote_because_the_lease_is_read_fresh()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        var publisher = Publisher(fixture);
        var first = await publisher.PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, CancellationToken.None);
        Assert.True(first.Success, first.Message);

        var emptyTree = IsolatedGit.RunChecked(fixture.Work, "mktree").Trim();
        var foreign = IsolatedGit.RunChecked(fixture.Work, "commit-tree", "-m", "someone else's assets", emptyTree).Trim();
        IsolatedGit.RunChecked(fixture.Work, "push", "-q", "--force", "origin", $"{foreign}:{AssetsRef}");
        Assert.Equal(foreign, fixture.OriginTip(AssetsBranch));

        var second = await publisher.PublishAsync(fixture.Work, [Pair(fixture, "home", "c", "d")], null, CancellationToken.None);

        Assert.True(second.Success, second.Message);
        Assert.Equal(second.AssetsCommit, fixture.OriginTip(AssetsBranch));
        Assert.Equal(["home-after.png", "home-before.png"], TreeNames(fixture.Origin, second.AssetsCommit!));
    }

    [Fact]
    public async Task Publish_without_a_base_label_writes_a_caption_that_names_only_the_assets_branch()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", null, "now")], null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        var body = fixture.Platform.Current!.Body;
        Assert.DoesNotContain("Before:", body, StringComparison.Ordinal);
        Assert.Contains("Images are stored on the branch `assets/forge-feature`", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://github.com/acme/forge.git", "https://github.com/acme/forge/blob/")]
    [InlineData("git@github.com:acme/forge.git", "https://github.com/acme/forge/blob/")]
    [InlineData("ssh://git@ghe.example.test:2222/acme/forge", "https://ghe.example.test/acme/forge/blob/")]
    [InlineData("https://user:s3cret-token@ghe.example.test/acme/forge.git", "https://ghe.example.test/acme/forge/blob/")]
    public async Task Publish_builds_the_links_from_the_address_of_the_remote_in_any_common_form(string address, string expectedPrefix)
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        Reach(fixture, address);

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains($"{expectedPrefix}{result.AssetsCommit}/home-after.png?raw=true", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", fixture.Platform.Current!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_uses_the_push_address_when_the_remote_has_a_separate_one()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        const string Fork = "https://github.com/fork-owner/forge.git";
        IsolatedGit.RunChecked(fixture.Work, "config", "--local", "remote.origin.pushurl", Fork);
        IsolatedGit.RunChecked(fixture.Work, "config", "--local", "--add", "url." + fixture.Origin.Replace('\\', '/') + ".insteadOf", Fork);

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains($"https://github.com/fork-owner/forge/blob/{result.AssetsCommit}/", result.Markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("acme", result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_refuses_a_remote_that_is_not_a_web_address_and_pushes_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        fixture.UseAddress(fixture.Origin.Replace('\\', '/'));

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Remote, result.Stage);
        Assert.Contains("not a GitHub address", result.Message, StringComparison.Ordinal);
        Assert.Null(result.AssetsCommit);
        Assert.Null(fixture.OriginTip(AssetsBranch));
        Assert.Empty(fixture.Platform.Edits);
        Assert.Equal(0, fixture.Platform.ViewCount);
    }

    [Fact]
    public async Task Publish_never_prints_the_credentials_of_a_remote_it_refuses()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        fixture.UseAddress("https://oauth2:s3cret-token@gitlab.example.test/group/subgroup/project.git");

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Remote, result.Stage);
        Assert.Contains("gitlab.example.test", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth2", result.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Platform.Edits);
    }

    [Fact]
    public async Task Publish_fails_at_the_remote_stage_when_head_is_detached()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        IsolatedGit.RunChecked(fixture.Work, "checkout", "-q", "--detach");

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Remote, result.Stage);
        Assert.Contains("detached", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Platform.Edits);
        Assert.Empty(IsolatedGit.RunChecked(fixture.Origin, "for-each-ref", "refs/heads/assets"));
    }

    [Fact]
    public async Task Publish_without_images_fails_at_the_objects_stage()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [], null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Objects, result.Stage);
        Assert.Contains("no images", result.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Platform.Edits);
    }

    [Fact]
    public async Task Publish_fails_at_the_objects_stage_when_an_image_file_is_missing_and_pushes_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        var good = Pair(fixture, "home", "a", "b");
        var gone = new ScreenshotPair("gone", Path.Combine(fixture.Markers, "does-not-exist.png"), null);

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [good, gone], null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Objects, result.Stage);
        Assert.Contains("Could not store the images as git objects", result.Message, StringComparison.Ordinal);
        Assert.Null(fixture.OriginTip(AssetsBranch));
        Assert.Empty(fixture.Platform.Edits);
    }

    [Fact]
    public async Task Publish_fails_at_the_objects_stage_for_an_image_name_that_is_not_a_plain_file_name()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        var real = Pair(fixture, "home", null, "b");
        var sneaky = real with { Name = "../escape" };

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [sneaky], null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Objects, result.Stage);
        Assert.Null(fixture.OriginTip(AssetsBranch));
        Assert.Empty(fixture.Platform.Edits);
    }

    [Fact]
    public async Task Publish_without_a_pull_request_still_pushes_the_images_and_hands_back_the_markdown()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = null;

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], "origin/main", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Body, result.Stage);
        Assert.Equal(PrFailureReason.PullRequestNotFound, result.Reason);
        Assert.Equal(AssetsRef, result.AssetsRef);
        Assert.Equal(result.AssetsCommit, fixture.OriginTip(AssetsBranch));
        Assert.Contains($"https://github.com/acme/forge/blob/{result.AssetsCommit}/home-before.png?raw=true", result.Markdown, StringComparison.Ordinal);
        Assert.Contains("The images were pushed to origin refs/heads/assets/forge-feature", result.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Platform.Edits);
        Assert.Null(result.PullRequestUrl);
    }

    [Fact]
    public async Task Publish_reports_a_description_the_platform_did_not_keep_as_a_body_failure_after_the_push()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        fixture.Platform.DropEdits = true;

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Body, result.Stage);
        Assert.Equal(result.AssetsCommit, fixture.OriginTip(AssetsBranch));
        Assert.NotEmpty(result.Markdown!);
        Assert.Contains("the description was not updated", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("## Screenshots", fixture.Platform.Current!.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_stops_at_the_push_stage_when_the_remote_rejects_the_branch_and_edits_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        fixture.RejectPushes();

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Push, result.Stage);
        Assert.NotEqual(GitRemoteFailureKind.None, result.Failure);
        Assert.Null(result.AssetsCommit);
        Assert.Null(result.Markdown);
        Assert.Null(fixture.OriginTip(AssetsBranch));
        Assert.Empty(fixture.Platform.Edits);
        Assert.Equal(0, fixture.Platform.ViewCount);
    }

    [Fact]
    public async Task Publish_stops_at_the_push_stage_when_the_remote_cannot_be_reached()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        var missing = Path.Combine(fixture.Markers, "no-such-remote").Replace('\\', '/');
        IsolatedGit.RunChecked(fixture.Work, "config", "--local", "--unset-all", "url." + fixture.Origin.Replace('\\', '/') + ".insteadOf");
        IsolatedGit.RunChecked(fixture.Work, "config", "--local", "url." + missing + ".insteadOf", ScreenshotFixture.PublicAddress);

        var result = await Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ScreenshotPublishStage.Push, result.Stage);
        Assert.Empty(fixture.Platform.Edits);
    }

    [Fact]
    public async Task Publish_with_a_cancelled_token_throws_and_pushes_nothing()
    {
        if (!GitProbe.IsAvailable)
        {
            return;
        }

        using var fixture = new ScreenshotFixture();
        fixture.Platform.Current = PrLifecycleTestHelpers.Pr();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Publisher(fixture).PublishAsync(fixture.Work, [Pair(fixture, "home", "a", "b")], null, cancellation.Token));

        Assert.Null(fixture.OriginTip(AssetsBranch));
        Assert.Empty(fixture.Platform.Edits);
    }

    [Fact]
    public async Task Publish_validates_its_arguments()
    {
        var publisher = new ScreenshotPublisher(new GitCli(), new PrLifecycleFakePlatform(), new GitToolSettings());

        await Assert.ThrowsAsync<ArgumentNullException>(() => publisher.PublishAsync("work", null!, null, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => publisher.PublishAsync(" ", [], null, CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => new ScreenshotPublisher(null!, new PrLifecycleFakePlatform(), new GitToolSettings()));
        Assert.Throws<ArgumentNullException>(() => new ScreenshotPublisher(new GitCli(), null!, new GitToolSettings()));
        Assert.Throws<ArgumentNullException>(() => new ScreenshotPublisher(new GitCli(), new PrLifecycleFakePlatform(), null!));
    }
}
