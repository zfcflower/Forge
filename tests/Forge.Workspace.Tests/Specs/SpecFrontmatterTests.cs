using System.Text;
using Xunit;

namespace Forge.Workspace.Tests.Specs;

public sealed class SpecFrontmatterTests
{
    // The sample is a source literal, so its line endings follow the checkout (CRLF on Windows); the parser keeps
    // body bytes as they are, so the fixture is pinned to the canonical \n form before it is parsed.
    private static readonly string Sample = """
        ---
        id: forge-core
        type: module-design
        title: Core module
        status: active
        parent: forge-architecture
        depends-on: [forge-abstractions, "forge-plugins"]
        tags:
          - storage
          - "agents"
        owner: alice
        priority: 3
        ---

        # Core module

        Body text.
        """.ReplaceLineEndings("\n");

    private static SpecFrontmatter Parse(string text)
    {
        Assert.True(SpecFrontmatter.TryParse(text, out var frontmatter, out _));
        return frontmatter;
    }

    [Fact]
    public void Parses_known_fields_lists_and_unknown_fields()
    {
        Assert.True(SpecFrontmatter.TryParse(Sample, out var frontmatter, out var body));

        Assert.Equal("forge-core", frontmatter.Id);
        Assert.Equal("module-design", frontmatter.Type);
        Assert.Equal("Core module", frontmatter.Title);
        Assert.Equal("active", frontmatter.Status);
        Assert.Equal("forge-architecture", frontmatter.Parent);
        Assert.Equal(["forge-abstractions", "forge-plugins"], frontmatter.DependsOn);
        Assert.Equal(["storage", "agents"], frontmatter.Tags);
        Assert.Equal("alice", frontmatter.Scalar("owner"));
        Assert.Equal("3", frontmatter.Scalar("priority"));
        Assert.True(frontmatter.Has("owner"));
        Assert.False(frontmatter.Has("missing"));
        Assert.Empty(frontmatter.Covers);
        Assert.StartsWith("\n# Core module", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Round_trip_preserves_body_unknown_fields_and_field_order()
    {
        var frontmatter = Parse(Sample);
        Assert.True(SpecFrontmatter.TryParse(Sample, out _, out var body));

        var rewritten = frontmatter.ToMarkdown(body);

        Assert.True(SpecFrontmatter.TryParse(rewritten, out var again, out var againBody));
        Assert.Equal(body, againBody);
        Assert.Equal(frontmatter.Fields.Select(static field => field.Name), again.Fields.Select(static field => field.Name));
        Assert.Equal("alice", again.Scalar("owner"));
        Assert.Equal("3", again.Scalar("priority"));
        Assert.Equal(["storage", "agents"], again.Tags);
        Assert.Equal(["forge-abstractions", "forge-plugins"], again.DependsOn);
    }

    [Fact]
    public void Round_trip_of_untouched_frontmatter_keeps_body_bytes_exactly()
    {
        const string body = "\n# Title\n\n```csharp\nvar x = 1;\n```\n\ntrailing spaces   \n";
        var frontmatter = new SpecFrontmatter([new SpecField("id", ["a"]), new SpecField("type", ["t"]), new SpecField("owner", ["bob"])]);
        var markdown = frontmatter.ToMarkdown(body);

        Assert.True(SpecFrontmatter.TryParse(markdown, out _, out var parsedBody));

        Assert.Equal(body, parsedBody);
        Assert.Equal("---\nid: a\ntype: t\nowner: bob\n---\n" + body, markdown);
    }

    [Fact]
    public void Unknown_flow_list_with_several_items_is_written_back_as_a_list()
    {
        var frontmatter = Parse("---\nid: x\ntype: t\nlabels: [one, two]\n---\n");

        var markdown = frontmatter.ToMarkdown(string.Empty);

        Assert.Contains("labels: [one, two]", markdown, StringComparison.Ordinal);
        Assert.Equal(["one", "two"], Parse(markdown).List("labels"));
    }

    [Fact]
    public void Quoted_scalars_unescape_and_keep_colons_and_hashes()
    {
        var frontmatter = Parse("""
            ---
            id: "quoted-id"
            type: 'module'
            title: "Hello: \"world\" \\ path"
            status: it's fine
            parent: 'it''s parent'
            owner: "tab\there"
            ---
            """);

        Assert.Equal("quoted-id", frontmatter.Id);
        Assert.Equal("module", frontmatter.Type);
        Assert.Equal("Hello: \"world\" \\ path", frontmatter.Title);
        Assert.Equal("it's fine", frontmatter.Status);
        Assert.Equal("it's parent", frontmatter.Parent);
        Assert.Equal("tab\there", frontmatter.Scalar("owner"));
    }

    [Fact]
    public void Values_needing_quotes_are_quoted_on_write_and_read_back_identically()
    {
        var frontmatter = Parse("---\nid: x\ntype: t\n---\n")
            .With("title", "a: b")
            .With("status", "- dash start")
            .With("owner", "[bracketed]")
            .With("parent", "has # hash")
            .With("priority", "trailing ")
            .With("tags", "\"leading quote")
            .With("depends-on", "null")
            .With("covers", "back\\slash");

        var parsed = Parse(frontmatter.ToMarkdown(string.Empty));

        Assert.Equal("a: b", parsed.Title);
        Assert.Equal("- dash start", parsed.Status);
        Assert.Equal("[bracketed]", parsed.Scalar("owner"));
        Assert.Equal("has # hash", parsed.Parent);
        Assert.Equal("trailing ", parsed.Scalar("priority"));
        Assert.Equal("\"leading quote", parsed.Scalar("tags"));
        Assert.Equal("null", parsed.Scalar("depends-on"));
        Assert.Equal("back\\slash", parsed.Scalar("covers"));
    }

    [Fact]
    public void Comment_lines_and_blank_lines_are_ignored()
    {
        var frontmatter = Parse("---\n# a comment\nid: c\n\ntype: t\n---\n");

        Assert.Equal(["id", "type"], frontmatter.Fields.Select(static field => field.Name));
    }

    [Fact]
    public void Crlf_file_parses_and_update_keeps_the_crlf_block_and_body()
    {
        var text = "---\r\nid: win\r\ntype: t\r\ntags: [a, b]\r\n---\r\n\r\n# Title\r\nline\r\n";

        Assert.True(SpecFrontmatter.TryParse(text, out var frontmatter, out var body));
        Assert.Equal("win", frontmatter.Id);
        Assert.Equal(["a", "b"], frontmatter.Tags);
        Assert.Equal("\r\n# Title\r\nline\r\n", body);

        var rewritten = frontmatter.WithList("tags", ["a", "b", "c"]).ToMarkdown(body, "\r\n");

        Assert.Equal("---\r\nid: win\r\ntype: t\r\ntags: [a, b, c]\r\n---\r\n\r\n# Title\r\nline\r\n", rewritten);
        Assert.DoesNotContain("\r\r", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Bom_prefixed_file_is_recognised()
    {
        var text = "\uFEFF---\nid: bom\ntype: t\n---\nbody";

        Assert.True(SpecFrontmatter.TryParse(text, out var frontmatter, out var body));
        Assert.Equal("bom", frontmatter.Id);
        Assert.Equal("body", body);
    }

    [Fact]
    public void Missing_opening_or_closing_fence_is_not_frontmatter()
    {
        Assert.False(SpecFrontmatter.TryParse("# no frontmatter\n", out _, out _));
        Assert.False(SpecFrontmatter.TryParse("---\nid: open\ntype: t\n", out _, out _));
        Assert.False(SpecFrontmatter.TryParse("---", out _, out _));
        Assert.False(SpecFrontmatter.TryParse(string.Empty, out _, out _));
    }

    [Fact]
    public void Closing_fence_at_end_of_file_yields_empty_body()
    {
        Assert.True(SpecFrontmatter.TryParse("---\nid: e\ntype: t\n---", out var frontmatter, out var body));

        Assert.Equal("e", frontmatter.Id);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public void Scalar_and_list_accessors_tolerate_empty_and_missing_values()
    {
        var frontmatter = Parse("---\nid: e\ntype: t\nowner:\nempty-list: []\n---\n");

        Assert.Null(frontmatter.Scalar("owner"));
        Assert.Null(frontmatter.Scalar("empty-list"));
        Assert.Empty(frontmatter.List("empty-list"));
        Assert.Empty(frontmatter.List("missing"));
        Assert.Null(frontmatter.Status);
        Assert.Null(frontmatter.Parent);
    }

    [Fact]
    public void Duplicate_names_keep_the_last_value_at_the_first_position()
    {
        var frontmatter = new SpecFrontmatter(
        [
            new SpecField("id", ["first"]),
            new SpecField("type", ["t"]),
            new SpecField("id", ["second"]),
        ]);

        Assert.Equal("second", frontmatter.Id);
        Assert.Equal(["id", "type"], frontmatter.Fields.Select(static field => field.Name));
    }

    [Fact]
    public void With_replaces_value_and_null_removes_the_field()
    {
        var frontmatter = Parse(Sample);

        var updated = frontmatter.With("status", "done");
        var removed = frontmatter.With("status", null);

        Assert.Equal("done", updated.Status);
        Assert.Equal(frontmatter.Fields.Count, updated.Fields.Count);
        Assert.Null(removed.Status);
        Assert.False(removed.Has("status"));
        Assert.Equal("active", frontmatter.Status);
    }

    [Fact]
    public void With_list_replaces_values_and_empty_list_is_kept_explicitly()
    {
        var frontmatter = Parse(Sample);

        var replaced = frontmatter.WithList("tags", ["only"]);
        var emptied = frontmatter.WithList("tags", []);

        Assert.Equal(["only"], replaced.Tags);
        Assert.True(emptied.Has("tags"));
        Assert.Empty(emptied.Tags);
        Assert.Contains("tags: []", emptied.ToMarkdown(string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void Add_and_remove_list_entries_dedupe_and_keep_order()
    {
        var frontmatter = Parse(Sample);

        var added = frontmatter.AddToList("depends-on", ["forge-plugins", "forge-editor"]);
        var removed = added.RemoveFromList("depends-on", ["forge-abstractions"]);
        var fresh = frontmatter.AddToList("covers", ["x", "x"]);
        var untouched = frontmatter.RemoveFromList("missing", ["x"]);

        Assert.Equal(["forge-abstractions", "forge-plugins", "forge-editor"], added.DependsOn);
        Assert.Equal(["forge-plugins", "forge-editor"], removed.DependsOn);
        Assert.Equal(["x"], fresh.Covers);
        Assert.Same(frontmatter, untouched);
    }

    [Fact]
    public void Remove_drops_the_field_and_leaves_others_intact()
    {
        var removed = Parse(Sample).Remove("owner");

        Assert.False(removed.Has("owner"));
        Assert.Equal("alice", Parse(Sample).Scalar("owner"));
        Assert.Equal("3", removed.Scalar("priority"));
    }

    [Fact]
    public void Constructor_rejects_blank_field_names()
    {
        Assert.Throws<ArgumentException>(() => new SpecFrontmatter([new SpecField(" ", ["x"])]));
    }

    [Fact]
    public void Empty_frontmatter_writes_only_the_fences_and_body()
    {
        var markdown = SpecFrontmatter.Empty.ToMarkdown("body");

        Assert.Equal("---\n---\nbody", markdown);
    }

    [Fact]
    public void Written_block_uses_requested_line_ending()
    {
        var markdown = new SpecFrontmatter([new SpecField("id", ["a"]), new SpecField("type", ["t"])]).ToMarkdown("x", "\r\n");

        Assert.Equal("---\r\nid: a\r\ntype: t\r\n---\r\nx", markdown);
        Assert.Throws<ArgumentException>(() => SpecFrontmatter.Empty.ToMarkdown("x", string.Empty));
    }

    [Fact]
    public void Multiline_value_survives_a_write_and_reparse()
    {
        var frontmatter = Parse("---\nid: m\ntype: t\n---\n").With("title", "first line\nsecond line");

        var parsed = Parse(frontmatter.ToMarkdown(string.Empty));

        Assert.Equal("first line\nsecond line", parsed.Title);
    }

    [Fact]
    public void Flow_list_items_containing_commas_survive_a_write_and_reparse()
    {
        var frontmatter = Parse("---\nid: c\ntype: t\n---\n").WithList("tags", ["alpha, beta", "gamma"]);

        var parsed = Parse(frontmatter.ToMarkdown(string.Empty));

        Assert.Equal(["alpha, beta", "gamma"], parsed.Tags);
    }

    [Fact]
    public void Dash_and_colon_prefixed_items_survive_a_write_and_reparse()
    {
        var frontmatter = Parse("---\nid: c\ntype: t\n---\n").WithList("tags", ["-x", "a:b"]);

        Assert.Equal(["-x", "a:b"], Parse(frontmatter.ToMarkdown(string.Empty)).Tags);
    }

    [Fact]
    public void Unicode_values_round_trip()
    {
        var frontmatter = Parse("---\nid: u\ntype: t\n---\n").With("title", "规格 — café");

        var parsed = Parse(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(frontmatter.ToMarkdown("é"))));

        Assert.Equal("规格 — café", parsed.Title);
    }
}
