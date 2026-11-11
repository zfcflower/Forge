using System.Globalization;
using Forge.Abstractions;
using Forge.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using static Forge.Core.Tests.Storage.StorageTestHelpers;

namespace Forge.Core.Tests.Storage;

/// <summary>
/// The session / worktree lifecycle of <see cref="SqliteSessionStore"/> (TASK-session-worktree-lifecycle 4.1-4.4, 7.5-7.8):
/// occupancy (conditional statements, the unique-index backstop, I1 containment inside a writer batch), handover, resume,
/// activation, soft delete, purge, self-heal, notices, fingerprint and branch-rename bookkeeping. Every test runs against a
/// real temporary <c>forge.db</c>.
/// </summary>
public sealed class SessionLifecycleStoreTests
{
    private static SessionCreate Root(string workspace, string title = "t") => new(workspace, title);

    private static ISessionCatalog Catalog(SqliteSessionStore store) => store;

    private static string RawConnection(SqliteSessionStore store) =>
        new SqliteConnectionStringBuilder(store.ConnectionString) { Pooling = false }.ToString();

    private static async Task<long> CountAsync(SqliteSessionStore store, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(RawConnection(store));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> TextAsync(SqliteSessionStore store, string sql, string id)
    {
        await using var connection = new SqliteConnection(RawConnection(store));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task<SessionInfo> GetAsync(SqliteSessionStore store, string id) =>
        await store.GetSessionAsync(id, default) ?? throw new Xunit.Sdk.XunitException($"session {id} is missing");

    /// <summary>
    /// Holds the database write lock from a second connection, so the store's writer blocks on <c>BEGIN IMMEDIATE</c> of
    /// the batch it already took; everything enqueued meanwhile is read as the next batch.
    /// </summary>
    private static async Task<SqliteConnection> HoldWriteLockAsync(SqliteSessionStore store)
    {
        var blocker = new SqliteConnection(RawConnection(store));
        await blocker.OpenAsync();
        await using var begin = blocker.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE";
        await begin.ExecuteNonQueryAsync();
        return blocker;
    }

    private static async Task ReleaseWriteLockAsync(SqliteConnection blocker)
    {
        await using (var commit = blocker.CreateCommand())
        {
            commit.CommandText = "COMMIT";
            await commit.ExecuteNonQueryAsync();
        }

        await blocker.DisposeAsync();
    }

    // ---- Occupancy ----

    [Fact]
    public async Task Second_active_root_session_on_the_same_key_is_refused_with_the_holder_id()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var worktree = directory.Combine("wt");

        var holder = await store.CreateAsync(Root(directory.Combine("other")) with { WorktreePath = worktree }, default);
        // Same key reached two ways: the worktree column of one row, the workspace column (no worktree) of the other.
        var refused = await Assert.ThrowsAsync<WorktreeOccupiedException>(() => store.CreateAsync(Root(worktree + Path.DirectorySeparatorChar), default));

        Assert.Equal(holder.Id, refused.HolderSessionId);
        Assert.Equal(1L, await CountAsync(store, "SELECT count(*) FROM sessions"));
        Assert.Equal(
            OccupancyKey.Compute(worktree, directory.Combine("other")),
            await TextAsync(store, "SELECT occupancy_key FROM sessions WHERE id = $id", holder.Id));
    }

    [Fact]
    public async Task Occupancy_key_folds_case_on_windows_only()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var lower = directory.Combine("case-wt");
        var upper = directory.Combine("CASE-WT");

        var first = await store.CreateAsync(Root(lower), default);
        if (OperatingSystem.IsWindows())
        {
            var refused = await Assert.ThrowsAsync<WorktreeOccupiedException>(() => store.CreateAsync(Root(upper), default));
            Assert.Equal(first.Id, refused.HolderSessionId);
            Assert.Equal(lower.ToUpperInvariant(), await TextAsync(store, "SELECT occupancy_key FROM sessions WHERE id = $id", first.Id));
        }
        else
        {
            var second = await store.CreateAsync(Root(upper), default);
            Assert.Equal(SessionStatus.Active, second.Status);
        }
    }

    [Fact]
    public async Task Child_sessions_are_never_constrained_and_an_archived_holder_frees_the_key()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var worktree = directory.Combine("wt");

        var parent = await store.CreateAsync(Root(worktree), default);
        var child1 = await store.CreateAsync(Root(worktree) with { ParentSessionId = parent.Id }, default);
        var child2 = await store.CreateAsync(Root(worktree) with { ParentSessionId = parent.Id }, default);
        Assert.Equal(SessionStatus.Active, (await GetAsync(store, child1.Id)).Status);
        Assert.Equal(SessionStatus.Active, (await GetAsync(store, child2.Id)).Status);

        Assert.True(await store.SetStatusAsync(parent.Id, SessionStatus.Archived, default));
        var next = await store.CreateAsync(Root(worktree), default);
        Assert.Equal(SessionStatus.Active, (await GetAsync(store, next.Id)).Status);
    }

    [Fact]
    public async Task Conflicting_create_batched_with_an_append_fails_alone()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var worktree = directory.Combine("wt");
        var holder = await store.CreateAsync(Root(worktree), default);
        var bystander = await store.CreateAsync(Root(directory.Combine("other")), default);
        Assert.Equal(1, await store.AppendAsync(bystander.Id, Text("before"), null, default));

        var blocker = await HoldWriteLockAsync(store);
        var first = store.TouchAsync(bystander.Id, DateTimeOffset.UtcNow, default);
        await Task.Delay(300); // the writer has taken the touch and waits for the lock
        var conflict = store.CreateAsync(Root(worktree), default);
        var append = store.AppendAsync(bystander.Id, Text("same batch"), null, default);
        var unrelated = store.CreateAsync(Root(directory.Combine("third")), default);
        await ReleaseWriteLockAsync(blocker);

        await first;
        var refused = await Assert.ThrowsAsync<WorktreeOccupiedException>(() => conflict);
        Assert.Equal(holder.Id, refused.HolderSessionId);
        Assert.Equal(2, await append);
        Assert.Equal(SessionStatus.Active, (await GetAsync(store, (await unrelated).Id)).Status);
        var stored = await CollectAsync(store.ReadAsync(bystander.Id, 1, null, default));
        Assert.Equal([1L, 2L], stored.Select(static m => m.Seq));
        Assert.Equal(3L, await CountAsync(store, "SELECT count(*) FROM sessions"));
    }

    [Fact]
    public async Task Unique_index_backstop_turns_a_raw_activation_into_WorktreeOccupied_without_failing_the_batch()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var worktree = directory.Combine("wt");
        var old = await store.CreateAsync(Root(worktree), default);
        Assert.True(await store.SetStatusAsync(old.Id, SessionStatus.Archived, default));
        var holder = await store.CreateAsync(Root(worktree), default);

        // SetStatusAsync is the one unconditional status write: only the unique partial index stands in its way, so this
        // exercises the SQLITE_CONSTRAINT_UNIQUE (2067) translation and its savepoint containment.
        var blocker = await HoldWriteLockAsync(store);
        var first = store.TouchAsync(holder.Id, DateTimeOffset.UtcNow, default);
        await Task.Delay(300);
        var raw = store.SetStatusAsync(old.Id, SessionStatus.Active, default);
        var append = store.AppendAsync(holder.Id, Text("same batch"), null, default);
        await ReleaseWriteLockAsync(blocker);

        await first;
        var refused = await Assert.ThrowsAsync<WorktreeOccupiedException>(() => raw);
        Assert.Equal(holder.Id, refused.HolderSessionId);
        Assert.Equal(1, await append);
        Assert.Equal(SessionStatus.Archived, (await GetAsync(store, old.Id)).Status);
    }

    // ---- Activation ----

    [Fact]
    public async Task Activate_restores_archived_and_deleted_sessions_and_refuses_a_held_key()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var worktree = directory.Combine("wt");
        var root = await store.CreateAsync(Root(worktree), default);
        var child = await store.CreateAsync(Root(worktree) with { ParentSessionId = root.Id }, default);
        var grandchild = await store.CreateAsync(Root(worktree) with { ParentSessionId = child.Id }, default);
        Assert.Equal(3, await catalog.SoftDeleteAsync([root.Id], default));

        var holder = await store.CreateAsync(Root(worktree), default);
        var refused = await Assert.ThrowsAsync<WorktreeOccupiedException>(() => catalog.ActivateAsync(root.Id, default));
        Assert.Equal(holder.Id, refused.HolderSessionId);
        Assert.Equal(SessionStatus.Deleted, (await GetAsync(store, root.Id)).Status);
        Assert.Equal(SessionStatus.Deleted, (await GetAsync(store, child.Id)).Status);

        Assert.True(await store.SetStatusAsync(holder.Id, SessionStatus.Archived, default));
        Assert.True(await catalog.ActivateAsync(root.Id, default));
        var restored = await GetAsync(store, root.Id);
        Assert.Equal(SessionStatus.Active, restored.Status);
        Assert.Null(restored.DeletedAt);
        foreach (var id in new[] { child.Id, grandchild.Id })
        {
            var descendant = await GetAsync(store, id);
            Assert.Equal(SessionStatus.Archived, descendant.Status);
            Assert.Null(descendant.DeletedAt);
        }

        Assert.True(await catalog.ActivateAsync(root.Id, default)); // already Active: no-op
        Assert.False(await catalog.ActivateAsync("missing", default));
    }

    // ---- Handover ----

    [Fact]
    public async Task Handover_to_a_new_session_archives_the_old_one_and_writes_initial_messages_in_the_same_transaction()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var worktree = directory.Combine("wt");
        var old = await store.CreateAsync(
            Root(worktree, "old") with { ProjectId = "p", Branch = "forge/x", ForkOfBranch = "main", ForkBaseSha = "abc123", LineageRootId = "lineage0" },
            default);
        await store.AppendAsync(old.Id, Text("old history"), null, default);

        var target = new HandoverTarget.NewSession(
            Root(worktree, "new") with
            {
                ProjectId = "p",
                Branch = "forge/x",
                ForkOfBranch = "ignored",
                ForkBaseSha = "ignored",
                LineageRootId = "ignored",
                InheritsFromSessionId = old.Id,
                InheritsUpToSeq = 1,
                Origin = SessionOrigin.Full,
            })
        {
            InitialMessages = [Text("handover zebrafish summary"), Text("inheritance note")],
        };
        var created = await catalog.HandoverAsync(old.Id, target, default);

        Assert.Equal(SessionStatus.Archived, (await GetAsync(store, old.Id)).Status);
        var loaded = await GetAsync(store, created.Id);
        Assert.Equal(created, loaded);
        Assert.Equal(SessionStatus.Active, loaded.Status);
        Assert.Equal("main", loaded.ForkOfBranch);
        Assert.Equal("abc123", loaded.ForkBaseSha);
        Assert.Equal("lineage0", loaded.LineageRootId);
        Assert.Equal(old.Id, loaded.InheritsFromSessionId);
        Assert.Equal(1L, loaded.InheritsUpToSeq);
        Assert.Equal(SessionOrigin.Full, loaded.Origin);

        var messages = await CollectAsync(store.ReadAsync(created.Id, 1, null, default));
        Assert.Equal([1L, 2L], messages.Select(static m => m.Seq));
        Assert.Equal(2, await store.LastSeqAsync(created.Id, default));
        Assert.Equal(3, await store.AppendAsync(created.Id, Text("next"), null, default));
        var hit = Assert.Single(await store.SearchAsync(created.Id, "zebrafish", 5, default));
        Assert.Equal(1, hit.Seq);
    }

    [Fact]
    public async Task Handover_copies_todos_groups_and_plan_with_new_ids_only_when_asked()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        await using var todos = new SqliteTodoStore(directory.Path);
        var catalog = Catalog(store);
        var worktree = directory.Combine("wt");
        var old = await store.CreateAsync(Root(worktree), default);
        var group = await todos.FindOrCreateGroupAsync(old.Id, "Plan A", default);
        var step = await todos.AddAsync(new TodoAddRequest(old.Id, "step 1", "n", GroupId: group.Id), default);
        var loose = await todos.AddAsync(new TodoAddRequest(old.Id, "loose", Origin: TodoOrigin.User), default);
        await todos.SetPlanSummaryAsync(old.Id, "## Summary", default);

        var copied = await catalog.HandoverAsync(
            old.Id,
            new HandoverTarget.NewSession(Root(worktree)) { CopyTodosAndPlans = true },
            default);

        var newGroup = Assert.Single(await todos.ListGroupsAsync(copied.Id, default));
        Assert.NotEqual(group.Id, newGroup.Id);
        Assert.Equal("Plan A", newGroup.Title);
        var items = await todos.ListAsync(copied.Id, default);
        Assert.Equal(2, items.Count);
        var newStep = Assert.Single(items, static i => i.Title == "step 1");
        Assert.Equal(newGroup.Id, newStep.GroupId);
        Assert.Equal("n", newStep.Note);
        Assert.NotEqual(step.Id, newStep.Id);
        var newLoose = Assert.Single(items, static i => i.Title == "loose");
        Assert.Null(newLoose.GroupId);
        Assert.Equal(TodoOrigin.User, newLoose.Origin);
        Assert.NotEqual(loose.Id, newLoose.Id);
        Assert.Equal("## Summary", (await todos.GetPlanSummaryAsync(copied.Id, default))?.Text);
        Assert.Equal(2, (await todos.ListAsync(old.Id, default)).Count); // the old session keeps its own

        var blank = await catalog.HandoverAsync(copied.Id, new HandoverTarget.NewSession(Root(worktree)), default);
        Assert.Empty(await todos.ListAsync(blank.Id, default));
        Assert.Empty(await todos.ListGroupsAsync(blank.Id, default));
        Assert.Null(await todos.GetPlanSummaryAsync(blank.Id, default));
    }

    [Fact]
    public async Task Handover_aborts_when_the_old_session_is_no_longer_active()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var worktree = directory.Combine("wt");
        var old = await store.CreateAsync(Root(worktree), default);
        Assert.True(await store.SetStatusAsync(old.Id, SessionStatus.Archived, default));

        var refused = await Assert.ThrowsAsync<SessionNotActiveException>(() => catalog.HandoverAsync(
            old.Id,
            new HandoverTarget.NewSession(Root(worktree)) { InitialMessages = [Text("x")] },
            default));
        Assert.Equal(old.Id, refused.SessionId);
        Assert.Equal(1L, await CountAsync(store, "SELECT count(*) FROM sessions"));
        Assert.Equal(0L, await CountAsync(store, "SELECT count(*) FROM messages"));
        await Assert.ThrowsAsync<SessionNotActiveException>(() => catalog.HandoverAsync("missing", new HandoverTarget.NewSession(Root(worktree)), default));
    }

    [Theory]
    [InlineData("plan_summaries")]
    [InlineData("messages")]
    public async Task Handover_rolls_back_entirely_when_a_sub_step_fails(string failingTable)
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        await using var todos = new SqliteTodoStore(directory.Path);
        var catalog = Catalog(store);
        var worktree = directory.Combine("wt");
        var old = await store.CreateAsync(Root(worktree), default);
        await store.AppendAsync(old.Id, Text("history"), null, default);
        await todos.AddAsync(old.Id, "todo", null, null, default);
        await todos.SetPlanSummaryAsync(old.Id, "plan", default);

        // An injected failure in a late sub-step: any insert into the table for a session other than the old one aborts.
        await ExecuteAsync(
            RawConnection(store),
            $"CREATE TRIGGER inject_failure BEFORE INSERT ON {failingTable} WHEN NEW.session_id <> '{old.Id}' BEGIN SELECT RAISE(ABORT, 'injected'); END;");

        var failure = await Assert.ThrowsAsync<SqliteException>(() => catalog.HandoverAsync(
            old.Id,
            new HandoverTarget.NewSession(Root(worktree)) { InitialMessages = [Text("summary")], CopyTodosAndPlans = true },
            default));
        Assert.Contains("injected", failure.Message, StringComparison.Ordinal);

        Assert.Equal(SessionStatus.Active, (await GetAsync(store, old.Id)).Status);
        Assert.Equal(1L, await CountAsync(store, "SELECT count(*) FROM sessions"));
        Assert.Equal(1L, await CountAsync(store, "SELECT count(*) FROM messages"));
        Assert.Equal(1L, await CountAsync(store, "SELECT count(*) FROM todos"));
        Assert.Equal(1L, await CountAsync(store, "SELECT count(*) FROM plan_summaries"));
        Assert.Equal(2, await store.AppendAsync(old.Id, Text("still writable"), null, default));

        await ExecuteAsync(RawConnection(store), "DROP TRIGGER inject_failure;");
        var retried = await catalog.HandoverAsync(
            old.Id,
            new HandoverTarget.NewSession(Root(worktree)) { InitialMessages = [Text("summary")] },
            default);
        Assert.Equal(1, await store.LastSeqAsync(retried.Id, default));
    }

    [Fact]
    public async Task Handover_to_a_held_key_is_refused_and_leaves_the_old_session_active()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var old = await store.CreateAsync(Root(directory.Combine("a")), default);
        var holder = await store.CreateAsync(Root(directory.Combine("b")), default);

        var refused = await Assert.ThrowsAsync<WorktreeOccupiedException>(() => catalog.HandoverAsync(
            old.Id,
            new HandoverTarget.NewSession(Root(directory.Combine("b"))) { InitialMessages = [Text("x")] },
            default));
        Assert.Equal(holder.Id, refused.HolderSessionId);
        Assert.Equal(SessionStatus.Active, (await GetAsync(store, old.Id)).Status);
        Assert.Equal(2L, await CountAsync(store, "SELECT count(*) FROM sessions"));
        Assert.Equal(0L, await CountAsync(store, "SELECT count(*) FROM messages"));
    }

    [Fact]
    public async Task Resume_changes_only_status_and_deleted_at()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var worktree = directory.Combine("wt");
        var earlier = await store.CreateAsync(Root(worktree, "earlier") with { Origin = SessionOrigin.Summary }, default);
        await store.AppendAsync(earlier.Id, Text("earlier history"), null, default);
        Assert.Equal(1, await catalog.SoftDeleteAsync([earlier.Id], default));
        var current = await store.CreateAsync(Root(worktree, "current"), default);
        await store.AppendAsync(current.Id, Text("current history"), null, default);
        var before = await GetAsync(store, earlier.Id);

        var resumed = await catalog.HandoverAsync(current.Id, new HandoverTarget.Resume(earlier.Id), default);

        Assert.Equal(before with { Status = SessionStatus.Active, DeletedAt = null }, resumed);
        Assert.Equal(resumed, await GetAsync(store, earlier.Id));
        Assert.Equal(SessionStatus.Archived, (await GetAsync(store, current.Id)).Status);
        Assert.Equal(1L, await CountAsync(store, "SELECT count(*) FROM messages WHERE session_id = $id", ("$id", earlier.Id)));
        Assert.Equal(2L, await CountAsync(store, "SELECT count(*) FROM sessions"));
    }

    [Fact]
    public async Task Resume_of_a_missing_or_active_session_rolls_back_and_keeps_the_old_one_active()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var current = await store.CreateAsync(Root(directory.Combine("a")), default);
        var elsewhere = await store.CreateAsync(Root(directory.Combine("b")), default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.HandoverAsync(current.Id, new HandoverTarget.Resume("missing"), default));
        Assert.Equal(SessionStatus.Active, (await GetAsync(store, current.Id)).Status);

        var active = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.HandoverAsync(current.Id, new HandoverTarget.Resume(elsewhere.Id), default));
        Assert.IsNotType<WorktreeOccupiedException>(active);
        Assert.Equal(SessionStatus.Active, (await GetAsync(store, current.Id)).Status);
        Assert.Equal(SessionStatus.Active, (await GetAsync(store, elsewhere.Id)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.HandoverAsync(current.Id, new HandoverTarget.Resume(current.Id), default));
    }

    // ---- Soft delete, history, regular queries ----

    [Fact]
    public async Task Soft_delete_cascades_to_descendants_and_regular_queries_hide_deleted_sessions()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var worktree = directory.Combine("wt");
        var root = await store.CreateAsync(Root(worktree) with { ProjectId = "p" }, default);
        var child = await store.CreateAsync(Root(worktree) with { ProjectId = "p", ParentSessionId = root.Id }, default);
        var grandchild = await store.CreateAsync(Root(worktree) with { ProjectId = "p", ParentSessionId = child.Id }, default);
        var archived = await store.CreateAsync(Root(directory.Combine("other")) with { ProjectId = "p" }, default);
        Assert.True(await store.SetStatusAsync(archived.Id, SessionStatus.Archived, default));
        var kept = await store.CreateAsync(Root(directory.Combine("kept")) with { ProjectId = "p" }, default);

        Assert.Equal(3, await catalog.SoftDeleteAsync([root.Id], default));
        var deleted = new[] { root.Id, child.Id, grandchild.Id };
        var stamps = new List<DateTimeOffset>();
        foreach (var id in deleted)
        {
            var session = await GetAsync(store, id);
            Assert.Equal(SessionStatus.Deleted, session.Status);
            stamps.Add(Assert.NotNull(session.DeletedAt));
        }

        Assert.Equal(
            new[] { archived.Id, kept.Id }.Order(StringComparer.Ordinal),
            (await catalog.QueryAsync(new SessionQuery { ProjectId = "p" }, default)).Select(static s => s.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { archived.Id, kept.Id }.Order(StringComparer.Ordinal),
            (await store.ListSessionsAsync(null, default)).Select(static s => s.Id).Order(StringComparer.Ordinal));
        Assert.Empty(await store.ListSessionsAsync(worktree, default));
        Assert.Equal(
            deleted.Order(StringComparer.Ordinal),
            (await catalog.QueryAsync(new SessionQuery { Status = SessionStatus.Deleted }, default)).Select(static s => s.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            new[] { root.Id, child.Id, grandchild.Id, archived.Id }.Order(StringComparer.Ordinal),
            (await catalog.ListHistoryAsync(new SessionQuery { ProjectId = "p", Status = SessionStatus.Active }, default))
                .Select(static s => s.Id).Order(StringComparer.Ordinal));
        Assert.Equal(
            [root.Id],
            (await catalog.ListHistoryAsync(new SessionQuery { ParentSessionId = string.Empty, ProjectId = "p" }, default))
                .Where(static s => s.Status == SessionStatus.Deleted).Select(static s => s.Id));

        // A second delete changes nothing and keeps the original stamps; the freed key takes a new root session.
        Assert.Equal(0, await catalog.SoftDeleteAsync([root.Id, child.Id], default));
        for (var index = 0; index < deleted.Length; index++)
        {
            Assert.Equal(stamps[index], (await GetAsync(store, deleted[index])).DeletedAt);
        }

        await store.CreateAsync(Root(worktree), default);
        Assert.Equal(0, await catalog.SoftDeleteAsync([], default));
    }

    // ---- Purge ----

    [Fact]
    public async Task Purge_removes_every_row_of_the_session_and_its_descendants_and_nothing_else()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        await using var todos = new SqliteTodoStore(directory.Path);
        await using var checkpoints = new SqliteTurnCheckpointStore(directory.Path);
        var catalog = Catalog(store);
        var root = await store.CreateAsync(Root(directory.Combine("wt")), default);
        var child = await store.CreateAsync(Root(directory.Combine("wt")) with { ParentSessionId = root.Id }, default);
        var other = await store.CreateAsync(Root(directory.Combine("other")), default);
        foreach (var id in new[] { root.Id, child.Id, other.Id })
        {
            await store.AppendAsync(id, Text("quokka " + id), null, default);
            await store.PutSummaryAsync(new SummaryRecord(id, 1, 1, 1, "summary", DateTimeOffset.UtcNow), default);
            var group = await todos.FindOrCreateGroupAsync(id, "plan", default);
            await todos.AddAsync(new TodoAddRequest(id, "step", GroupId: group.Id), default);
            await todos.SetPlanSummaryAsync(id, "plan text", default);
            await checkpoints.BeginAsync(id, 1, directory.Combine("wt"), "tree", DateTimeOffset.UtcNow, default);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.PurgeAsync(root.Id, default));
        Assert.Equal(2, await catalog.SoftDeleteAsync([root.Id], default));

        var purged = await catalog.PurgeAsync(root.Id, default);
        Assert.Equal([root.Id, child.Id], purged);

        var search = store.Fts5Available ? "messages_fts" : "messages_text";
        foreach (var table in new[] { "messages", search, "summaries", "todos", "todo_groups", "plan_summaries", "turn_checkpoints" })
        {
            Assert.Equal(0L, await CountAsync(store, $"SELECT count(*) FROM {table} WHERE session_id IN ($a, $b)", ("$a", root.Id), ("$b", child.Id)));
            Assert.Equal(1L, await CountAsync(store, $"SELECT count(*) FROM {table} WHERE session_id = $o", ("$o", other.Id)));
        }

        Assert.Equal(0L, await CountAsync(store, "SELECT count(*) FROM sessions WHERE id IN ($a, $b)", ("$a", root.Id), ("$b", child.Id)));
        Assert.Null(await store.GetSessionAsync(root.Id, default));
        Assert.Empty(await store.SearchAsync(root.Id, "quokka", 5, default));
        Assert.Single(await store.SearchAsync(other.Id, "quokka", 5, default));
        Assert.Equal(0, await store.LastSeqAsync(root.Id, default));
        Assert.Empty(await catalog.PurgeAsync(root.Id, default)); // already gone
    }

    [Fact]
    public async Task Purge_is_refused_while_a_session_outside_the_purged_set_inherits_from_it()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var parent = await store.CreateAsync(Root(directory.Combine("a")), default);
        await store.AppendAsync(parent.Id, Text("history"), null, default);
        // A child inheriting from its own root is inside the purged set and does not block.
        await store.CreateAsync(Root(directory.Combine("a")) with { ParentSessionId = parent.Id, InheritsFromSessionId = parent.Id }, default);
        var heir = await store.CreateAsync(
            Root(directory.Combine("b")) with { InheritsFromSessionId = parent.Id, InheritsUpToSeq = 1, Origin = SessionOrigin.Full },
            default);
        Assert.Equal(2, await catalog.SoftDeleteAsync([parent.Id], default));

        var refused = await Assert.ThrowsAsync<SessionPurgeRefusedException>(() => catalog.PurgeAsync(parent.Id, default));
        Assert.Equal([heir.Id], refused.InheritingSessionIds);
        Assert.Equal(SessionStatus.Deleted, (await GetAsync(store, parent.Id)).Status);
        Assert.Equal(1L, await CountAsync(store, "SELECT count(*) FROM messages WHERE session_id = $id", ("$id", parent.Id)));

        // Purging the heir first lifts the guard.
        Assert.Equal(1, await catalog.SoftDeleteAsync([heir.Id], default));
        Assert.Equal([heir.Id], await catalog.PurgeAsync(heir.Id, default));
        Assert.Equal(2, (await catalog.PurgeAsync(parent.Id, default)).Count);
    }

    // ---- Self-heal and notices ----

    [Fact]
    public async Task Heal_soft_deletes_roots_with_missing_directories_only_when_the_project_root_exists()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        await using var projects = new SqliteProjectRegistry(directory.Path);
        var catalog = Catalog(store);
        var online = await projects.AddAsync(directory.Combine("online"), "online", ProjectKind.Repository, default);
        var offline = await projects.AddAsync(directory.Combine("offline"), "offline", ProjectKind.Repository, default);

        var gone = await store.CreateAsync(Root(online.RootPath) with { ProjectId = online.Id, WorktreePath = directory.Combine("online.worktrees", "gone") }, default);
        var goneChild = await store.CreateAsync(Root(online.RootPath) with { ProjectId = online.Id, ParentSessionId = gone.Id }, default);
        var goneArchived = await store.CreateAsync(Root(directory.Combine("online.worktrees", "old")) with { ProjectId = online.Id }, default);
        Assert.True(await store.SetStatusAsync(goneArchived.Id, SessionStatus.Archived, default));
        var present = await store.CreateAsync(Root(directory.Combine("online.worktrees", "here")) with { ProjectId = online.Id }, default);
        var offlineSession = await store.CreateAsync(Root(directory.Combine("offline.worktrees", "x")) with { ProjectId = offline.Id }, default);
        var unregistered = await store.CreateAsync(Root(directory.Combine("loose")) with { ProjectId = "no-such-project" }, default);

        var existing = new HashSet<string>(StringComparer.Ordinal) { online.RootPath, directory.Combine("online.worktrees", "here") };
        var probed = new List<string>();
        bool DirectoryExists(string path)
        {
            probed.Add(path);
            return existing.Contains(path);
        }

        var healed = await catalog.HealMissingDirectoriesAsync(DirectoryExists, existing.Contains, default);

        Assert.Equal(new[] { gone.Id, goneArchived.Id }.Order(StringComparer.Ordinal), healed);
        foreach (var id in new[] { gone.Id, goneChild.Id, goneArchived.Id })
        {
            var session = await GetAsync(store, id);
            Assert.Equal(SessionStatus.Deleted, session.Status);
            Assert.NotNull(session.DeletedAt);
        }

        foreach (var id in new[] { present.Id, offlineSession.Id, unregistered.Id })
        {
            Assert.Equal(SessionStatus.Active, (await GetAsync(store, id)).Status);
        }

        Assert.DoesNotContain(directory.Combine("offline.worktrees", "x"), probed);
        var notice = Assert.Single(await catalog.ReadNoticesAsync(default));
        Assert.Equal(StorageNoticeKinds.HealDeleted, notice.Kind);
        Assert.Equal(healed, notice.SessionIds);

        // Nothing left to heal: no change, no second notice.
        Assert.Empty(await catalog.HealMissingDirectoriesAsync(DirectoryExists, existing.Contains, default));
        Assert.Single(await catalog.ReadNoticesAsync(default));
    }

    [Fact]
    public async Task Notices_are_read_oldest_first_until_acknowledged()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        await using var projects = new SqliteProjectRegistry(directory.Path);
        Assert.Empty(await catalog.ReadNoticesAsync(default));
        var project = await projects.AddAsync(directory.Combine("repo"), "repo", ProjectKind.Repository, default);
        var a = await store.CreateAsync(Root(directory.Combine("a")) with { ProjectId = project.Id }, default);
        var b = await store.CreateAsync(Root(directory.Combine("b")) with { ProjectId = project.Id }, default);

        await catalog.HealMissingDirectoriesAsync(path => path != directory.Combine("a"), static _ => true, default);
        await catalog.HealMissingDirectoriesAsync(static _ => false, static _ => true, default);
        var notices = await catalog.ReadNoticesAsync(default);
        Assert.Equal(2, notices.Count);
        Assert.True(notices[0].Id < notices[1].Id);
        Assert.Equal([a.Id], notices[0].SessionIds);
        Assert.Equal([b.Id], notices[1].SessionIds);

        await catalog.AcknowledgeNoticesAsync([notices[0].Id], default);
        Assert.Equal([notices[1].Id], (await catalog.ReadNoticesAsync(default)).Select(static n => n.Id));
        await catalog.AcknowledgeNoticesAsync([], default);
        await catalog.AcknowledgeNoticesAsync([notices[1].Id, 999], default);
        Assert.Empty(await catalog.ReadNoticesAsync(default));
    }

    // ---- Fingerprint, branch rename, new fields ----

    [Fact]
    public async Task Prefix_fingerprint_is_written_only_when_it_changes()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var session = await store.CreateAsync(Root(directory.Combine("a")), default);
        Assert.Null(session.PrefixFingerprint);

        Assert.True(await catalog.SetPrefixFingerprintAsync(session.Id, "h1", default));
        Assert.False(await catalog.SetPrefixFingerprintAsync(session.Id, "h1", default));
        Assert.True(await catalog.SetPrefixFingerprintAsync(session.Id, "h2", default));
        Assert.Equal("h2", (await GetAsync(store, session.Id)).PrefixFingerprint);
        Assert.False(await catalog.SetPrefixFingerprintAsync("missing", "h1", default));
    }

    [Fact]
    public async Task Rename_branch_updates_the_worktree_sessions_and_fork_pointers_of_the_project_only()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var catalog = Catalog(store);
        var worktree = directory.Combine("wt");
        var archived = await store.CreateAsync(Root(worktree) with { ProjectId = "p", Branch = "forge/old" }, default);
        Assert.True(await store.SetStatusAsync(archived.Id, SessionStatus.Archived, default));
        var active = await store.CreateAsync(Root(worktree) with { ProjectId = "p", Branch = "forge/old" }, default);
        var fork = await store.CreateAsync(Root(directory.Combine("fork")) with { ProjectId = "p", Branch = "forge/f", ForkOfBranch = "forge/old" }, default);
        var otherProject = await store.CreateAsync(
            Root(directory.Combine("q")) with { ProjectId = "q", Branch = "forge/old", ForkOfBranch = "forge/old" },
            default);

        var changed = await catalog.RenameBranchAsync("p", worktree + Path.DirectorySeparatorChar, "forge/old", "forge/new", default);

        Assert.Equal(3, changed);
        Assert.Equal("forge/new", (await GetAsync(store, archived.Id)).Branch);
        Assert.Equal("forge/new", (await GetAsync(store, active.Id)).Branch);
        Assert.Equal("forge/new", (await GetAsync(store, fork.Id)).ForkOfBranch);
        Assert.Equal("forge/f", (await GetAsync(store, fork.Id)).Branch);
        var untouched = await GetAsync(store, otherProject.Id);
        Assert.Equal("forge/old", untouched.Branch);
        Assert.Equal("forge/old", untouched.ForkOfBranch);
    }

    [Fact]
    public async Task Create_round_trips_lineage_inheritance_and_origin()
    {
        using var directory = new TempDirectory();
        await using var store = new SqliteSessionStore(directory.Path);
        var plain = await store.CreateAsync(Root(directory.Combine("a")), default);
        Assert.Equal(plain.Id, plain.LineageRootId);
        Assert.Equal(SessionOrigin.Cold, plain.Origin);
        Assert.Equal(plain, await GetAsync(store, plain.Id));

        var forked = await store.CreateAsync(
            Root(directory.Combine("b")) with
            {
                ForkOfBranch = "main",
                ForkBaseSha = "deadbeef",
                LineageRootId = plain.Id,
                InheritsFromSessionId = plain.Id,
                InheritsUpToSeq = 7,
                Origin = SessionOrigin.Blank,
            },
            default);
        var loaded = await GetAsync(store, forked.Id);
        Assert.Equal(forked, loaded);
        Assert.Equal(plain.Id, loaded.LineageRootId);
        Assert.Equal(7L, loaded.InheritsUpToSeq);
        Assert.Equal(SessionOrigin.Blank, loaded.Origin);
        Assert.Equal("blank", await TextAsync(store, "SELECT origin_kind FROM sessions WHERE id = $id", forked.Id));

        // A row written before v9 bookkeeping (NULL lineage) reads its own id as the lineage root.
        await ExecuteAsync(RawConnection(store), $"UPDATE sessions SET lineage_root_id = NULL WHERE id = '{plain.Id}';");
        Assert.Equal(plain.Id, (await GetAsync(store, plain.Id)).LineageRootId);
    }
}
