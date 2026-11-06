using System.Text.Json;
using Forge.Abstractions;
using Forge.App.Approval;
using Forge.Core.Tools;
using Xunit;

namespace Forge.App.Tests.Approval;

public sealed class ApprovalRulesTests
{
    private static string RulesPath(string dir) => Path.Combine(dir, ApprovalRules.FileName);

    private static ApprovalRules CreateRules(string dir, int debounceMilliseconds = 0) =>
        new(RulesPath(dir), debounceMilliseconds);

    [Fact]
    public async Task Session_project_and_global_scopes_each_match_their_own_scope()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-hit-");
        try
        {
            await using var rules = CreateRules(dir);
            Assert.True(rules.Allow(ApprovalRuleKey.ForSession("shell_run", ToolSideEffect.Execute, "s1")));
            Assert.True(rules.Allow(ApprovalRuleKey.ForProject("shell_run", ToolSideEffect.Execute, "p1")));
            Assert.True(rules.Allow(ApprovalRuleKey.Global("read_file", ToolSideEffect.ReadOnly)));

            Assert.True(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s1", "p1"));
            Assert.True(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "other-session", "p1"));
            Assert.True(rules.IsAllowed("read_file", ToolSideEffect.ReadOnly, "any", "any"));
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Rules_do_not_match_another_session_project_or_side_effect()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-miss-");
        try
        {
            await using var rules = CreateRules(dir);
            Assert.True(rules.Allow(ApprovalRuleKey.ForSession("shell_run", ToolSideEffect.Execute, "s1")));

            // A session rule follows its session into any project, but never into another session.
            Assert.True(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s1", "p2"));
            Assert.False(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s2", "p1"));
            Assert.False(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s2", null));
            Assert.False(rules.IsAllowed("shell_run", ToolSideEffect.Write, "s1", "p1"));
            Assert.False(rules.IsAllowed("other_tool", ToolSideEffect.Execute, "s1", "p1"));

            Assert.True(rules.Allow(ApprovalRuleKey.ForProject("shell_run", ToolSideEffect.Execute, "p1")));

            // A project rule follows its project into any session, but never into another project or a missing one.
            Assert.True(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s2", "p1"));
            Assert.False(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s2", "p2"));
            Assert.False(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s2", null));
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Extension_delete_can_never_be_remembered()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-delete-");
        try
        {
            await using var rules = CreateRules(dir);
            Assert.False(ApprovalRules.IsRememberable(ExtensionToolNames.Delete));
            Assert.False(rules.Allow(ApprovalRuleKey.ForSession(ExtensionToolNames.Delete, ToolSideEffect.Execute, "s1")));
            Assert.False(rules.Allow(ApprovalRuleKey.ForProject(ExtensionToolNames.Delete, ToolSideEffect.Execute, "p1")));
            Assert.False(rules.Allow(ApprovalRuleKey.Global(ExtensionToolNames.Delete, ToolSideEffect.Execute)));
            Assert.False(rules.IsAllowed(ExtensionToolNames.Delete, ToolSideEffect.Execute, "s1", "p1"));
            Assert.Empty(rules.Rules);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Round_trip_persists_project_and_global_rules_but_not_session_rules()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-roundtrip-");
        try
        {
            await using (var rules = CreateRules(dir))
            {
                Assert.True(rules.Allow(ApprovalRuleKey.ForSession("shell_run", ToolSideEffect.Execute, "s1")));
                Assert.True(rules.Allow(ApprovalRuleKey.ForProject("shell_run", ToolSideEffect.Execute, "p1")));
                Assert.True(rules.Allow(ApprovalRuleKey.Global("read_file", ToolSideEffect.ReadOnly)));
                await rules.SaveAsync(CancellationToken.None);
            }

            var raw = await File.ReadAllTextAsync(RulesPath(dir));
            Assert.Contains("shell_run", raw);
            Assert.DoesNotContain("\"s1\"", raw);

            await using var reloaded = CreateRules(dir);
            await reloaded.LoadAsync(CancellationToken.None);
            Assert.Null(reloaded.LoadProblem);
            // Without a project the project rule cannot match, so only the (unpersisted) session rule could allow this.
            Assert.False(reloaded.IsAllowed("shell_run", ToolSideEffect.Execute, "s1", null));
            Assert.True(reloaded.IsAllowed("shell_run", ToolSideEffect.Execute, "other", "p1"));
            Assert.True(reloaded.IsAllowed("read_file", ToolSideEffect.ReadOnly, "any", null));
            Assert.DoesNotContain(reloaded.Rules, static rule => rule.Key.Scope == ApprovalScope.Session);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Corrupt_file_reports_a_problem_stays_empty_and_backs_up_only_once()
    {
        const string garbage = "this is not json {{{";
        var dir = ApprovalFixtures.TempDir("forge-rules-corrupt-");
        try
        {
            var path = RulesPath(dir);
            await File.WriteAllTextAsync(path, garbage);

            await using var rules = CreateRules(dir);
            await rules.LoadAsync(CancellationToken.None);

            Assert.NotNull(rules.LoadProblem);
            Assert.Empty(rules.Rules);
            Assert.False(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s1", "p1"));

            // A rule granted while the file was broken is kept in memory; the first write saves it and backs up once.
            Assert.True(rules.Allow(ApprovalRuleKey.Global("shell_run", ToolSideEffect.Execute)));
            await rules.SaveAsync(CancellationToken.None);

            var backup = path + ApprovalRules.BackupSuffix;
            Assert.True(File.Exists(backup));
            Assert.Equal(garbage, await File.ReadAllTextAsync(backup));

            // The file is healthy again, so the next write must not touch the backup.
            Assert.True(rules.Allow(ApprovalRuleKey.Global("read_file", ToolSideEffect.ReadOnly)));
            await rules.SaveAsync(CancellationToken.None);
            Assert.Equal(garbage, await File.ReadAllTextAsync(backup));

            await using var healed = CreateRules(dir);
            await healed.LoadAsync(CancellationToken.None);
            Assert.Null(healed.LoadProblem);
            Assert.Equal(2, healed.Rules.Count);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Unknown_version_is_treated_like_a_corrupt_file()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-version-");
        try
        {
            await File.WriteAllTextAsync(RulesPath(dir), "{\"version\":999,\"rules\":[]}");

            await using var rules = CreateRules(dir);
            await rules.LoadAsync(CancellationToken.None);

            Assert.NotNull(rules.LoadProblem);
            Assert.Empty(rules.Rules);

            Assert.True(rules.Allow(ApprovalRuleKey.Global("shell_run", ToolSideEffect.Execute)));
            await rules.SaveAsync(CancellationToken.None);
            Assert.True(File.Exists(RulesPath(dir) + ApprovalRules.BackupSuffix));

            await using var healed = CreateRules(dir);
            await healed.LoadAsync(CancellationToken.None);
            Assert.Null(healed.LoadProblem);
            Assert.Single(healed.Rules);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task A_hand_edited_extension_delete_rule_is_ignored_on_load()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-handedit-");
        try
        {
            var json = "{\"version\":1,\"rules\":["
                + "{\"tool\":\"" + ExtensionToolNames.Delete + "\",\"sideEffect\":\"Execute\",\"scope\":\"Global\",\"scopeId\":\"\",\"createdAt\":\"2026-01-01T00:00:00Z\"},"
                + "{\"tool\":\"shell_run\",\"sideEffect\":\"Execute\",\"scope\":\"Global\",\"scopeId\":\"\",\"createdAt\":\"2026-01-01T00:00:00Z\"}]}";
            await File.WriteAllTextAsync(RulesPath(dir), json);

            await using var rules = CreateRules(dir);
            await rules.LoadAsync(CancellationToken.None);

            Assert.False(rules.IsAllowed(ExtensionToolNames.Delete, ToolSideEffect.Execute, "s1", "p1"));
            Assert.True(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s1", "p1"));
            Assert.Equal("shell_run", Assert.Single(rules.Rules).Key.ToolName);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Debounce_merges_changes_into_one_write_and_save_forces_it()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-debounce-");
        try
        {
            await using var rules = CreateRules(dir, debounceMilliseconds: 60_000);
            Assert.True(rules.Allow(ApprovalRuleKey.ForProject("tool_a", ToolSideEffect.Execute, "p1")));
            Assert.True(rules.Allow(ApprovalRuleKey.ForProject("tool_b", ToolSideEffect.Write, "p1")));
            Assert.True(rules.Allow(ApprovalRuleKey.Global("tool_c", ToolSideEffect.ReadOnly)));

            Assert.True(rules.IsDirty);
            Assert.False(File.Exists(RulesPath(dir)));

            await rules.SaveAsync(CancellationToken.None);

            Assert.False(rules.IsDirty);
            var document = JsonDocument.Parse(await File.ReadAllTextAsync(RulesPath(dir)));
            Assert.Equal(3, document.RootElement.GetProperty("rules").GetArrayLength());
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Session_only_changes_never_mark_the_store_dirty()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-session-");
        try
        {
            await using var rules = CreateRules(dir, debounceMilliseconds: 60_000);
            Assert.True(rules.Allow(ApprovalRuleKey.ForSession("shell_run", ToolSideEffect.Execute, "s1")));
            Assert.False(rules.IsDirty);

            await rules.SaveAsync(CancellationToken.None);
            var document = JsonDocument.Parse(await File.ReadAllTextAsync(RulesPath(dir)));
            Assert.Equal(0, document.RootElement.GetProperty("rules").GetArrayLength());
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Remove_by_tool_names_spans_every_scope()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-remove-");
        try
        {
            await using var rules = CreateRules(dir);
            var changed = 0;
            rules.Changed += () => changed++;
            Assert.True(rules.Allow(ApprovalRuleKey.ForSession("shell_run", ToolSideEffect.Execute, "s1")));
            Assert.True(rules.Allow(ApprovalRuleKey.ForProject("shell_run", ToolSideEffect.Execute, "p1")));
            Assert.True(rules.Allow(ApprovalRuleKey.Global("shell_run", ToolSideEffect.Execute)));
            Assert.True(rules.Allow(ApprovalRuleKey.Global("other_tool", ToolSideEffect.Execute)));
            var before = changed;

            Assert.Equal(3, rules.RemoveByToolNames(["shell_run"]));
            Assert.Equal(before + 1, changed);
            Assert.False(rules.IsAllowed("shell_run", ToolSideEffect.Execute, "s1", "p1"));
            Assert.True(rules.IsAllowed("other_tool", ToolSideEffect.Execute, "s1", "p1"));
            Assert.Equal(0, rules.RemoveByToolNames(["missing_tool"]));
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Revoke_removes_exactly_one_rule()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-revoke-");
        try
        {
            await using var rules = CreateRules(dir);
            var kept = ApprovalRuleKey.Global("kept_tool", ToolSideEffect.Execute);
            var dropped = ApprovalRuleKey.Global("dropped_tool", ToolSideEffect.Execute);
            Assert.True(rules.Allow(kept));
            Assert.True(rules.Allow(dropped));

            Assert.False(rules.Revoke(ApprovalRuleKey.Global("missing_tool", ToolSideEffect.Execute)));
            Assert.True(rules.Revoke(dropped));
            Assert.False(rules.IsAllowed("dropped_tool", ToolSideEffect.Execute, "s", null));
            Assert.True(rules.IsAllowed("kept_tool", ToolSideEffect.Execute, "s", null));
            Assert.Single(rules.Rules);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Changed_fires_on_add_revoke_and_load_but_not_on_duplicates()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-changed-");
        try
        {
            await using var rules = CreateRules(dir);
            var changed = 0;
            rules.Changed += () => changed++;

            var key = ApprovalRuleKey.Global("shell_run", ToolSideEffect.Execute);
            Assert.True(rules.Allow(key));
            Assert.False(rules.Allow(key));
            Assert.Equal(1, changed);

            Assert.True(rules.Revoke(key));
            Assert.Equal(2, changed);

            await rules.LoadAsync(CancellationToken.None);
            Assert.Equal(3, changed);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    // 回归:并发添加持久化规则曾因在锁外读取已被 Dispose 的 CTS 的 Token 而抛 ObjectDisposedException。
    [Fact]
    public async Task Concurrent_adds_never_lose_a_rule()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-concurrent-");
        try
        {
            await using var rules = CreateRules(dir);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new List<Task>();
            for (var index = 0; index < 32; index++)
            {
                var name = "tool_" + index;
                tasks.Add(Task.Run(
                    async () =>
                    {
                        await gate.Task;
                        rules.Allow(ApprovalRuleKey.Global(name, ToolSideEffect.Execute));
                    }));
            }

            gate.SetResult();
            await Task.WhenAll(tasks);

            Assert.Equal(32, rules.Rules.Count);
            Assert.True(rules.IsAllowed("tool_17", ToolSideEffect.Execute, "s", null));
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task Concurrent_duplicate_adds_keep_exactly_one_rule()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-dedupe-");
        try
        {
            await using var rules = CreateRules(dir);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = new List<Task<bool>>();
            for (var index = 0; index < 16; index++)
            {
                tasks.Add(Task.Run(
                    async () =>
                    {
                        await gate.Task;
                        return rules.Allow(ApprovalRuleKey.Global("shell_run", ToolSideEffect.Execute));
                    }));
            }

            gate.SetResult();
            var results = await Task.WhenAll(tasks);

            Assert.Single(results, static added => added);
            Assert.Single(rules.Rules);
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }

    [Fact]
    public async Task File_format_is_versioned_and_readable()
    {
        var dir = ApprovalFixtures.TempDir("forge-rules-format-");
        try
        {
            await using (var rules = CreateRules(dir))
            {
                Assert.True(rules.Allow(ApprovalRuleKey.ForProject("shell_run", ToolSideEffect.Execute, "p1")));
                await rules.SaveAsync(CancellationToken.None);
            }

            var raw = await File.ReadAllTextAsync(RulesPath(dir));
            Assert.Contains("\"version\": 1", raw);
            Assert.Contains("\"tool\":", raw);
            Assert.Contains("\"sideEffect\":", raw);
            Assert.Contains("\"scope\":", raw);
            Assert.Contains("\"scopeId\":", raw);
            Assert.Contains("\"createdAt\":", raw);

            var document = JsonDocument.Parse(raw);
            Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
            var entry = document.RootElement.GetProperty("rules")[0];
            Assert.Equal("shell_run", entry.GetProperty("tool").GetString());
            Assert.Equal("Execute", entry.GetProperty("sideEffect").GetString());
            Assert.Equal("Project", entry.GetProperty("scope").GetString());
            Assert.Equal("p1", entry.GetProperty("scopeId").GetString());
        }
        finally
        {
            ApprovalFixtures.DeleteTree(dir);
        }
    }
}
