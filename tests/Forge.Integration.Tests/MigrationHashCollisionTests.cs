using System.Text.Json;
using Forge.HostClient;
using Sdk = Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>
/// Security regression for the approval <c>DependencyHash</c>: a manifest dependency that only <i>looks</i> like a
/// permission (or a private file) must hash differently from the real permission (or file), and the encoding must
/// separate type domains and lengths instead of joining <c>name@version</c> lines with a bare prefix. Otherwise an
/// attacker swaps the manifest to the privileged meaning while the old approval - and the old hash - stays valid.
/// All comparisons use the live <c>plugins.status</c> hashes; no golden hash is asserted and the database is never
/// read directly. These tests fail while the old prefix-only encoding is in place and pass once the typed encoding
/// lands.
/// </summary>
public sealed class MigrationHashCollisionTests
{
    [Fact]
    public async Task Fake_permission_dependency_requires_reapproval_once_replaced_by_a_real_permission()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("alpha", PluginSources.Standard("alpha", echoPrefix: "a"));

        // The attacker's manifest: no permission is declared, but a dependency is shaped like one.
        // A non-empty version is required by the manifest reader, so the collision needs a scope.
        WriteManifest(fx, "alpha", [new { name = "permission:Database", version = "s" }], null);
        await fx.StartAsync();

        var before = SingleStatus(await fx.Client.GetPluginStatusesAsync(fx.Ct), "alpha");
        Assert.Equal("draft", before.Status);

        // Approve through the hashes plugins.status reports, not through the database.
        await fx.Client.ApprovePluginAsync(
            "alpha", "1.0.0", before.SourceHash, before.DependencyHash, before.SdkVersion, fx.Ct);
        await fx.WaitForToolsAsync("alpha", PluginSources.AllTools.Length, "alpha tools");

        var echoTool = fx.ToolOf("alpha", "echo");
        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"hi"}""", "a:hi");

        // Same source, honest manifest: the fake dependency becomes a real permission demand.
        WriteManifest(fx, "alpha", null, [new { kind = "Database", scope = "s" }]);
        await fx.Client.RefreshPluginsAsync(fx.Ct);

        var after = SingleStatus(await fx.Client.GetPluginStatusesAsync(fx.Ct), "alpha");
        Assert.NotEqual(before.DependencyHash, after.DependencyHash);
        Assert.Equal(before.SourceHash, after.SourceHash);
        Assert.False(
            string.IsNullOrWhiteSpace(after.Error),
            "a stale approval must be reported as an error until it is re-approved");

        await fx.WaitForToolsAsync("alpha", 0, "alpha tools to be withdrawn");
        var blocked = await fx.InvokeAsync(echoTool, """{"text":"hi"}""");
        Assert.True(blocked.IsError, blocked.Content);
        Assert.Contains("must not run", blocked.Content, StringComparison.Ordinal);

        await fx.Client.ApprovePluginAsync(
            "alpha", "1.0.0", after.SourceHash, after.DependencyHash, after.SdkVersion, fx.Ct);
        await fx.WaitUntilAsync(
            () => fx.ToolsOf("alpha").Count == PluginSources.AllTools.Length, "alpha tools after re-approval");
        await fx.AssertToolContentAsync("alpha", "echo", """{"text":"hi"}""", "a:hi");
    }

    [Fact]
    public async Task Delimiter_and_domain_spoofs_change_the_dependency_hash()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("beta", PluginSources.Standard("beta", echoPrefix: "b"));
        WriteManifest(fx, "beta", [new { name = "a", version = "1" }, new { name = "b", version = "2" }], null);
        await fx.StartAsync();

        var baseline = SingleStatus(await fx.Client.GetPluginStatusesAsync(fx.Ct), "beta");

        // '@' inside a version must not be re-splittable into a different name/version pair.
        var atSplit = await SwapAsync(fx, "beta", [new { name = "a", version = "1@b" }], null);
        Assert.NotEqual(baseline.DependencyHash, atSplit.DependencyHash);
        var atSplitTwin = await SwapAsync(
            fx, "beta", [new { name = "a@1", version = "b" }], null);
        Assert.NotEqual(atSplit.DependencyHash, atSplitTwin.DependencyHash);

        // A newline inside a version must not be re-splittable into two dependency entries.
        var newlineSplit = await SwapAsync(fx, "beta", [new { name = "a", version = "1\nb@2" }], null);
        Assert.NotEqual(baseline.DependencyHash, newlineSplit.DependencyHash);

        // The type domain matters: a manifest dependency named like a permission entry is not a permission.
        var permissionSpoof = await SwapAsync(
            fx, "beta", [new { name = "permission:Database", version = "s" }], null);
        var realPermission = await SwapAsync(
            fx, "beta", null, [new { kind = "Database", scope = "s" }]);
        Assert.NotEqual(permissionSpoof.DependencyHash, realPermission.DependencyHash);
        Assert.NotEqual(baseline.DependencyHash, permissionSpoof.DependencyHash);

        // The file domain matters too: re-declaring a loadable file's digest as a manifest dependency
        // must not collapse back to the same hash.
        var digest = PrivateFileDigest(fx, "beta");
        var fileSpoof = await SwapAsync(
            fx, "beta", [new { name = "file:plugin.dll", version = digest }], null);
        Assert.NotEqual(realPermission.DependencyHash, fileSpoof.DependencyHash);

        // Sanity: the source never changed through all of these swaps.
        Assert.Equal(baseline.SourceHash, fileSpoof.SourceHash);
    }

    [Fact]
    public async Task Ordering_and_duplicates_do_not_change_the_dependency_hash()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("gamma", PluginSources.Standard("gamma", echoPrefix: "g"));
        WriteManifest(
            fx, "gamma",
            [new { name = "a", version = "1" }, new { name = "b", version = "2" }],
            [new { kind = "Database", scope = "s" }]);
        await fx.StartAsync();

        var baseline = SingleStatus(await fx.Client.GetPluginStatusesAsync(fx.Ct), "gamma");

        // Order is not meaning: a reordered manifest describes the same content.
        var reordered = await SwapAsync(
            fx, "gamma",
            [new { name = "b", version = "2" }, new { name = "a", version = "1" }],
            [new { kind = "Database", scope = "s" }]);
        Assert.Equal(baseline.DependencyHash, reordered.DependencyHash);

        // Duplicates collapse: repeating an entry describes the same content.
        var duplicated = await SwapAsync(
            fx, "gamma",
            [new { name = "a", version = "1" }, new { name = "a", version = "1" }, new { name = "b", version = "2" }],
            [new { kind = "Database", scope = "s" }]);
        Assert.Equal(baseline.DependencyHash, duplicated.DependencyHash);
        Assert.Equal(baseline.SourceHash, duplicated.SourceHash);
    }

    [Fact]
    public async Task Dependency_reader_guards_names_and_hashes_empty_versions_distinctly()
    {
        await using var fx = new E2eFixture();
        fx.WritePlugin("delta", PluginSources.Standard("delta", echoPrefix: "d"));

        // An empty version means "unpinned" and is legitimate input: the framed encoding hashes it
        // distinctly instead of colliding with a permission or file entry.
        WriteManifest(fx, "delta", [new { name = "permission:Database", version = "" }], null);
        await fx.StartAsync();
        var unpinned = SingleStatus(await fx.Client.GetPluginStatusesAsync(fx.Ct), "delta");
        Assert.Equal("draft", unpinned.Status);

        WriteManifest(fx, "delta", [new { name = "permission:Database", version = "s" }], null);
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        var pinned = SingleStatus(await fx.Client.GetPluginStatusesAsync(fx.Ct), "delta");
        Assert.NotEqual(unpinned.DependencyHash, pinned.DependencyHash);

        // A dependency without a name is still rejected instead of hashed: the package never reaches
        // the hash, so no status entry and no tools exist for it.
        WriteManifest(fx, "delta", [new { name = "", version = "s" }], null);
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        var statuses = await fx.Client.GetPluginStatusesAsync(fx.Ct);
        Assert.DoesNotContain(statuses, status => status.PluginId == "delta");
        Assert.Empty(fx.ToolsOf("delta"));
    }

    private static async Task<Sdk.PluginStatusInfo> SwapAsync(
        E2eFixture fx, string id, object[]? dependencies, object[]? permissions)
    {
        WriteManifest(fx, id, dependencies, permissions);
        await fx.Client.RefreshPluginsAsync(fx.Ct);
        return SingleStatus(await fx.Client.GetPluginStatusesAsync(fx.Ct), id);
    }

    private static void WriteManifest(
        E2eFixture fx, string id, object[]? dependencies, object[]? permissions, string version = "1.0.0")
    {
        var manifest = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["version"] = version,
            ["source"] = "Plugin.cs",
        };
        if (dependencies is not null)
        {
            manifest["dependencies"] = dependencies;
        }

        if (permissions is not null)
        {
            manifest["permissions"] = permissions;
        }

        File.WriteAllText(Path.Combine(fx.PluginDirectory(id), "manifest.json"), JsonSerializer.Serialize(manifest));
    }

    private static Sdk.PluginStatusInfo SingleStatus(IReadOnlyList<Sdk.PluginStatusInfo> statuses, string id)
    {
        var match = statuses.SingleOrDefault(status => status.PluginId == id);
        Assert.True(match is not null, $"plugins.status reported no entry for '{id}'.");
        return match!;
    }

    /// <summary>Digest of the compiled image on disk, i.e. the value the host folds into the hash as a file entry.</summary>
    private static string PrivateFileDigest(E2eFixture fx, string id)
    {
        var candidates = Directory.EnumerateFiles(Path.Combine(fx.PluginDirectory(id), "bin"), "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(path), "plugin.dll", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(candidates);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(candidates[0])));
    }
}
