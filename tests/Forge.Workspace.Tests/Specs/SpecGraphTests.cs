using System.Diagnostics;
using Xunit;

namespace Forge.Workspace.Tests.Specs;

public sealed class SpecGraphTests
{
    private sealed class SpecWorkspace : IDisposable
    {
        private readonly TempDirectory temp = new();

        public string Root => temp.Path;

        public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

        public void Write(string relative, string content) => SpecFixture.Write(Root, relative, content);

        public void Delete(string relative) => File.Delete(Path.Combine(Root, relative));

        public SpecGraph Graph(SpecGraphOptions? options = null) => new(Root, options);

        public void Dispose() => temp.Dispose();
    }

    private static SpecWorkspace Populated()
    {
        var workspace = new SpecWorkspace();
        workspace.Write("docs/arch.md", SpecFixture.Spec("arch", "architecture-design", "status: active\n"));
        workspace.Write("docs/module-a.md", SpecFixture.Spec(
            "module-a",
            "module-design",
            "parent: arch\ndepends-on: [module-b]\ntags: [storage]\nstatus: active\n"));
        workspace.Write("docs/module-b.md", SpecFixture.Spec(
            "module-b",
            "module-design",
            "parent: arch\nreferences: [module-a]\ntags: [ui]\n"));
        workspace.Write("docs/tasks/task-1.md", SpecFixture.Spec(
            "task-1",
            "task-spec",
            "parent: module-a\nimplements: [module-a]\ncovers: [arch, ghost]\n",
            "\n# Task one\n\nTODO: wire the storage layer.\n"));
        return workspace;
    }

    private static string[] Ids(SpecGrepPage page) =>
        page.Matches.Select(static match => match.Id).Distinct().OrderBy(static id => id, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Index_skips_ignored_directories_and_non_spec_files()
    {
        using var workspace = Populated();
        workspace.Write("bin/hidden.md", SpecFixture.Spec("bin-hidden", "module-design"));
        workspace.Write("node_modules/pkg/x.md", SpecFixture.Spec("nm-hidden", "module-design"));
        workspace.Write(".git/y.md", SpecFixture.Spec("git-hidden", "module-design"));
        workspace.Write(".forge-worktrees/s/z.md", SpecFixture.Spec("wt-hidden", "module-design"));
        workspace.Write("notes/plain.md", "# just notes\n");
        workspace.Write("notes/no-type.md", "---\nid: no-type\n---\n");
        workspace.Write("notes/data.txt", SpecFixture.Spec("txt-ext", "module-design"));

        var graph = workspace.Graph();
        var ids = (await graph.ListAsync()).Select(static node => node.Id).ToArray();

        Assert.Equal(["arch", "module-a", "module-b", "task-1"], ids);
        Assert.Null(await graph.GetAsync("bin-hidden"));
        Assert.Null(await graph.GetAsync("txt-ext"));
    }

    [Fact]
    public async Task Index_uses_forward_slash_relative_paths_and_summaries()
    {
        using var workspace = Populated();

        var nodes = await workspace.Graph().ListAsync();

        var task = nodes.Single(static node => node.Id == "task-1");
        Assert.Equal("docs/tasks/task-1.md", task.Path);
        Assert.Equal("task-spec", task.Type);
        Assert.Equal("Title of task-1", task.Title);
        Assert.Null(task.Status);
    }

    [Fact]
    public async Task Oversized_spec_files_are_not_indexed()
    {
        using var workspace = Populated();
        workspace.Write("docs/huge.md", SpecFixture.Spec("huge", "module-design", body: new string('x', 4096)));

        var graph = workspace.Graph(new SpecGraphOptions { MaxFileBytes = 1024 });

        Assert.Null(await graph.GetAsync("huge"));
        Assert.NotNull(await graph.GetAsync("arch"));
    }

    [Fact]
    public async Task Missing_root_directory_indexes_nothing_and_does_not_throw()
    {
        using var workspace = new SpecWorkspace();

        var graph = new SpecGraph(Path.Combine(workspace.Root, "does-not-exist"));

        Assert.Empty(await graph.ListAsync());
        Assert.Equal(0, (await graph.RefreshAsync()).SpecsIndexed);
    }

    [Fact]
    public async Task Refresh_reports_additions_updates_and_removals()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var first = await graph.RefreshAsync();
        Assert.Equal(4, first.SpecsIndexed);
        Assert.Equal(4, first.Added);
        Assert.Equal(0, first.Updated);
        Assert.Equal(0, first.Removed);

        var unchanged = await graph.RefreshAsync();
        Assert.Equal(0, unchanged.Added + unchanged.Updated + unchanged.Removed);
        Assert.Equal(4, unchanged.SpecsIndexed);

        workspace.Write("docs/new.md", SpecFixture.Spec("new-one", "module-design"));
        workspace.Delete("docs/module-b.md");
        var changed = await graph.RefreshAsync();
        Assert.Equal(1, changed.Added);
        Assert.Equal(1, changed.Removed);
        Assert.Equal(4, changed.SpecsIndexed);
    }

    [Fact]
    public async Task Refresh_picks_up_edited_content_incrementally()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();
        await graph.RefreshAsync();
        Assert.Equal("Title of arch", (await graph.GetAsync("arch"))!.Node.Title);

        workspace.Write("docs/arch.md", SpecFixture.Spec("arch", "architecture-design", "status: active\n").Replace("Title of arch", "Architecture v2 is much longer", StringComparison.Ordinal));
        var result = await graph.RefreshAsync();

        Assert.Equal(1, result.Updated);
        Assert.Equal("Architecture v2 is much longer", (await graph.GetAsync("arch"))!.Node.Title);
    }

    [Fact]
    public async Task Queries_see_changes_only_after_refresh()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();
        await graph.RefreshAsync();

        workspace.Write("docs/late.md", SpecFixture.Spec("late", "module-design"));

        Assert.Null(await graph.GetAsync("late"));
        await graph.RefreshAsync();
        Assert.NotNull(await graph.GetAsync("late"));
    }

    [Fact]
    public async Task Duplicate_ids_are_both_indexed_and_resolve_deterministically()
    {
        using var workspace = Populated();
        workspace.Write("docs/copy-of-arch.md", SpecFixture.Spec("arch", "architecture-design"));

        var graph = workspace.Graph();
        var view = await graph.GetAsync("arch");

        Assert.Equal("docs/arch.md", view!.Node.Path);
        var validation = await graph.ValidateAsync();
        Assert.Equal(2, validation.Issues.Count(static issue => issue.Code == SpecIssueCode.DuplicateId));
    }

    [Fact]
    public async Task Grep_substring_is_case_insensitive_by_default_and_reports_line_numbers()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var page = await graph.GrepAsync(new SpecGrepQuery { Pattern = "todo: WIRE" });

        var match = Assert.Single(page.Matches);
        Assert.Equal("task-1", match.Id);
        Assert.Equal("docs/tasks/task-1.md", match.Path);
        Assert.Equal("TODO: wire the storage layer.", match.Text);
        Assert.Equal(1, page.Total);
        Assert.Null(page.NextOffset);
    }

    [Fact]
    public async Task Grep_case_sensitive_substring_misses_other_case()
    {
        using var workspace = Populated();

        var page = await workspace.Graph().GrepAsync(new SpecGrepQuery { Pattern = "todo", IgnoreCase = false });

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Matches);
    }

    [Fact]
    public async Task Grep_regex_mode_matches_patterns_and_respects_ignore_case()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var anchored = await graph.GrepAsync(new SpecGrepQuery { Pattern = "^title: Title of module-.$", Regex = true });
        var caseSensitive = await graph.GrepAsync(new SpecGrepQuery { Pattern = "^todo", Regex = true, IgnoreCase = false });

        Assert.Equal(2, anchored.Total);
        Assert.Equal(["module-a", "module-b"], anchored.Matches.Select(static match => match.Id));
        Assert.Equal(0, caseSensitive.Total);
    }

    [Fact]
    public async Task Grep_invalid_regex_throws_argument_exception()
    {
        using var workspace = Populated();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => workspace.Graph().GrepAsync(new SpecGrepQuery { Pattern = "([", Regex = true }));
    }

    [Fact]
    public async Task Grep_metadata_filters_narrow_the_search()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        Assert.Equal(["module-a", "module-b"], Ids(await graph.GrepAsync(new SpecGrepQuery { Pattern = "Title", Type = "MODULE-DESIGN" })));
        Assert.Equal(["module-a"], Ids(await graph.GrepAsync(new SpecGrepQuery { Pattern = "Title", Tag = "storage" })));
        Assert.Equal(["module-a", "module-b"], Ids(await graph.GrepAsync(new SpecGrepQuery { Pattern = "Title", Parent = "arch" })));
        Assert.Equal(["module-a"], Ids(await graph.GrepAsync(new SpecGrepQuery { Pattern = "Title", DependsOn = "module-b" })));
        Assert.Equal(0, (await graph.GrepAsync(new SpecGrepQuery { Pattern = "Title", Type = "task-spec", Parent = "arch" })).Total);
    }

    [Fact]
    public async Task Grep_pages_through_matches_in_stable_order()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();
        var query = new SpecGrepQuery { Pattern = "# ", Limit = 2 };

        var first = await graph.GrepAsync(query);
        var second = await graph.GrepAsync(query with { Offset = 2 });
        var third = await graph.GrepAsync(query with { Offset = 4 });

        Assert.Equal(4, first.Total);
        Assert.Equal(2, first.Matches.Count);
        Assert.Equal(2, first.NextOffset);
        Assert.Equal(2, second.Matches.Count);
        Assert.Null(second.NextOffset);
        Assert.Empty(third.Matches);
        Assert.Equal(4, third.Total);
        var all = first.Matches.Concat(second.Matches).Select(static m => (m.Path, m.Line)).ToArray();
        Assert.Equal(all, all.OrderBy(static m => m.Path, StringComparer.Ordinal).ThenBy(static m => m.Line).ToArray());
    }

    [Fact]
    public async Task Grep_clamps_limit_and_negative_offset()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var zeroLimit = await graph.GrepAsync(new SpecGrepQuery { Pattern = "Title of", Limit = 0 });
        var negativeOffset = await graph.GrepAsync(new SpecGrepQuery { Pattern = "Title of", Offset = -5, Limit = 100 });

        Assert.Single(zeroLimit.Matches);
        Assert.Equal(4, negativeOffset.Total);
        Assert.Equal(4, negativeOffset.Matches.Count);
    }

    [Fact]
    public async Task Grep_rejects_empty_pattern()
    {
        using var workspace = Populated();

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Graph().GrepAsync(new SpecGrepQuery { Pattern = " " }));
    }

    [Fact]
    public async Task Get_lists_forward_links_with_resolved_and_dangling_targets()
    {
        using var workspace = Populated();

        var view = await workspace.Graph().GetAsync("task-1");

        Assert.NotNull(view);
        var forward = view.ForwardLinks.Select(static link => (link.Kind, link.TargetId, Exists: link.Target is not null)).ToArray();
        Assert.Equal(
            new[] { (SpecEdgeKind.Parent, "module-a", true), (SpecEdgeKind.Implements, "module-a", true), (SpecEdgeKind.Covers, "arch", true), (SpecEdgeKind.Covers, "ghost", false) },
            forward);
        Assert.Equal(["ghost"], view.DanglingTargets);
        Assert.True(view.HasDanglingTargets);
        Assert.Equal("module-a", view.Frontmatter.Parent);
    }

    [Fact]
    public async Task Get_lists_reverse_links_from_other_specs()
    {
        using var workspace = Populated();

        var view = await workspace.Graph().GetAsync("module-a");

        Assert.NotNull(view);
        Assert.Equal(
            new[] { (SpecEdgeKind.References, "module-b"), (SpecEdgeKind.Parent, "task-1"), (SpecEdgeKind.Implements, "task-1") },
            view.ReverseLinks.Select(static link => (link.Kind, link.TargetId)).ToArray());
        Assert.False(view.HasDanglingTargets);
    }

    [Fact]
    public async Task Get_unknown_id_returns_null()
    {
        using var workspace = Populated();

        Assert.Null(await workspace.Graph().GetAsync("nope"));
    }

    [Fact]
    public async Task Subtree_walks_children_by_depth_and_marks_reverse_edges()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var shallow = await graph.GetGraphAsync("arch", SpecGraphDirection.Subtree, depth: 1);
        var deep = await graph.GetGraphAsync("arch", SpecGraphDirection.Subtree, depth: 2);

        Assert.NotNull(shallow);
        Assert.Equal(["arch", "module-a", "module-b"], shallow.Nodes.Select(static node => node.Node.Id).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.All(shallow.Edges, static edge => Assert.True(edge.IsReverse));
        Assert.Equal(["arch", "module-a", "module-b", "task-1"], deep!.Nodes.Select(static node => node.Node.Id).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Equal(2, deep.Nodes.Single(static node => node.Node.Id == "task-1").Depth);
    }

    [Fact]
    public async Task Ancestors_follow_parent_chain_up_to_depth()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var full = await graph.GetGraphAsync("task-1", SpecGraphDirection.Ancestors, depth: 5);
        var limited = await graph.GetGraphAsync("task-1", SpecGraphDirection.Ancestors, depth: 1);

        Assert.Equal(["task-1", "module-a", "arch"], full!.Nodes.Select(static node => node.Node.Id));
        Assert.Equal([0, 1, 2], full.Nodes.Select(static node => node.Depth));
        Assert.All(full.Edges, static edge => Assert.False(edge.IsReverse));
        Assert.Equal(["task-1", "module-a"], limited!.Nodes.Select(static node => node.Node.Id));
    }

    [Fact]
    public async Task Neighbors_follow_one_edge_kind_in_both_directions()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var dependsOn = await graph.GetGraphAsync("module-a", SpecGraphDirection.Neighbors, depth: 1, edge: SpecEdgeKind.DependsOn);
        var references = await graph.GetGraphAsync("module-a", SpecGraphDirection.Neighbors, depth: 1, edge: SpecEdgeKind.References);

        Assert.Equal(["module-a", "module-b"], dependsOn!.Nodes.Select(static node => node.Node.Id).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Contains(dependsOn.Edges, static edge => edge.FromId == "module-a" && edge.ToId == "module-b" && !edge.IsReverse);
        Assert.Equal(["module-a", "module-b"], references!.Nodes.Select(static node => node.Node.Id).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Contains(references.Edges, static edge => edge.FromId == "module-b" && edge.ToId == "module-a" && edge.IsReverse);
    }

    [Fact]
    public async Task Graph_queries_on_cycles_terminate_and_never_repeat_nodes()
    {
        using var workspace = new SpecWorkspace();
        workspace.Write("a.md", SpecFixture.Spec("a", "module-design", "parent: b\nreferences: [b]\n"));
        workspace.Write("b.md", SpecFixture.Spec("b", "module-design", "parent: a\nreferences: [a]\n"));
        var graph = workspace.Graph();

        var subtree = await graph.GetGraphAsync("a", SpecGraphDirection.Subtree, depth: 10);
        var ancestors = await graph.GetGraphAsync("a", SpecGraphDirection.Ancestors, depth: 10);
        var neighbors = await graph.GetGraphAsync("a", SpecGraphDirection.Neighbors, depth: 10, edge: SpecEdgeKind.References);

        Assert.Equal(["a", "b"], subtree!.Nodes.Select(static node => node.Node.Id).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Equal(["a", "b"], ancestors!.Nodes.Select(static node => node.Node.Id).OrderBy(static id => id, StringComparer.Ordinal));
        Assert.Equal(2, neighbors!.Nodes.Count);
        Assert.Equal(10, neighbors.Depth);
    }

    [Fact]
    public async Task Graph_depth_is_clamped_to_the_supported_range()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var zero = await graph.GetGraphAsync("arch", SpecGraphDirection.Subtree, depth: 0);
        var huge = await graph.GetGraphAsync("arch", SpecGraphDirection.Subtree, depth: 999);

        Assert.Equal(1, zero!.Depth);
        Assert.Equal(10, huge!.Depth);
    }

    [Fact]
    public async Task Graph_for_unknown_root_returns_null()
    {
        using var workspace = Populated();

        Assert.Null(await workspace.Graph().GetGraphAsync("nope"));
    }

    [Fact]
    public async Task Validate_reports_only_the_intentionally_dangling_ghost_link()
    {
        using var workspace = Populated();

        var result = await workspace.Graph().ValidateAsync();

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(SpecIssueCode.DanglingLink, issue.Code);
        Assert.Equal("task-1", issue.SpecId);
    }

    [Fact]
    public async Task Validate_of_a_fully_linked_tree_is_valid()
    {
        using var workspace = new SpecWorkspace();
        workspace.Write("arch.md", SpecFixture.Spec("arch", "architecture-design"));
        workspace.Write("child.md", SpecFixture.Spec("child", "module-design", "parent: arch\ndepends-on: [arch]\n"));

        var result = await workspace.Graph().ValidateAsync();

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Validate_reports_dangling_parent_and_dangling_link()
    {
        using var workspace = new SpecWorkspace();
        workspace.Write("x.md", SpecFixture.Spec("x", "module-design", "parent: missing-parent\ndepends-on: [missing-dep]\n"));

        var result = await workspace.Graph().ValidateAsync();

        Assert.Contains(result.Issues, issue => issue.Code == SpecIssueCode.DanglingParent && issue.SpecId == "x" && issue.Path == "x.md");
        Assert.Contains(result.Issues, issue => issue.Code == SpecIssueCode.DanglingLink && issue.Message.Contains("missing-dep", StringComparison.Ordinal));
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validate_reports_parent_cycles_for_each_member_and_self_parent()
    {
        using var workspace = new SpecWorkspace();
        workspace.Write("a.md", SpecFixture.Spec("a", "module-design", "parent: b\n"));
        workspace.Write("b.md", SpecFixture.Spec("b", "module-design", "parent: a\n"));
        workspace.Write("self.md", SpecFixture.Spec("self", "module-design", "parent: self\n"));
        workspace.Write("leaf.md", SpecFixture.Spec("leaf", "module-design", "parent: a\n"));

        var cycles = (await workspace.Graph().ValidateAsync()).Issues
            .Where(static issue => issue.Code == SpecIssueCode.ParentCycle)
            .Select(static issue => issue.SpecId!)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "a", "b", "self" }, cycles);
    }

    [Fact]
    public async Task Create_writes_frontmatter_and_default_body_then_indexes_it()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var view = await graph.CreateAsync(new SpecCreateRequest
        {
            Path = "docs/new/module-c.md",
            Id = "module-c",
            Type = "module-design",
            Title = "Module C",
            Parent = "arch",
            DependsOn = ["module-a"],
            Tags = ["fresh"],
        });

        Assert.Equal("module-c", view.Node.Id);
        Assert.Equal("docs/new/module-c.md", view.Node.Path);
        Assert.Equal("module-c", view.Frontmatter.Id);
        Assert.Equal("arch", view.Frontmatter.Parent);
        Assert.Equal(["module-a"], view.Frontmatter.DependsOn);
        var text = await File.ReadAllTextAsync(workspace.Combine("docs", "new", "module-c.md"));
        Assert.EndsWith("---\n\n# Module C\n", text, StringComparison.Ordinal);
        Assert.NotNull(await graph.GetAsync("module-c"));
    }

    [Fact]
    public async Task Create_uses_given_body_and_status_when_provided()
    {
        using var workspace = new SpecWorkspace();

        var view = await workspace.Graph().CreateAsync(new SpecCreateRequest
        {
            Path = "draft.md",
            Id = "draft",
            Type = "task-spec",
            Title = "Draft",
            Status = "draft",
            Body = "Custom body",
        });

        Assert.Equal("draft", view.Node.Status);
        Assert.EndsWith("---\n\nCustom body", await File.ReadAllTextAsync(workspace.Combine("draft.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_rejects_duplicate_id_without_writing()
    {
        using var workspace = Populated();

        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Graph().CreateAsync(new SpecCreateRequest
        {
            Path = "docs/dup.md",
            Id = "arch",
            Type = "architecture-design",
            Title = "Dup",
        }));

        Assert.False(File.Exists(workspace.Combine("docs", "dup.md")));
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("../../escape.md")]
    [InlineData("docs/../../escape.md")]
    [InlineData("../ws-sibling/escape.md")]
    public async Task Create_refuses_paths_that_leave_the_workspace(string relative)
    {
        using var workspace = Populated();
        var escaped = Path.GetFullPath(Path.Combine(workspace.Root, relative));

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Graph().CreateAsync(new SpecCreateRequest
        {
            Path = relative,
            Id = "escape",
            Type = "module-design",
            Title = "Escape",
        }));

        Assert.False(File.Exists(escaped));
    }

    [Fact]
    public async Task Create_refuses_absolute_path_outside_the_workspace()
    {
        using var workspace = Populated();
        using var outside = new TempDirectory();
        var target = Path.Combine(outside.Path, "outside.md");

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Graph().CreateAsync(new SpecCreateRequest
        {
            Path = target,
            Id = "outside",
            Type = "module-design",
            Title = "Outside",
        }));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Create_accepts_absolute_path_inside_the_workspace()
    {
        using var workspace = new SpecWorkspace();
        var target = workspace.Combine("inside", "abs.md");

        var view = await workspace.Graph().CreateAsync(new SpecCreateRequest
        {
            Path = target,
            Id = "abs",
            Type = "module-design",
            Title = "Absolute",
        });

        Assert.Equal("inside/abs.md", view.Node.Path);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task Create_refuses_non_markdown_extension()
    {
        using var workspace = new SpecWorkspace();

        await Assert.ThrowsAsync<ArgumentException>(() => workspace.Graph().CreateAsync(new SpecCreateRequest
        {
            Path = "notes.txt",
            Id = "txt",
            Type = "module-design",
            Title = "Text",
        }));

        Assert.False(File.Exists(workspace.Combine("notes.txt")));
    }

    [Fact]
    public async Task Create_treats_id_as_data_not_as_a_path()
    {
        using var workspace = new SpecWorkspace();

        var view = await workspace.Graph().CreateAsync(new SpecCreateRequest
        {
            Path = "safe.md",
            Id = "../escape",
            Type = "module-design",
            Title = "Traversal id",
        });

        Assert.Equal("../escape", view.Node.Id);
        Assert.True(File.Exists(workspace.Combine("safe.md")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(workspace.Root)!, "escape.md")));
    }

    [Fact]
    public async Task Update_sets_removes_and_adds_list_entries_keeping_body_and_unknown_fields()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var updated = await graph.UpdateFrontmatterAsync("module-a", new SpecFrontmatterUpdate
        {
            Set = new Dictionary<string, string?> { ["status"] = "done", ["title"] = null },
            Add = new Dictionary<string, IReadOnlyList<string>> { ["tags"] = ["cache"] },
            Remove = new Dictionary<string, IReadOnlyList<string>> { ["depends-on"] = ["module-b"] },
        });

        Assert.NotNull(updated);
        Assert.Equal("done", updated.Node.Status);
        Assert.Null(updated.Frontmatter.Title);
        Assert.Equal(["storage", "cache"], updated.Frontmatter.Tags);
        Assert.Empty(updated.Frontmatter.DependsOn);
        var text = await File.ReadAllTextAsync(workspace.Combine("docs", "module-a.md"));
        Assert.EndsWith("\n# Title\n", text, StringComparison.Ordinal);
        Assert.Contains("parent: arch", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_keeps_crlf_line_endings_of_the_file()
    {
        using var workspace = new SpecWorkspace();
        var original = SpecFixture.Spec("crlf", "module-design").Replace("\n", "\r\n", StringComparison.Ordinal);
        workspace.Write("crlf.md", original);
        var graph = workspace.Graph();

        await graph.UpdateFrontmatterAsync("crlf", new SpecFrontmatterUpdate
        {
            Set = new Dictionary<string, string?> { ["status"] = "active" },
        });

        var bytes = await File.ReadAllTextAsync(workspace.Combine("crlf.md"));
        Assert.Contains("status: active\r\n", bytes, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\r", bytes, StringComparison.Ordinal);
        Assert.Equal(bytes.Count(static ch => ch == '\n'), bytes.Split("\r\n").Length - 1);
    }

    [Fact]
    public async Task Update_refuses_to_remove_id_or_type_and_leaves_file_unchanged()
    {
        using var workspace = Populated();
        var path = workspace.Combine("docs", "module-b.md");
        var before = await File.ReadAllTextAsync(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => workspace.Graph().UpdateFrontmatterAsync("module-b", new SpecFrontmatterUpdate
        {
            Set = new Dictionary<string, string?> { ["type"] = null },
        }));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task An_update_never_leaves_a_truncated_file_when_the_target_cannot_be_replaced()
    {
        using var workspace = new SpecWorkspace();
        workspace.Write("docs/module-a.md", SpecFixture.Spec("module-a", "module-design", "status: active\n"));
        var path = workspace.Combine("docs", "module-a.md");
        var original = await File.ReadAllBytesAsync(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var update = new SpecFrontmatterUpdate
            {
                Set = new Dictionary<string, string?> { ["status"] = "done" },
            };
            var stopwatch = Stopwatch.StartNew();
            var exception = await Record.ExceptionAsync(() => workspace.Graph().UpdateFrontmatterAsync("module-a", update));
            stopwatch.Stop();

            Assert.Empty(Directory.GetFiles(workspace.Root, "*.tmp", SearchOption.AllDirectories));

            if (!OperatingSystem.IsWindows())
            {
                Assert.Null(exception);
                Assert.Contains("status: done", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
                return;
            }

            Assert.IsType<UnauthorizedAccessException>(exception);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"the failed update returned after {stopwatch.ElapsedMilliseconds} ms, so it was retried");
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            var reloaded = await workspace.Graph().GetAsync("module-a");
            Assert.NotNull(reloaded);
            Assert.Equal("active", reloaded.Node.Status);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Update_of_unknown_id_returns_null()
    {
        using var workspace = Populated();

        Assert.Null(await workspace.Graph().UpdateFrontmatterAsync("ghost", new SpecFrontmatterUpdate()));
    }

    [Fact]
    public async Task Delete_removes_the_file_and_leaves_links_dangling()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        Assert.True(await graph.DeleteAsync("module-a"));

        Assert.False(File.Exists(workspace.Combine("docs", "module-a.md")));
        Assert.Null(await graph.GetAsync("module-a"));
        var validation = await graph.ValidateAsync();
        Assert.Contains(validation.Issues, static issue => issue.Code == SpecIssueCode.DanglingParent && issue.SpecId == "task-1");
        Assert.Contains(validation.Issues, static issue => issue.Code == SpecIssueCode.DanglingLink && issue.SpecId == "module-b");
    }

    [Fact]
    public async Task Delete_of_unknown_id_returns_false()
    {
        using var workspace = Populated();

        Assert.False(await workspace.Graph().DeleteAsync("ghost"));
    }

    [Fact]
    public async Task Options_are_applied_to_extension_and_skipped_directories()
    {
        using var workspace = new SpecWorkspace();
        workspace.Write("custom/skip-me/a.spec", SpecFixture.Spec("custom-spec", "module-design"));
        workspace.Write("docs/b.spec", SpecFixture.Spec("custom-spec-2", "module-design"));
        var options = new SpecGraphOptions { Extension = ".spec", SkippedDirectories = ["skip-me"] };

        var ids = (await workspace.Graph(options).ListAsync()).Select(static node => node.Id).ToArray();

        Assert.Equal(["custom-spec-2"], ids);
    }

    [Fact]
    public async Task Bom_prefixed_spec_is_indexed()
    {
        using var workspace = new SpecWorkspace();
        workspace.Write("bom.md", "\uFEFF" + SpecFixture.Spec("bom", "module-design"));

        Assert.Equal("bom", (await workspace.Graph().GetAsync("bom"))!.Node.Id);
    }

    [Fact]
    public async Task Frontmatter_with_crlf_is_indexed_and_grep_lines_are_split_on_lf()
    {
        using var workspace = new SpecWorkspace();
        workspace.Write("win.md", SpecFixture.Spec("win", "module-design", body: "\nfirst\nsecond line\n").Replace("\n", "\r\n", StringComparison.Ordinal));

        var graph = workspace.Graph();
        var page = await graph.GrepAsync(new SpecGrepQuery { Pattern = "second" });

        Assert.Equal("win", (await graph.GetAsync("win"))!.Node.Id);
        Assert.Equal(8, Assert.Single(page.Matches).Line);
    }

    [Fact]
    public async Task Concurrent_refreshes_and_queries_do_not_fault()
    {
        using var workspace = Populated();
        var graph = workspace.Graph();

        var tasks = Enumerable.Range(0, 16).Select(async index =>
        {
            if (index % 2 == 0)
            {
                await graph.RefreshAsync();
                return 0;
            }

            var page = await graph.GrepAsync(new SpecGrepQuery { Pattern = "Title of" });
            return page.Total;
        });
        var results = await Task.WhenAll(tasks);

        Assert.All(results.Where(static total => total > 0), static total => Assert.Equal(4, total));
        Assert.Equal(4, (await graph.ListAsync()).Count);
    }

    [Fact]
    public async Task Cancelled_refresh_throws_operation_canceled()
    {
        using var workspace = Populated();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.Graph().RefreshAsync(cts.Token));
    }

    [Fact]
    public void Constructor_normalises_root_and_rejects_blank_root()
    {
        using var workspace = new SpecWorkspace();
        var graph = new SpecGraph(workspace.Root + Path.DirectorySeparatorChar);

        Assert.Equal(Path.GetFullPath(workspace.Root).TrimEnd(Path.DirectorySeparatorChar), graph.WorkspaceRoot, ignoreCase: OperatingSystem.IsWindows());
        Assert.Throws<ArgumentException>(() => new SpecGraph(" "));
    }

    [Fact]
    public void Grep_query_defaults_match_documented_limits()
    {
        var query = new SpecGrepQuery { Pattern = "x" };

        Assert.True(query.IgnoreCase);
        Assert.False(query.Regex);
        Assert.Equal(200, query.Limit);
        Assert.Equal(0, query.Offset);
        Assert.Equal(2 * 1024 * 1024, new SpecGraphOptions().MaxFileBytes);
    }
}
