using Forge.Abstractions;
using Forge.App.Git;
using Xunit;

namespace Forge.App.Tests.Git;

/// <summary>
/// Pins <see cref="StepCommitter"/> against real git repositories and a real <c>SqliteTodoStore</c>: the order of the
/// outcome rules (cancel, preference, resolver, missing directory, not a repository, no changes, operation in progress,
/// detached HEAD, commit), the commit message, what is persisted per outcome and the result text.
/// </summary>
public sealed class StepCommitterTests
{
    private static CancellationTokenSource Bound() => new(TimeSpan.FromSeconds(90));

    // ------------------------------------------------------------ order of the outcome rules

    [Fact]
    public async Task A_cancelled_token_throws_before_the_preference_the_resolver_or_git_is_touched()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        var head = await harness.Repo.HeadAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Committer.CommitStepAsync(item, cts.Token));

        Assert.Equal(0, harness.ResolveCalls);
        Assert.Equal(head, await harness.Repo.HeadAsync());
        Assert.Equal("?? new.txt", await harness.Repo.StatusAsync());
    }

    [Fact]
    public async Task The_preference_off_skips_without_resolving_or_committing()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        var head = await harness.Repo.HeadAsync();
        harness.Enabled = false;
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Skipped, result.Outcome);
        Assert.Null(result.CommitId);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal(0, harness.ResolveCalls);
        Assert.Equal(head, await harness.Repo.HeadAsync());
        Assert.Equal("?? new.txt", await harness.Repo.StatusAsync());
    }

    [Fact]
    public async Task The_preference_is_read_on_every_call_so_a_change_applies_to_the_next_step()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var first = await harness.AddDoneAsync("first");
        var second = await harness.AddDoneAsync("second");
        harness.Repo.WriteFile("a.txt", "a\n");
        using var cts = Bound();

        harness.Enabled = false;
        var off = await harness.Committer.CommitStepAsync(first, cts.Token);
        harness.Enabled = true;
        var on = await harness.Committer.CommitStepAsync(second, cts.Token);

        Assert.Equal(StepCommitOutcome.Skipped, off.Outcome);
        Assert.Equal(StepCommitOutcome.Committed, on.Outcome);
        Assert.Equal("second", await harness.Repo.LastMessageAsync());
    }

    [Fact]
    public async Task A_resolver_that_throws_fails_the_step_and_records_the_reason()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        harness.ResolveFailure = new InvalidOperationException("session store is down");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Null(result.CommitId);
        Assert.Contains("session store is down", result.Message, StringComparison.Ordinal);
        var stored = await harness.StoredAsync(item.Id);
        Assert.Equal(result.Message, stored.CommitError);
        Assert.Null(stored.CommitId);
        Assert.Equal(TodoStatus.Done, stored.Status);
    }

    [Fact]
    public async Task A_cancelled_resolver_propagates_the_cancellation_instead_of_failing_the_step()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        harness.ResolveFailure = new OperationCanceledException();
        using var cts = Bound();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.Committer.CommitStepAsync(item, cts.Token));

        var stored = await harness.StoredAsync(item.Id);
        Assert.Null(stored.CommitError);
        Assert.Null(stored.CommitId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_session_without_a_worktree_is_skipped_and_nothing_is_committed(string? worktree)
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        harness.Repo.WriteFile("new.txt", "x\n");
        harness.WorktreeBySession[StepCommitHarness.SessionId] = worktree;
        var head = await harness.Repo.HeadAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Skipped, result.Outcome);
        Assert.Null(result.CommitId);
        Assert.Equal(head, await harness.Repo.HeadAsync());
        Assert.Equal("?? new.txt", await harness.Repo.StatusAsync());
    }

    [Fact]
    public async Task An_item_without_a_session_id_is_skipped_without_asking_the_resolver()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = (await harness.AddDoneAsync()) with { SessionId = "  " };
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Skipped, result.Outcome);
        Assert.Equal(0, harness.ResolveCalls);
    }

    [Fact]
    public async Task A_missing_worktree_directory_is_skipped()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        harness.Resolved = Path.Combine(harness.Repo.Root, "gone", "worktree");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Skipped, result.Outcome);
        Assert.Null(result.CommitId);
        Assert.False(Directory.Exists(harness.Resolved));
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_is_skipped_and_gets_no_git_directory()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        var plain = Path.Combine(harness.Repo.Root, "plain");
        Directory.CreateDirectory(plain);
        await File.WriteAllTextAsync(Path.Combine(plain, "notes.txt"), "not tracked\n");
        harness.Resolved = plain;
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Skipped, result.Outcome);
        Assert.Null(result.CommitId);
        Assert.False(Directory.Exists(Path.Combine(plain, ".git")));
    }

    [Fact]
    public async Task A_clean_tree_is_NoChanges_and_creates_no_commit()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        var head = await harness.Repo.HeadAsync();
        var count = await harness.Repo.CommitCountAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.NoChanges, result.Outcome);
        Assert.Null(result.CommitId);
        Assert.Equal(head, await harness.Repo.HeadAsync());
        Assert.Equal(count, await harness.Repo.CommitCountAsync());
    }

    [Fact]
    public async Task A_tree_with_only_ignored_files_is_NoChanges()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.WriteFile(".gitignore", "build/\n*.log\n");
        await harness.Repo.GitAsync("add", "-A");
        await harness.Repo.GitAsync("commit", "-q", "-m", "ignore rules");
        harness.Repo.WriteFile("build/output.bin", "bin\n");
        harness.Repo.WriteFile("trace.log", "log\n");
        var item = await harness.AddDoneAsync();
        var head = await harness.Repo.HeadAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.NoChanges, result.Outcome);
        Assert.Equal(head, await harness.Repo.HeadAsync());
    }

    // ------------------------------------------------------------ the commit itself

    [Fact]
    public async Task Modified_deleted_and_untracked_files_all_land_in_one_commit()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.WriteFile("keep.txt", "keep\n");
        harness.Repo.WriteFile("remove-me.txt", "bye\n");
        await harness.Repo.GitAsync("add", "-A");
        await harness.Repo.GitAsync("commit", "-q", "-m", "baseline");
        harness.Repo.WriteFile("keep.txt", "keep, changed\n");
        File.Delete(harness.Repo.FullPath("remove-me.txt"));
        harness.Repo.WriteFile("src/deep/new file.txt", "new\n");
        harness.Repo.WriteFile("文档/说明 终稿.md", "# 说明\n");
        var item = await harness.AddDoneAsync("Add feature", "feat: add the feature");
        var before = await harness.Repo.CommitCountAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Equal(before + 1, await harness.Repo.CommitCountAsync());
        var changes = await harness.Repo.LastCommitChangesAsync();
        Assert.Equal(4, changes.Count);
        Assert.Contains("M\tkeep.txt", changes);
        Assert.Contains("D\tremove-me.txt", changes);
        Assert.Contains("A\tsrc/deep/new file.txt", changes);
        Assert.Contains("A\t文档/说明 终稿.md", changes);
        Assert.Equal(string.Empty, await harness.Repo.StatusAsync());
    }

    [Fact]
    public async Task An_untracked_only_change_is_committed_not_reported_as_NoChanges()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.WriteFile("brand-new.txt", "new\n");
        var item = await harness.AddDoneAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Equal(["A\tbrand-new.txt"], await harness.Repo.LastCommitChangesAsync());
    }

    [Fact]
    public async Task A_committed_step_returns_the_full_sha_and_the_documented_message_and_persists_it()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.WriteFile("feature.txt", "feature\n");
        var item = await harness.AddDoneAsync("Add feature", "feat: add feature", "Adds the feature.");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        var head = await harness.Repo.HeadAsync();
        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Equal(head, result.CommitId);
        Assert.Equal(40, result.CommitId!.Length);
        Assert.Equal($"Committed {head[..7]}: feat: add feature", result.Message);
        var stored = await harness.StoredAsync(item.Id);
        Assert.Equal(head, stored.CommitId);
        Assert.Null(stored.CommitError);
        Assert.Equal(TodoStatus.Done, stored.Status);
    }

    [Fact]
    public async Task The_result_message_falls_back_to_the_title_when_the_item_has_no_commit_subject()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.WriteFile("feature.txt", "feature\n");
        var item = await harness.AddDoneAsync("Wire the panel");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal($"Committed {result.CommitId![..7]}: Wire the panel", result.Message);
    }

    [Fact]
    public async Task A_committed_step_clears_an_earlier_recorded_failure()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        await harness.Store.SetCommitResultAsync(item.Id, null, "hook failed earlier", CancellationToken.None);
        harness.Repo.WriteFile("fix.txt", "fixed\n");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(await harness.StoredAsync(item.Id), cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        var stored = await harness.StoredAsync(item.Id);
        Assert.Equal(result.CommitId, stored.CommitId);
        Assert.Null(stored.CommitError);
    }

    public static TheoryData<string?, string, string?, string?, string> MessageCases() => new()
    {
        // subject, title, summary, verification, expected full message
        { "feat: add x", "Title", null, null, "feat: add x" },
        { "\n  \n  feat: add y  \nsecond line is not a subject", "Title", null, null, "feat: add y" },
        { "   ", "Title here", null, null, "Title here" },
        { null, "  Title with spaces  \nsecond title line", null, null, "Title with spaces" },
        { null, "   ", null, null, StepCommitter.FallbackSubject },
        { "subj", "T", "Did the thing.", null, "subj\n\nDid the thing." },
        { "subj", "T", "  \r\n  line one\r\nline two  \r\n\r\n", null, "subj\n\nline one\nline two" },
        { "subj", "T", "line1\rline2", null, "subj\n\nline1\nline2" },
        { "subj", "T", "   ", null, "subj" },
        { "subj", "T", "Body text", "ran 120 tests, all green", "subj\n\nBody text" },
        { "subj", "T", null, "only the verification is set", "subj" },
        { "subj", "T", "## Summary\n- one\n# heading-like line\n\n## Testing\n- two", null, "subj\n\n## Summary\n- one\n# heading-like line\n\n## Testing\n- two" },
        { "修复登录页的空指针", "T", "改动:处理了空值。\n测试:新增两条用例。", null, "修复登录页的空指针\n\n改动:处理了空值。\n测试:新增两条用例。" },
    };

    [Theory]
    [MemberData(nameof(MessageCases))]
    public async Task The_commit_message_is_subject_then_summary_and_never_the_verification(
        string? commitSubject,
        string title,
        string? summary,
        string? verification,
        string expected)
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.WriteFile("change.txt", "change\n");
        var stored = await harness.AddDoneAsync("stored title");
        var item = stored with { Title = title, CommitSubject = commitSubject, Summary = summary, Verification = verification };
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        var message = await harness.Repo.LastMessageAsync();
        Assert.Equal(expected, message);
        if (verification is not null)
        {
            Assert.DoesNotContain(verification, message, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------ failures are results, not exceptions

    [Theory]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    [InlineData("revert")]
    public async Task An_operation_in_progress_fails_the_step_names_the_operation_and_commits_nothing(string operation)
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        switch (operation)
        {
            case "merge":
                await harness.Repo.StartMergeInProgressAsync();
                break;
            case "rebase":
                await harness.Repo.StartRebaseInProgressAsync();
                break;
            case "cherry-pick":
                await harness.Repo.StartCherryPickInProgressAsync();
                break;
            default:
                await harness.Repo.StartRevertInProgressAsync();
                break;
        }

        var item = await harness.AddDoneAsync();
        var head = await harness.Repo.HeadAsync();
        var count = await harness.Repo.CommitCountAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Null(result.CommitId);
        Assert.Contains(operation, result.Message, StringComparison.Ordinal);
        Assert.Equal(head, await harness.Repo.HeadAsync());
        Assert.Equal(count, await harness.Repo.CommitCountAsync());
        var stored = await harness.StoredAsync(item.Id);
        Assert.Equal(result.Message, stored.CommitError);
        Assert.Null(stored.CommitId);
        Assert.Equal(TodoStatus.Done, stored.Status);
    }

    [Fact]
    public async Task A_detached_head_fails_the_step_and_commits_nothing()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        await harness.Repo.GitAsync("checkout", "-q", "--detach");
        harness.Repo.WriteFile("change.txt", "change\n");
        var item = await harness.AddDoneAsync();
        var head = await harness.Repo.HeadAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Null(result.CommitId);
        Assert.Contains("detached", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(head, await harness.Repo.HeadAsync());
        Assert.Equal("?? change.txt", await harness.Repo.StatusAsync());
        var stored = await harness.StoredAsync(item.Id);
        Assert.Equal(result.Message, stored.CommitError);
        Assert.Equal(TodoStatus.Done, stored.Status);
    }

    [Fact]
    public async Task A_failing_pre_commit_hook_fails_the_step_with_the_hook_output_and_a_retry_commits_the_same_work()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.InstallHook("pre-commit", "echo 'hook rejected: lint failed' >&2\nexit 1\n");
        harness.Repo.WriteFile("change.txt", "change\n");
        var item = await harness.AddDoneAsync("Add feature", "feat: add feature");
        var head = await harness.Repo.HeadAsync();
        using var cts = Bound();

        var failed = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, failed.Outcome);
        Assert.Null(failed.CommitId);
        Assert.StartsWith("git commit failed: ", failed.Message, StringComparison.Ordinal);
        Assert.Contains("hook rejected: lint failed", failed.Message, StringComparison.Ordinal);
        Assert.Equal(head, await harness.Repo.HeadAsync());
        Assert.True(File.Exists(harness.Repo.FullPath("change.txt")));
        var afterFailure = await harness.StoredAsync(item.Id);
        Assert.Equal(failed.Message, afterFailure.CommitError);
        Assert.Null(afterFailure.CommitId);
        Assert.Equal(TodoStatus.Done, afterFailure.Status);

        harness.Repo.RemoveHook("pre-commit");
        var retried = await harness.Committer.CommitStepAsync(afterFailure, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, retried.Outcome);
        Assert.Equal(["A\tchange.txt"], await harness.Repo.LastCommitChangesAsync());
        var afterRetry = await harness.StoredAsync(item.Id);
        Assert.Equal(retried.CommitId, afterRetry.CommitId);
        Assert.Null(afterRetry.CommitError);
    }

    [Fact]
    public async Task A_hook_failure_keeps_only_the_tail_of_a_long_output_bounded()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.InstallHook(
            "pre-commit",
            "i=0\nwhile [ $i -lt 400 ]; do echo \"noise line $i padding padding padding\" >&2; i=$((i+1)); done\necho 'FINAL VERDICT: rejected' >&2\nexit 1\n");
        harness.Repo.WriteFile("change.txt", "change\n");
        var item = await harness.AddDoneAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Contains("FINAL VERDICT: rejected", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("noise line 0 ", result.Message, StringComparison.Ordinal);
        Assert.True(
            result.Message.Length <= "git commit failed: ".Length + StepCommitter.MaxFailureCharacters + 1,
            $"the failure text is {result.Message.Length} characters long");
        var stored = await harness.StoredAsync(item.Id);
        Assert.Equal(result.Message, stored.CommitError);
    }

    [Fact]
    public async Task A_hook_that_fails_silently_reports_the_exit_code()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        harness.Repo.InstallHook("pre-commit", "exit 1\n");
        harness.Repo.WriteFile("change.txt", "change\n");
        var item = await harness.AddDoneAsync();
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Contains("1", result.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.Message["git commit failed:".Length..]));
    }

    // ------------------------------------------------------------ persistence per outcome

    [Fact]
    public async Task Committed_and_Failed_each_raise_exactly_one_store_change()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        var changes = 0;
        harness.Store.Changed += _ => Interlocked.Increment(ref changes);
        using var cts = Bound();

        harness.Repo.InstallHook("pre-commit", "echo no >&2\nexit 1\n");
        harness.Repo.WriteFile("one.txt", "1\n");
        var failed = await harness.Committer.CommitStepAsync(item, cts.Token);
        Assert.Equal(StepCommitOutcome.Failed, failed.Outcome);
        Assert.Equal(1, Volatile.Read(ref changes));

        harness.Repo.RemoveHook("pre-commit");
        var committed = await harness.Committer.CommitStepAsync(item, cts.Token);
        Assert.Equal(StepCommitOutcome.Committed, committed.Outcome);
        Assert.Equal(2, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task NoChanges_and_Skipped_write_nothing_when_no_failure_is_recorded()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        var changes = 0;
        harness.Store.Changed += _ => Interlocked.Increment(ref changes);
        using var cts = Bound();

        var noChanges = await harness.Committer.CommitStepAsync(item, cts.Token);
        harness.Enabled = false;
        var skipped = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.NoChanges, noChanges.Outcome);
        Assert.Equal(StepCommitOutcome.Skipped, skipped.Outcome);
        Assert.Equal(0, Volatile.Read(ref changes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoChanges_and_Skipped_clear_a_recorded_failure_with_one_write(bool skipped)
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        await harness.Store.SetCommitResultAsync(item.Id, null, "earlier hook failure", CancellationToken.None);
        var recorded = await harness.StoredAsync(item.Id);
        harness.Enabled = !skipped;
        var changes = 0;
        harness.Store.Changed += _ => Interlocked.Increment(ref changes);
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(recorded, cts.Token);

        Assert.Equal(skipped ? StepCommitOutcome.Skipped : StepCommitOutcome.NoChanges, result.Outcome);
        var stored = await harness.StoredAsync(item.Id);
        Assert.Null(stored.CommitError);
        Assert.Null(stored.CommitId);
        Assert.Equal(TodoStatus.Done, stored.Status);
        Assert.Equal(1, Volatile.Read(ref changes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoChanges_and_Skipped_never_wipe_a_recorded_commit_id(bool skipped)
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        const string Recorded = "0123456789abcdef0123456789abcdef01234567";
        await harness.Store.SetCommitResultAsync(item.Id, Recorded, null, CancellationToken.None);
        var committedItem = await harness.StoredAsync(item.Id);
        harness.Enabled = !skipped;
        var changes = 0;
        harness.Store.Changed += _ => Interlocked.Increment(ref changes);
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(committedItem, cts.Token);

        Assert.Equal(skipped ? StepCommitOutcome.Skipped : StepCommitOutcome.NoChanges, result.Outcome);
        var stored = await harness.StoredAsync(item.Id);
        Assert.Equal(Recorded, stored.CommitId);
        Assert.Null(stored.CommitError);
        Assert.Equal(0, Volatile.Read(ref changes));
    }

    [Fact]
    public async Task A_stale_snapshot_with_an_error_does_not_write_when_the_store_no_longer_has_one()
    {
        FaultyTodoStore? faulty = null;
        await using var harness = await StepCommitHarness.CreateAsync(store => faulty = new FaultyTodoStore(store));
        var item = await harness.AddDoneAsync();
        var stale = item with { CommitError = "failure the agent already fixed" };
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(stale, cts.Token);

        Assert.Equal(StepCommitOutcome.NoChanges, result.Outcome);
        Assert.Equal(0, faulty!.SetCommitResultCalls);
    }

    [Fact]
    public async Task A_snapshot_without_an_error_still_clears_an_error_the_store_has()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        await harness.Store.SetCommitResultAsync(item.Id, null, "failed in the meantime", CancellationToken.None);
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.NoChanges, result.Outcome);
        Assert.Null((await harness.StoredAsync(item.Id)).CommitError);
    }

    [Fact]
    public async Task When_the_checklist_cannot_be_re_read_the_snapshot_decides_whether_to_clear()
    {
        FaultyTodoStore? faulty = null;
        await using var harness = await StepCommitHarness.CreateAsync(store => faulty = new FaultyTodoStore(store));
        var item = await harness.AddDoneAsync();
        await harness.Store.SetCommitResultAsync(item.Id, null, "earlier failure", CancellationToken.None);
        var withError = await harness.StoredAsync(item.Id);
        faulty!.ListFailure = new InvalidOperationException("db locked");
        using var cts = Bound();

        var withoutError = await harness.Committer.CommitStepAsync(item with { CommitError = null }, cts.Token);
        Assert.Equal(StepCommitOutcome.NoChanges, withoutError.Outcome);
        Assert.Equal(0, faulty.SetCommitResultCalls);

        var cleared = await harness.Committer.CommitStepAsync(withError, cts.Token);
        Assert.Equal(StepCommitOutcome.NoChanges, cleared.Outcome);
        Assert.Equal(1, faulty.SetCommitResultCalls);
        Assert.Null((await harness.StoredAsync(item.Id)).CommitError);
    }

    [Fact]
    public async Task A_store_that_cannot_persist_does_not_change_the_returned_result_or_the_commit()
    {
        FaultyTodoStore? faulty = null;
        await using var harness = await StepCommitHarness.CreateAsync(store => faulty = new FaultyTodoStore(store));
        faulty!.SetCommitResultFailure = new InvalidOperationException("database is read-only");
        harness.Repo.WriteFile("change.txt", "change\n");
        var item = await harness.AddDoneAsync("Add feature", "feat: add feature");
        using var cts = Bound();

        var committed = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, committed.Outcome);
        Assert.Equal(await harness.Repo.HeadAsync(), committed.CommitId);
        Assert.Equal("feat: add feature", await harness.Repo.LastMessageAsync());
        Assert.Null((await harness.StoredAsync(item.Id)).CommitId);

        harness.Enabled = false;
        var skipped = await harness.Committer.CommitStepAsync(item with { CommitError = "old" }, cts.Token);
        Assert.Equal(StepCommitOutcome.Skipped, skipped.Outcome);
    }

    [Fact]
    public async Task A_step_for_an_item_the_store_no_longer_has_still_commits()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var item = await harness.AddDoneAsync();
        await harness.Store.RemoveAsync(item.Id, CancellationToken.None);
        harness.Repo.WriteFile("change.txt", "change\n");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Equal(["A\tchange.txt"], await harness.Repo.LastCommitChangesAsync());
    }

    // ------------------------------------------------------------ a linked worktree, the real target

    [Fact]
    public async Task A_step_in_a_linked_worktree_lands_on_that_worktrees_branch_only()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var worktree = Path.Combine(harness.Repo.Root, "repo.worktrees", "step");
        await harness.Repo.GitAsync("worktree", "add", "-q", "-b", "forge/step", worktree);
        var mainHead = await harness.Repo.HeadAsync();
        await File.WriteAllTextAsync(Path.Combine(worktree, "in-worktree.txt"), "worktree work\n");
        harness.Resolved = worktree;
        var item = await harness.AddDoneAsync("Work in the worktree");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Committed, result.Outcome);
        Assert.Equal(mainHead, await harness.Repo.HeadAsync());
        Assert.Equal(result.CommitId, await StepCommitRepo.GitInAsync(worktree, "rev-parse", "forge/step"));
        Assert.Equal(string.Empty, await StepCommitRepo.GitInAsync(worktree, "status", "--porcelain"));
        Assert.Equal("Work in the worktree", await StepCommitRepo.GitInAsync(worktree, "log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task An_operation_in_progress_inside_a_linked_worktree_is_found_through_its_own_git_dir()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var worktree = Path.Combine(harness.Repo.Root, "repo.worktrees", "conflict");
        await harness.Repo.GitAsync("worktree", "add", "-q", "-b", "forge/conflict", worktree);
        await File.WriteAllTextAsync(Path.Combine(worktree, "a.txt"), "base\n");
        await StepCommitRepo.GitInAsync(worktree, "add", "-A");
        await StepCommitRepo.GitInAsync(worktree, "commit", "-q", "-m", "add a");
        await StepCommitRepo.GitInAsync(worktree, "checkout", "-q", "-b", "side");
        await File.WriteAllTextAsync(Path.Combine(worktree, "a.txt"), "side\n");
        await StepCommitRepo.GitInAsync(worktree, "commit", "-q", "-am", "side change");
        await StepCommitRepo.GitInAsync(worktree, "checkout", "-q", "forge/conflict");
        await File.WriteAllTextAsync(Path.Combine(worktree, "a.txt"), "own\n");
        await StepCommitRepo.GitInAsync(worktree, "commit", "-q", "-am", "own change");
        using (var setup = Bound())
        {
            await StepCommitRepo.Cli.RunAsync(worktree, ["merge", "side"], setup.Token);
        }

        harness.Resolved = worktree;
        var item = await harness.AddDoneAsync();
        var head = await StepCommitRepo.GitInAsync(worktree, "rev-parse", "HEAD");
        using var cts = Bound();

        var result = await harness.Committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Failed, result.Outcome);
        Assert.Contains("merge", result.Message, StringComparison.Ordinal);
        Assert.Equal(head, await StepCommitRepo.GitInAsync(worktree, "rev-parse", "HEAD"));
    }

    // ------------------------------------------------------------ construction and the resolver helper

    [Fact]
    public void The_constructor_rejects_null_collaborators()
    {
        var git = StepCommitRepo.Cli;
        using var directory = new TempData();
        var store = new Forge.Core.Storage.SqliteTodoStore(directory.Path);
        Func<string, CancellationToken, Task<string?>> resolver = static (_, _) => Task.FromResult<string?>(null);

        Assert.Throws<ArgumentNullException>(() => new StepCommitter(null!, store, resolver, static () => true));
        Assert.Throws<ArgumentNullException>(() => new StepCommitter(git, null!, resolver, static () => true));
        Assert.Throws<ArgumentNullException>(() => new StepCommitter(git, store, null!, static () => true));
        Assert.Throws<ArgumentNullException>(() => new StepCommitter(git, store, resolver, null!));
        store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task A_null_item_is_rejected()
    {
        await using var harness = await StepCommitHarness.CreateAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => harness.Committer.CommitStepAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task WorktreeOf_returns_the_session_worktree_path_or_null()
    {
        var sessions = new WorktreeSessionStore(
            new SessionInfo("with-worktree", "C:/repo", "title", DateTimeOffset.UnixEpoch) { WorktreePath = "C:/repo.worktrees/x" },
            new SessionInfo("general", "C:/General/scratch/1", "title", DateTimeOffset.UnixEpoch));
        var resolve = StepCommitter.WorktreeOf(sessions);

        Assert.Equal("C:/repo.worktrees/x", await resolve("with-worktree", CancellationToken.None));
        Assert.Null(await resolve("general", CancellationToken.None));
        Assert.Null(await resolve("unknown-session", CancellationToken.None));
        Assert.Throws<ArgumentNullException>(() => StepCommitter.WorktreeOf(null!));
    }

    [Fact]
    public async Task A_general_session_without_a_worktree_path_is_never_committed_into()
    {
        await using var harness = await StepCommitHarness.CreateAsync();
        var general = new SessionInfo(StepCommitHarness.SessionId, harness.Repo.Path, "general", DateTimeOffset.UnixEpoch);
        var committer = new StepCommitter(
            StepCommitRepo.Cli,
            harness.Store,
            StepCommitter.WorktreeOf(new WorktreeSessionStore(general)),
            static () => true);
        harness.Repo.WriteFile("scratch/notes.txt", "scratch\n");
        var item = await harness.AddDoneAsync();
        var head = await harness.Repo.HeadAsync();
        using var cts = Bound();

        var result = await committer.CommitStepAsync(item, cts.Token);

        Assert.Equal(StepCommitOutcome.Skipped, result.Outcome);
        Assert.Equal(head, await harness.Repo.HeadAsync());
        Assert.Equal("?? scratch/", await harness.Repo.StatusAsync());
    }

    /// <summary>A session store that only answers <see cref="GetSessionAsync"/>; the committer needs nothing else.</summary>
    internal sealed class WorktreeSessionStore(params SessionInfo[] sessions) : ISessionStore
    {
        public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken ct) =>
            Task.FromResult(sessions.FirstOrDefault(session => session.Id == sessionId));

        public Task<SessionInfo> CreateSessionAsync(string workspacePath, string title, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string? workspacePath, CancellationToken ct) => throw new NotSupportedException();

        public Task<long> AppendAsync(string sessionId, ChatMessage message, TokenUsage? usage, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<StoredMessage> ReadAsync(string sessionId, long fromSeq, long? toSeq, CancellationToken ct) => throw new NotSupportedException();

        public Task<long> LastSeqAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task PutSummaryAsync(SummaryRecord summary, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<SummaryRecord>> GetSummariesAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<SearchHit>> SearchAsync(string sessionId, string query, int limit, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A temp directory for the store of a test that has no repository.</summary>
    private sealed class TempData : IDisposable
    {
        public TempData()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-step-commit-data-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            StepCommitRepo.DeleteTree(Path);
        }
    }
}
