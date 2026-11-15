using Forge.HostClient;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>
/// <c>extensions.install</c> (wave W10a) against a real host: the <c>sourceDir</c> containment rule, a successful install
/// recording a Draft with the caller's origin and starting nothing, the trash rollback when the package cannot be prepared
/// after it went live (and when the install is cancelled in the same window), and the two plugin roots - a built-in plugin
/// that refuses disable/delete, and a bundled plugin copied once and never overwritten.
/// Persistence is asserted against <c>approvals.db</c> directly, never through the host's own answers alone.
/// </summary>
public sealed class ExtensionInstallLifecycleTests
{
    [Theory]
    [InlineData("outside")]
    [InlineData("the install root itself")]
    [InlineData("a path that climbs out of the root")]
    public async Task Install_refuses_a_source_dir_outside_the_install_root(string kind)
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var pluginsBefore = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var installRoot = Path.Combine(fx.DataDirectory, ExtensionInstallPaths.TempDirectoryName);
        string source;
        switch (kind)
        {
            case "outside":
                // A perfectly valid package that simply does not live under the host's install root.
                source = WritePackage(Path.Combine(fx.Root, "outside-package"), "escaped");
                break;
            case "the install root itself":
                source = WritePackage(installRoot, "escaped");
                break;
            default:
                source = WritePackage(Path.GetFullPath(Path.Combine(installRoot, "..", "climbing-package")), "escaped");
                break;
        }

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => InstallAsync(fx, source, ExtensionOrigins.Installed),
            "invalid_request");

        // The refusal is decided before anything is moved: the plugins tree is byte-identical and no record was written.
        ExtensionAuthoringSupport.AssertSameTree(
            pluginsBefore,
            ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory),
            "refused install");
        Assert.Null(ApprovalDatabase.ReadRecord(fx.DataDirectory, "escaped", "1.0.0"));
        Assert.Empty(fx.Client.Tools);
    }

    [Fact]
    public async Task A_successful_install_records_a_draft_with_origin_installed_and_starts_nothing()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var source = WritePackage(
            Path.Combine(InstallRoot(fx), "package-" + Guid.NewGuid().ToString("N")),
            "shipped",
            source: PluginSources.Standard("shipped", "x"));

        var result = await InstallAsync(fx, source, ExtensionOrigins.Installed);

        Assert.Equal("shipped", result.PluginId);
        Assert.Equal("1.0.0", result.Version);
        Assert.Equal("draft", result.Status.Status);
        Assert.Empty(result.Diagnostics);

        // The persisted record is what the settings page and the next approval read: Draft (installation never approves),
        // the caller's origin, and no approval hash at all.
        var record = fx.ReadRecord("shipped", "1.0.0");
        Assert.NotNull(record);
        Assert.Equal("Draft", record!.Status);
        Assert.Equal("Installed", record.Origin);
        Assert.Null(record.ApprovedHash);

        // Live bytes came from the package and the source directory was consumed by the move.
        Assert.True(File.Exists(Path.Combine(fx.PluginDirectory("shipped"), "Plugin.cs")));
        Assert.False(Directory.Exists(source));

        // A Draft plugin is not registered and not running: nothing of it reaches the model before the user approves it.
        Assert.Empty(fx.Client.Tools);
        Assert.Empty(fx.ToolsOf("shipped"));
        var status = await fx.StatusOfAsync("shipped");
        Assert.Equal("draft", status.Status);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task A_package_that_fails_preparation_after_it_went_live_restores_the_previous_version()
    {
        await using var fx = new E2eFixture();
        await fx.StartWithStatusApprovedPluginAsync("keep", "x");
        await fx.WaitForToolsAsync("keep", PluginSources.AllTools.Length, "keep tools");
        var liveBefore = ExtensionAuthoringSupport.SnapshotTree(fx.PluginDirectory("keep"));
        var recordBefore = fx.ReadRecord("keep", "1.0.0")!;
        Assert.Equal("Approved", recordBefore.Status);

        // The manifest is readable (so the install knows which plugin it targets) but the source cannot be compiled, which
        // is only discovered by the preparation that runs after the package has been moved into place.
        var source = WritePackage(
            Path.Combine(InstallRoot(fx), "package-" + Guid.NewGuid().ToString("N")),
            "keep",
            version: "2.0.0",
            source: PluginSources.Broken("keep"));

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => InstallAsync(fx, source, ExtensionOrigins.Installed),
            "invalid_extension");

        // The rollback put the previous directory back, restored its record and re-applied it, so the approved version is
        // exactly as runnable as it was before the failed install.
        ExtensionAuthoringSupport.AssertSameTree(
            liveBefore,
            ExtensionAuthoringSupport.SnapshotTree(fx.PluginDirectory("keep")),
            "live directory after a failed install");
        var recordAfter = fx.ReadRecord("keep", "1.0.0")!;
        Assert.Equal(recordBefore, recordAfter);
        Assert.Null(fx.ReadRecord("keep", "2.0.0"));
        Assert.Empty(ExtensionAuthoringSupport.TrashEntries(fx.PluginsDirectory));
        await fx.AssertToolContentAsync("keep", PluginSources.EchoTool, """{"text":"still-here"}""", "x:still-here");
    }

    [Fact]
    public async Task Cancelling_an_install_after_the_package_went_live_restores_the_previous_version()
    {
        await using var fx = new E2eFixture();
        await fx.StartWithStatusApprovedPluginAsync("keep", "x");
        await fx.WaitForToolsAsync("keep", PluginSources.AllTools.Length, "keep tools");
        var liveBefore = ExtensionAuthoringSupport.SnapshotTree(fx.PluginDirectory("keep"));
        var recordBefore = fx.ReadRecord("keep", "1.0.0")!;

        var source = WritePackage(
            Path.Combine(InstallRoot(fx), "package-" + Guid.NewGuid().ToString("N")),
            "keep",
            version: "2.0.0",
            source: PluginSources.Standard("keep", "y"));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(fx.Ct);
        var install = InstallAsync(fx, source, ExtensionOrigins.Installed, cts.Token);

        // Cancel as soon as the package is no longer where the main program left it: the host has taken it, so whatever
        // still has to run (record write, apply) is inside the rollback window. Where exactly the cancellation lands does
        // not matter - the contract is the same end state, which is what is asserted below.
        await fx.WaitUntilAsync(() => !Directory.Exists(source), "the host to take the package");
        await cts.CancelAsync();

        var failure = await Record.ExceptionAsync(() => install);
        Assert.True(
            failure is OperationCanceledException or IpcRemoteException { Code: "cancelled" },
            $"the install should end cancelled, not '{failure?.GetType().Name ?? "succeeded"}': {failure?.Message}");

        await fx.WaitUntilAsync(
            () => SameTree(liveBefore, ExtensionAuthoringSupport.SnapshotTree(fx.PluginDirectory("keep"))),
            "the cancelled install to restore the previous directory");
        Assert.Equal(recordBefore, fx.ReadRecord("keep", "1.0.0"));

        // Where the cancellation landed decides whether a record for the new version exists: one written just before the
        // abort stays behind as a Draft for bytes that are not live (the same harmless leftover a failed fresh install
        // leaves). It is never an approval, and the next install of that version overwrites it.
        var newRecord = fx.ReadRecord("keep", "2.0.0");
        Assert.True(
            newRecord is null || (newRecord.Status == "Draft" && newRecord.ApprovedHash is null),
            $"the record of the version that never went live is '{newRecord?.Status}'");
        Assert.Empty(ExtensionAuthoringSupport.TrashEntries(fx.PluginsDirectory));

        // The old version is not just on disk: the rollback re-applied it, so its tools are callable again.
        await fx.WaitForToolsAsync("keep", PluginSources.AllTools.Length, "keep tools after the rollback");
        await fx.AssertToolContentAsync("keep", PluginSources.EchoTool, """{"text":"back"}""", "x:back");
    }

    [Fact]
    public async Task A_builtin_plugin_cannot_be_disabled_or_deleted()
    {
        var builtInRoot = Path.Combine(Path.GetTempPath(), "forge-builtin-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(builtInRoot);
        try
        {
            WritePackage(Path.Combine(builtInRoot, "core"), "core", source: PluginSources.Standard("core", "x"));

            await using var fx = new E2eFixture(hostArguments: ["--builtin", builtInRoot]);
            await fx.StartAsync();

            // Discovered from the read-only root, so its first record carries that origin.
            await fx.WaitForCleanDraftAsync("core");
            var record = fx.ReadRecord("core", "1.0.0");
            Assert.NotNull(record);
            Assert.Equal("BuiltIn", record!.Origin);

            await ExtensionAuthoringSupport.AssertHostErrorAsync(
                () => fx.Client.DisablePluginAsync("core", "1.0.0", fx.Ct),
                "builtin_blocked");
            await ExtensionAuthoringSupport.AssertHostErrorAsync(
                () => fx.Client.DeletePluginAsync("core", "1.0.0", fx.Ct),
                "builtin_blocked");

            // Built-ins are part of the app payload: the refusal leaves both the directory and the record untouched.
            Assert.True(Directory.Exists(Path.Combine(builtInRoot, "core")));
            Assert.Equal(record, fx.ReadRecord("core", "1.0.0"));
        }
        finally
        {
            TempRoots.Delete(builtInRoot);
        }
    }

    [Fact]
    public async Task A_bundled_plugin_is_copied_on_first_start_with_origin_bundled()
    {
        var bundledRoot = NewBundledRoot();
        try
        {
            var bundled = WriteBundled(bundledRoot, "gift", PluginSources.Standard("gift", "x"));
            await using var host = RestartableHost.Create(hostArguments: ["--bundled", bundledRoot]);

            await host.StartAsync();

            var live = Path.Combine(host.PluginsDirectory, "gift");
            Assert.True(File.Exists(Path.Combine(live, "manifest.json")));
            Assert.True(File.Exists(Path.Combine(live, "Plugin.cs")));
            Assert.True(File.Exists(Path.Combine(bundled, "Plugin.cs")), "the bundled copy stays next to the app");
            Assert.Empty(ExtensionAuthoringSupport.TrashEntries(host.PluginsDirectory));

            var record = await WaitForRecordAsync(host, "gift");
            Assert.Equal("Draft", record.Status);
            Assert.Equal("Bundled", record.Origin);
        }
        finally
        {
            TempRoots.Delete(bundledRoot);
        }
    }

    [Fact]
    public async Task A_second_start_never_overwrites_the_copied_bundled_plugin()
    {
        var bundledRoot = NewBundledRoot();
        try
        {
            WriteBundled(bundledRoot, "gift", PluginSources.Standard("gift", "x"));
            await using var host = RestartableHost.Create(hostArguments: ["--bundled", bundledRoot]);
            await host.StartAsync();
            await WaitForRecordAsync(host, "gift");

            // The user's copy is the one that counts from now on: the app ships a different bundled body, and the next
            // start must keep the local one (with its own bytes, hence its own content hash).
            var live = Path.Combine(host.PluginsDirectory, "gift", "Plugin.cs");
            File.AppendAllText(live, "\n// edited locally\n");
            WriteBundled(bundledRoot, "gift", PluginSources.Standard("gift", "changed-by-the-app"));
            var edited = ExtensionAuthoringSupport.SnapshotTree(host.PluginsDirectory);

            await host.RestartAsync();

            ExtensionAuthoringSupport.AssertSameTree(
                edited,
                ExtensionAuthoringSupport.SnapshotTree(host.PluginsDirectory),
                "plugins tree after a second start");

            // The record keeps the origin of the first copy even though its content hashes moved with the local edit.
            var record = await WaitForRecordAsync(host, "gift");
            Assert.Equal("Bundled", record.Origin);
        }
        finally
        {
            TempRoots.Delete(bundledRoot);
        }
    }

    /// <summary>The directory under the host's data directory the main program extracts packages into.</summary>
    private static string InstallRoot(E2eFixture fx) =>
        Path.Combine(fx.DataDirectory, ExtensionInstallPaths.TempDirectoryName);

    private static Task<Sdk.ExtensionInstallResult> InstallAsync(
        E2eFixture fx,
        string sourceDir,
        string origin,
        CancellationToken ct = default) =>
        fx.Client.InstallExtensionAsync(new Sdk.ExtensionInstallRequest(sourceDir, origin), ct);

    /// <summary>Writes one extracted package (a <c>manifest.json</c> plus its source) and returns the directory.</summary>
    private static string WritePackage(
        string directory,
        string id,
        string version = "1.0.0",
        string? source = null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            $$"""{ "id": "{{id}}", "version": "{{version}}", "source": "Plugin.cs" }""");
        File.WriteAllText(
            Path.Combine(directory, "Plugin.cs"),
            source ?? PluginSources.Standard(id, "x", version));
        return directory;
    }

    /// <summary>An empty directory that stands in for "the bundled plugins shipped next to the app".</summary>
    private static string NewBundledRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "forge-bundled-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Writes one plugin into the bundled root; the host copies it into the plugins directory.</summary>
    private static string WriteBundled(string root, string id, string source)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            $$"""{ "id": "{{id}}", "version": "1.0.0", "source": "Plugin.cs" }""");
        File.WriteAllText(Path.Combine(directory, "Plugin.cs"), source);
        return directory;
    }

    /// <summary>The record for one plugin version, waited for so a host that is still discovering is not a failure.</summary>
    private static async Task<ApprovalRecord> WaitForRecordAsync(RestartableHost host, string id)
    {
        ApprovalRecord? record = null;
        await host.WaitUntilAsync(
            () => (record = ApprovalDatabase.ReadRecord(host.DataDirectory, id, "1.0.0")) is not null,
            $"the approval record of '{id}'");
        return record!;
    }

    /// <summary>True when both file-tree snapshots carry the same relative paths and digests.</summary>
    private static bool SameTree(Dictionary<string, string> before, Dictionary<string, string> after) =>
        before.Count == after.Count &&
        before.All(pair => after.TryGetValue(pair.Key, out var hash) && string.Equals(hash, pair.Value, StringComparison.Ordinal));
}
