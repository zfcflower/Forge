using Xunit;

namespace Forge.Approval.Tests;

public class HashingTests
{
    private const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void Source_hash_of_empty_text_is_the_sha256_of_no_bytes()
    {
        Assert.Equal(EmptySha256, Hashing.SourceHash(""));
    }

    [Fact]
    public void Source_hash_is_the_sha256_of_the_normalized_utf8_text()
    {
        Assert.Equal(ApprovalFixtures.Sha256Hex("line one\nline two"), Hashing.SourceHash("line one\nline two\n"));
    }

    [Theory]
    [InlineData("line one\nline two\n")]
    [InlineData("line one\r\nline two\r\n")]
    [InlineData("line one\rline two\r")]
    [InlineData("line one\nline two")]
    [InlineData("line one\r\nline two")]
    [InlineData("line one\r\nline two\n")]
    public void Source_hash_is_stable_across_line_ending_styles(string source)
    {
        Assert.Equal(Hashing.SourceHash("line one\nline two"), Hashing.SourceHash(source));
    }

    [Fact]
    public void Source_hash_normalizes_mixed_line_endings_in_one_text()
    {
        Assert.Equal(Hashing.SourceHash("a\nb\nc"), Hashing.SourceHash("a\r\nb\rc\n"));
    }

    [Theory]
    [InlineData("value", "value\n")]
    [InlineData("value", "value\r\n")]
    [InlineData("value", "value\r")]
    public void Exactly_one_trailing_newline_in_any_style_is_ignored(string bare, string withNewline)
    {
        Assert.Equal(Hashing.SourceHash(bare), Hashing.SourceHash(withNewline));
    }

    [Fact]
    public void Two_trailing_newlines_are_significant()
    {
        Assert.NotEqual(Hashing.SourceHash("value"), Hashing.SourceHash("value\n\n"));
        Assert.NotEqual(Hashing.SourceHash("value\n"), Hashing.SourceHash("value\n\n"));
        Assert.Equal(ApprovalFixtures.Sha256Hex("value\n"), Hashing.SourceHash("value\n\n"));
    }

    [Fact]
    public void Two_trailing_crlf_pairs_are_significant()
    {
        Assert.Equal(Hashing.SourceHash("value\n\n"), Hashing.SourceHash("value\r\n\r\n"));
        Assert.NotEqual(Hashing.SourceHash("value\r\n"), Hashing.SourceHash("value\r\n\r\n"));
    }

    [Fact]
    public void Content_edit_changes_the_source_hash()
    {
        Assert.NotEqual(Hashing.SourceHash("return 1;"), Hashing.SourceHash("return 2;"));
    }

    [Fact]
    public void Trailing_space_is_not_normalized_away()
    {
        Assert.NotEqual(Hashing.SourceHash("value"), Hashing.SourceHash("value "));
    }

    [Fact]
    public void Normalize_source_collapses_each_line_break_to_lf_and_drops_one_trailing_lf()
    {
        Assert.Equal("a\nb\nc\n", Hashing.NormalizeSource("a\r\nb\rc\n\n"));
        Assert.Equal("", Hashing.NormalizeSource("\r\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("ünïcode\r\n")]
    public void Source_hash_is_64_lowercase_hex(string source)
    {
        Assert.Matches("^[0-9a-f]{64}$", Hashing.SourceHash(source));
    }

    [Fact]
    public void Source_hash_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => Hashing.SourceHash(null!));
    }

    [Fact]
    public void Dependency_hash_of_no_dependencies_is_the_sha256_of_no_bytes()
    {
        Assert.Equal(EmptySha256, Hashing.DependencyHash([]));
    }

    [Fact]
    public void Dependency_hash_is_the_sha256_of_sorted_name_at_version_lines()
    {
        var hash = Hashing.DependencyHash([new Dependency("Beta", "2.0.0"), new Dependency("Alpha", "1.0.0")]);

        Assert.Equal(ApprovalFixtures.Sha256Hex("Alpha@1.0.0\nBeta@2.0.0"), hash);
    }

    [Fact]
    public void Dependency_hash_is_independent_of_entry_order()
    {
        var alpha = new Dependency("Alpha", "1.0.0");
        var beta = new Dependency("Beta", "2.0.0");
        var gamma = new Dependency("Gamma", "3.0.0");
        var expected = Hashing.DependencyHash([alpha, beta, gamma]);

        Assert.Equal(expected, Hashing.DependencyHash([alpha, gamma, beta]));
        Assert.Equal(expected, Hashing.DependencyHash([beta, alpha, gamma]));
        Assert.Equal(expected, Hashing.DependencyHash([beta, gamma, alpha]));
        Assert.Equal(expected, Hashing.DependencyHash([gamma, alpha, beta]));
        Assert.Equal(expected, Hashing.DependencyHash([gamma, beta, alpha]));
    }

    [Fact]
    public void Dependency_hash_orders_names_ordinally_so_uppercase_precedes_lowercase()
    {
        var hash = Hashing.DependencyHash([new Dependency("b", "1"), new Dependency("B", "1")]);

        Assert.Equal(ApprovalFixtures.Sha256Hex("B@1\nb@1"), hash);
    }

    [Fact]
    public void Duplicate_dependency_entries_collapse()
    {
        var once = Hashing.DependencyHash([new Dependency("A", "1"), new Dependency("B", "2")]);
        var repeated = Hashing.DependencyHash(
            [new Dependency("A", "1"), new Dependency("A", "1"), new Dependency("B", "2"), new Dependency("B", "2")]);

        Assert.Equal(once, repeated);
    }

    [Fact]
    public void Same_name_with_different_versions_is_not_a_duplicate()
    {
        var hash = Hashing.DependencyHash([new Dependency("A", "1"), new Dependency("A", "2")]);

        Assert.Equal(ApprovalFixtures.Sha256Hex("A@1\nA@2"), hash);
        Assert.NotEqual(Hashing.DependencyHash([new Dependency("A", "1")]), hash);
    }

    [Fact]
    public void Version_bump_changes_the_dependency_hash()
    {
        Assert.NotEqual(
            Hashing.DependencyHash([new Dependency("Newtonsoft.Json", "13.0.3")]),
            Hashing.DependencyHash([new Dependency("Newtonsoft.Json", "13.0.4")]));
    }

    [Fact]
    public void Dependency_name_change_changes_the_dependency_hash()
    {
        Assert.NotEqual(
            Hashing.DependencyHash([new Dependency("Newtonsoft.Json", "13.0.3")]),
            Hashing.DependencyHash([new Dependency("System.Text.Json", "13.0.3")]));
    }

    [Fact]
    public void Dependency_hash_rejects_null_sequence_and_null_entry()
    {
        Assert.Throws<ArgumentNullException>(() => Hashing.DependencyHash(null!));
        Assert.Throws<ArgumentNullException>(() => Hashing.DependencyHash([null!]));
    }

    [Fact]
    public void Fingerprint_is_the_sha256_of_source_dependency_and_sdk_joined_by_newlines()
    {
        var fingerprint = Hashing.ApprovalFingerprint("source", "deps", "10.0.100");

        Assert.Equal(ApprovalFixtures.Sha256Hex("source\ndeps\n10.0.100"), fingerprint);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Fact]
    public void Fingerprint_changes_when_the_source_hash_changes()
    {
        Assert.NotEqual(
            Hashing.ApprovalFingerprint("source-a", "deps", "10.0.100"),
            Hashing.ApprovalFingerprint("source-b", "deps", "10.0.100"));
    }

    [Fact]
    public void Fingerprint_changes_when_the_dependency_hash_changes()
    {
        Assert.NotEqual(
            Hashing.ApprovalFingerprint("source", "deps-a", "10.0.100"),
            Hashing.ApprovalFingerprint("source", "deps-b", "10.0.100"));
    }

    [Fact]
    public void Fingerprint_changes_when_the_sdk_version_changes()
    {
        Assert.NotEqual(
            Hashing.ApprovalFingerprint("source", "deps", "10.0.100"),
            Hashing.ApprovalFingerprint("source", "deps", "10.0.101"));
    }

    [Fact]
    public void Fingerprint_rejects_null_components()
    {
        Assert.Throws<ArgumentNullException>(() => Hashing.ApprovalFingerprint(null!, "deps", "10.0.100"));
        Assert.Throws<ArgumentNullException>(() => Hashing.ApprovalFingerprint("source", null!, "10.0.100"));
        Assert.Throws<ArgumentNullException>(() => Hashing.ApprovalFingerprint("source", "deps", null!));
    }
}
