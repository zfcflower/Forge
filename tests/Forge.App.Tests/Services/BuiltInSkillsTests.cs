using System.Text.RegularExpressions;
using Forge.App.Services;
using Forge.App.Skills;
using Xunit;

namespace Forge.App.Tests.Services;

/// <summary>
/// The skills the product ships (<see cref="BuiltInSkills"/>): the embedded list, the content version that names the
/// materialized directory, and the materialization itself - which must be idempotent, prune stale versions and never
/// throw on an unwritable root.
/// </summary>
public sealed class BuiltInSkillsTests
{
    [Fact]
    public void All_lists_the_four_product_skills_in_name_order_with_front_matter_and_lf_text()
    {
        var skills = BuiltInSkills.All;

        Assert.Equal(
            ["brainstorming", "choosing-a-workflow", "shipping-a-pr", "todos"],
            skills.Select(skill => skill.Name).ToArray());
        foreach (var skill in skills)
        {
            Assert.NotEqual(string.Empty, skill.Description);
            Assert.StartsWith("Use ", skill.Description, StringComparison.Ordinal);
            Assert.StartsWith("---", skill.Text, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', skill.Text);
        }
    }

    [Fact]
    public void ContentVersion_is_16_lowercase_hex_characters_and_stable_across_reads()
    {
        // The version names the prompt prefix's skill directory, so two reads of the same build must agree on it.
        var first = BuiltInSkills.ContentVersion;
        var second = BuiltInSkills.ContentVersion;

        Assert.Equal(first, second);
        Assert.Matches(new Regex("^[0-9a-f]{16}$", RegexOptions.None, TimeSpan.FromSeconds(1)), first);
    }

    [Fact]
    public void Materialize_writes_every_skill_under_the_version_directory_and_returns_it()
    {
        var userData = TestTempFiles.NewDirectory("forge-builtin-");
        try
        {
            var directory = BuiltInSkills.Materialize(userData);

            Assert.NotNull(directory);
            Assert.Equal(
                Path.Combine(userData, "builtin-skills", BuiltInSkills.ContentVersion),
                directory);
            foreach (var skill in BuiltInSkills.All)
            {
                var text = File.ReadAllText(Path.Combine(directory!, skill.Name, "SKILL.md"));
                Assert.Equal(skill.Text, text);
            }
        }
        finally
        {
            TestTempFiles.DeleteTree(userData);
        }
    }

    [Fact]
    public void Materialize_is_idempotent_and_does_not_rewrite_an_existing_version()
    {
        var userData = TestTempFiles.NewDirectory("forge-builtin-");
        try
        {
            var first = BuiltInSkills.Materialize(userData);
            Assert.NotNull(first);

            // A file the test plants in the complete version directory must survive a second call: the version is only
            // written when a file is missing, never refreshed.
            var tampered = Path.Combine(first!, "todos", "SKILL.md");
            File.WriteAllText(tampered, "tampered by the test\n");
            var before = File.GetLastWriteTimeUtc(tampered);

            var second = BuiltInSkills.Materialize(userData);

            Assert.Equal(first, second);
            Assert.Equal("tampered by the test\n", File.ReadAllText(tampered));
            Assert.Equal(before, File.GetLastWriteTimeUtc(tampered));
        }
        finally
        {
            TestTempFiles.DeleteTree(userData);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Materialize_of_a_blank_user_data_directory_returns_null(string? userData)
    {
        Assert.Null(BuiltInSkills.Materialize(userData));
    }

    [Fact]
    public void Materialize_prunes_the_versions_of_older_builds_beside_the_current_one()
    {
        var userData = TestTempFiles.NewDirectory("forge-builtin-");
        try
        {
            var stale = Path.Combine(userData, "builtin-skills", "0000000000000000");
            Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, "SKILL.md"), "an older build's skill\n");

            var directory = BuiltInSkills.Materialize(userData);

            Assert.NotNull(directory);
            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(directory!));
        }
        finally
        {
            TestTempFiles.DeleteTree(userData);
        }
    }

    [Fact]
    public void Materialize_returns_null_without_throwing_when_the_root_cannot_be_written()
    {
        var userData = TestTempFiles.NewDirectory("forge-builtin-");
        try
        {
            // "builtin-skills" exists as a file, so the version directory cannot be created under it.
            File.WriteAllText(Path.Combine(userData, "builtin-skills"), "not a directory\n");

            Assert.Null(BuiltInSkills.Materialize(userData));
        }
        finally
        {
            TestTempFiles.DeleteQuietly(Path.Combine(userData, "builtin-skills"));
            TestTempFiles.DeleteTree(userData);
        }
    }
}

/// <summary>
/// The product's skills inside <see cref="SkillCatalog.Discover"/>: the version directory is the last root, so a
/// workspace or user copy of the same name wins, and a blank product directory adds no root at all.
/// </summary>
public sealed class SkillCatalogProductRootTests
{
    [Theory]
    [InlineData(".github", (int)SkillScope.BuiltIn)]
    [InlineData(".forge", (int)SkillScope.Project)]
    [InlineData("user", (int)SkillScope.Global)]
    public void A_product_skill_is_shadowed_by_every_earlier_layer_of_the_same_name(
        string folder,
        int expectedScopeValue)
    {
        // ".github" and ".forge" are workspace roots; "user" is the user directory's skills root.
        var expectedScope = (SkillScope)expectedScopeValue;
        var root = TestTempFiles.NewDirectory("forge-skill-product-");
        try
        {
            var workspace = Path.Combine(root, "ws");
            var user = Path.Combine(root, "user");
            var product = Path.Combine(root, "product");
            // ".github" and ".forge" live inside the workspace; "user" is the user directory's own skills root.
            var earlierRoot = folder == "user" ? user : workspace;
            var earlierRelative = (folder == "user" ? "skills" : folder + "/skills") + "/shared/SKILL.md";
            TestTempFiles.WriteFile(earlierRoot, earlierRelative, "---\nname: shared\ndescription: the earlier layer's copy\n---\n");
            TestTempFiles.WriteFile(product, "shared/SKILL.md", "---\nname: shared\ndescription: the product's own copy\n---\n");
            TestTempFiles.WriteFile(product, "product-only/SKILL.md", "---\nname: product-only\ndescription: only the product ships this\n---\n");

            var skills = SkillCatalog.Discover(workspace, user, product);

            var shared = Assert.Single(skills, skill => skill.Name == "shared");
            Assert.Equal(expectedScope, shared.Scope);
            Assert.Equal("the earlier layer's copy", shared.Description);
            var own = Assert.Single(skills, skill => skill.Name == "product-only");
            Assert.Equal(SkillScope.Product, own.Scope);
            Assert.StartsWith(product, own.Path, StringComparison.Ordinal);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_product_directory_adds_no_root_and_keeps_the_two_argument_behaviour(string? product)
    {
        var root = TestTempFiles.NewDirectory("forge-skill-product-");
        try
        {
            var workspace = Path.Combine(root, "ws");
            var user = Path.Combine(root, "user");
            TestTempFiles.WriteFile(workspace, ".github/skills/ws-skill/SKILL.md", "---\nname: ws-skill\n---\n");
            TestTempFiles.WriteFile(user, "skills/user-skill/SKILL.md", "---\nname: user-skill\n---\n");

            var skills = SkillCatalog.Discover(workspace, user, product);

            Assert.Equal(["ws-skill", "user-skill"], skills.Select(skill => skill.Name).ToArray());
            Assert.DoesNotContain(skills, skill => skill.Scope == SkillScope.Product);
        }
        finally
        {
            TestTempFiles.DeleteTree(root);
        }
    }
}
