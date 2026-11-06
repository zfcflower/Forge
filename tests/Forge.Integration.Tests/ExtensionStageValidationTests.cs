using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Forge.HostClient;
using Forge.Ipc;
using Xunit;
using Sdk = Forge.Plugin.Sdk;

namespace Forge.Integration.Tests;

/// <summary>
/// <c>extensions.stage</c> validation matrix: every illegal draft fails with <c>invalid_extension</c> and
/// leaves the plugins tree byte-identical; exact size limits pass, one byte over fails.
/// </summary>
public sealed class ExtensionStageValidationTests
{
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxDraftBytes = 1024 * 1024;

    [Theory]
    [InlineData("../evil.cs")]
    [InlineData("a/../../evil.cs")]
    [InlineData("..")]
    public async Task Path_traversal_rejected_and_disk_unchanged(string path)
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "trav",
            files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [path] = PluginSources.Standard("trav"),
            });

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", path);
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "traversal stage");
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.PendingDirectory(fx.PluginsDirectory, "trav")));
    }

    [Theory]
    [InlineData("/absolute.cs")]
    [InlineData("C:/absolute.cs")]
    public async Task Absolute_path_rejected_and_disk_unchanged(string path)
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "abs",
            files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [path] = PluginSources.Standard("abs"),
            });

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "absolute-path stage");
    }

    [Theory]
    [InlineData("docs//notes.md")]
    [InlineData("a/./b.cs")]
    public async Task Empty_or_dot_segment_rejected_and_disk_unchanged(string path)
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "seg",
            files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [path] = "content",
                ["Plugin.cs"] = PluginSources.Standard("seg"),
            });

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "empty-segment stage");
    }

    [Fact]
    public async Task Backslash_path_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "slash",
            files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sub\\file.cs"] = PluginSources.Standard("slash"),
            });

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "\\");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "backslash stage");
    }

    [Theory]
    [InlineData("run.exe")]
    [InlineData("notes.txt")]
    [InlineData("noextension")]
    [InlineData("lib.dll")]
    public async Task Non_whitelisted_extension_rejected_and_disk_unchanged(string path)
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "ext",
            files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Plugin.cs"] = PluginSources.Standard("ext"),
                [path] = "payload",
            });

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "allowed");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "extension stage");
    }

    [Theory]
    [InlineData("manifest.json")]
    [InlineData("sub/manifest.json")]
    [InlineData("MANIFEST.JSON")]
    public async Task Author_manifest_rejected_and_disk_unchanged(string path)
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "man",
            files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Plugin.cs"] = PluginSources.Standard("man"),
                [path] = """{"id":"man","version":"1.0.0"}""",
            });

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "manifest.json");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "manifest stage");
    }

    [Fact]
    public async Task Single_file_at_exactly_256KiB_passes()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Plugin.cs"] = PluginSources.Standard("exact1"),
            ["notes.md"] = new string('a', MaxFileBytes),
        };
        Assert.Equal(MaxFileBytes, Encoding.UTF8.GetByteCount(files["notes.md"]));

        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("exact1", files: files), fx.Ct);

        Assert.Empty(staged.Diagnostics);
        Assert.False(string.IsNullOrWhiteSpace(staged.SourceHash));
        Assert.True(Directory.Exists(ExtensionAuthoringSupport.PendingDirectory(fx.PluginsDirectory, "exact1")));
    }

    [Fact]
    public async Task Single_file_one_byte_over_256KiB_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Plugin.cs"] = PluginSources.Standard("over1"),
            ["notes.md"] = new string('a', MaxFileBytes + 1),
        };
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(
                ExtensionAuthoringSupport.StagePluginRequest("over1", files: files), fx.Ct),
            "invalid_extension",
            "262144");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "oversize-file stage");
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.PendingDirectory(fx.PluginsDirectory, "over1")));
    }

    [Fact]
    public async Task Multibyte_file_at_exactly_256KiB_passes()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        // '中' is 3 UTF-8 bytes: 87381 * 3 + 1 ASCII byte = exactly 262144 bytes.
        var text = new string('中', 87381) + "a";
        Assert.Equal(MaxFileBytes, Encoding.UTF8.GetByteCount(text));
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Plugin.cs"] = PluginSources.Standard("exactmb"),
            ["notes.md"] = text,
        };

        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("exactmb", files: files), fx.Ct);

        Assert.Empty(staged.Diagnostics);
    }

    [Fact]
    public async Task Total_at_exactly_1MiB_passes()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var files = SizedDraft("exactT", MaxDraftBytes);

        var staged = await fx.Client.StageExtensionAsync(
            ExtensionAuthoringSupport.StagePluginRequest("exactT", files: files), fx.Ct);

        Assert.Empty(staged.Diagnostics);
    }

    [Fact]
    public async Task Total_one_byte_over_1MiB_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var files = SizedDraft("overT", MaxDraftBytes + 1);
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(
                ExtensionAuthoringSupport.StagePluginRequest("overT", files: files), fx.Ct),
            "invalid_extension",
            "1");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "oversize-draft stage");
        Assert.False(Directory.Exists(ExtensionAuthoringSupport.PendingDirectory(fx.PluginsDirectory, "overT")));
    }

    [Fact]
    public async Task Case_insensitive_duplicate_path_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "dup",
            files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Plugin.cs"] = PluginSources.Standard("dup"),
                ["PLUGIN.cs"] = PluginSources.Standard("dup"),
            });

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "twice");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "duplicate-path stage");
    }

    [Fact]
    public async Task Unknown_permission_kind_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "perm",
            permissions: [new Sdk.Permission((Sdk.PermissionKind)99)]);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "permission");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "permission stage");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_permission_scope_rejected_and_disk_unchanged(string scope)
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "scope",
            permissions: [new Sdk.Permission(Sdk.PermissionKind.ReadFile, scope)]);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "scope");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "scope stage");
    }

    [Fact]
    public async Task Duplicate_permissions_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "dupperm",
            permissions:
            [
                new Sdk.Permission(Sdk.PermissionKind.ReadFile, "docs"),
                new Sdk.Permission(Sdk.PermissionKind.ReadFile, "docs"),
            ]);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "twice");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "duplicate-permission stage");
    }

    [Fact]
    public async Task Duplicate_scopeless_permissions_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "dupperm2",
            permissions:
            [
                new Sdk.Permission(Sdk.PermissionKind.Network),
                new Sdk.Permission(Sdk.PermissionKind.Network),
            ]);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "twice");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "duplicate-scopeless stage");
    }

    [Fact]
    public async Task Plugin_without_cs_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "nocs",
            files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["notes.md"] = "just notes",
            });

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "exactly one");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "zero-cs stage");
    }

    [Fact]
    public async Task Plugin_with_two_cs_files_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "twocs",
            files: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Plugin.cs"] = PluginSources.Standard("twocs"),
                ["Extra.cs"] = PluginSources.Standard("twocs"),
            });

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "exactly one");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "two-cs stage");
    }

    [Fact]
    public async Task Workflow_without_workflow_json_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = new Sdk.ExtensionStageRequest(
            "nowf",
            "1.0.0",
            Sdk.ExtensionKinds.Workflow,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["notes.md"] = "no workflow here" },
            [],
            [],
            ExtensionAuthoringSupport.WorkflowTool("run-it"));

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "workflow.json");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "missing-workflow stage");
    }

    [Fact]
    public async Task Workflow_without_tool_rejected_and_disk_unchanged()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = new Sdk.ExtensionStageRequest(
            "notool",
            "1.0.0",
            Sdk.ExtensionKinds.Workflow,
            ExtensionAuthoringSupport.WorkflowFiles(ExtensionAuthoringSupport.SingleStepWorkflow("probe")),
            [],
            [],
            Tool: null);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "tool");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "missing-tool stage");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a..b")]
    [InlineData(".hidden")]
    public async Task Workflow_with_illegal_tool_name_rejected_and_disk_unchanged(string name)
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StageWorkflowRequest("badtool", "{}", toolName: name);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "tool name");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "bad-tool-name stage");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"just-a-string\"")]
    [InlineData("42")]
    [InlineData("{oops")]
    [InlineData("")]
    public async Task Workflow_with_non_object_schema_rejected_and_disk_unchanged(string schema)
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory);

        var request = ExtensionAuthoringSupport.StageWorkflowRequest(
            "badschema", ExtensionAuthoringSupport.SingleStepWorkflow("probe"), inputSchema: schema);

        await ExtensionAuthoringSupport.AssertHostErrorAsync(
            () => fx.Client.StageExtensionAsync(request, fx.Ct), "invalid_extension", "inputSchema");
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(fx.PluginsDirectory), "bad-schema stage");
    }

    [Fact]
    public async Task Unknown_kind_rejected_over_the_wire_and_disk_unchanged()
    {
        await using var raw = await RawHost.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(raw.PluginsDirectory);

        var args = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["pluginId"] = "weird",
            ["version"] = "1.0.0",
            ["kind"] = "applet",
            ["files"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Plugin.cs"] = PluginSources.Standard("weird"),
            },
        });

        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => raw.Peer.SendRequestAsync(Sdk.ExtensionProtocol.ExtensionsStage, args, raw.Ct));
        Assert.Equal("invalid_extension", error.Code);
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(raw.PluginsDirectory), "unknown-kind stage");
        Assert.False(Directory.Exists(Path.Combine(raw.PluginsDirectory, ".pending", "weird")));
    }

    [Fact]
    public async Task Blank_path_rejected_over_the_wire_and_disk_unchanged()
    {
        await using var raw = await RawHost.StartAsync();
        var before = ExtensionAuthoringSupport.SnapshotTree(raw.PluginsDirectory);

        var args = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["pluginId"] = "blank",
            ["version"] = "1.0.0",
            ["kind"] = Sdk.ExtensionKinds.Plugin,
            ["files"] = new Dictionary<string, string?>(StringComparer.Ordinal) { [""] = "x" },
        });

        var error = await Assert.ThrowsAsync<IpcRemoteException>(
            () => raw.Peer.SendRequestAsync(Sdk.ExtensionProtocol.ExtensionsStage, args, raw.Ct));
        Assert.Equal("invalid_extension", error.Code);
        ExtensionAuthoringSupport.AssertSameTree(
            before, ExtensionAuthoringSupport.SnapshotTree(raw.PluginsDirectory), "blank-path stage");
    }

    [Fact]
    public async Task Typed_client_rejects_unknown_kind_before_the_wire()
    {
        await using var fx = new E2eFixture();
        var request = ExtensionAuthoringSupport.StagePluginRequest("x") with { Kind = "applet" };

        await Assert.ThrowsAsync<ArgumentException>(() => fx.Client.StageExtensionAsync(request, fx.Ct));
    }

    [Fact]
    public async Task Typed_client_rejects_blank_path_before_the_wire()
    {
        await using var fx = new E2eFixture();
        var request = ExtensionAuthoringSupport.StagePluginRequest(
            "x",
            files: new Dictionary<string, string>(StringComparer.Ordinal) { [""] = "x" });

        await Assert.ThrowsAsync<ArgumentException>(() => fx.Client.StageExtensionAsync(request, fx.Ct));
    }

    /// <summary>
    /// Builds a plugin draft whose UTF-8 total is exactly <paramref name="totalBytes"/>: one small source
    /// plus four markdown files that each stay under the single-file limit.
    /// </summary>
    private static Dictionary<string, string> SizedDraft(string id, int totalBytes)
    {
        var source = PluginSources.Standard(id);
        var rest = totalBytes - Encoding.UTF8.GetByteCount(source);
        Assert.True(rest > 0 && rest < 4 * MaxFileBytes);
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["Plugin.cs"] = source };
        for (var i = 0; i < 4; i++)
        {
            var size = rest / 4 + (i < rest % 4 ? 1 : 0);
            files[$"part{i}.md"] = new string((char)('a' + i), size);
        }

        var actual = files.Sum(entry => Encoding.UTF8.GetByteCount(entry.Value));
        Assert.Equal(totalBytes, actual);
        foreach (var name in files.Keys.Where(key => key != "Plugin.cs"))
        {
            Assert.True(Encoding.UTF8.GetByteCount(files[name]) <= MaxFileBytes);
        }

        return files;
    }

    /// <summary>Minimal raw host: a real Extension Host process plus a scripted main-program peer.</summary>
    private sealed class RawHost : IAsyncDisposable
    {
        private readonly Process process;
        private readonly NamedPipeClientTransport transport;

        private RawHost(Process process, NamedPipeClientTransport transport, IpcPeer peer, string root)
        {
            this.process = process;
            this.transport = transport;
            Peer = peer;
            Root = root;
            PluginsDirectory = Path.Combine(root, "plugins");
        }

        public IpcPeer Peer { get; }

        public string Root { get; }

        public string PluginsDirectory { get; }

        public CancellationToken Ct => budget.Token;

        private readonly CancellationTokenSource budget = new(TimeSpan.FromSeconds(120));

        public static async Task<RawHost> StartAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "forge-raw-" + Guid.NewGuid().ToString("N")[..10]);
            var plugins = Path.Combine(root, "plugins");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(plugins);
            Directory.CreateDirectory(data);
            var pipe = "forge-raw-" + Guid.NewGuid().ToString("N")[..16];
            var info = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add(E2eFixture.HostAssemblyPath);
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add(pipe);
            info.ArgumentList.Add("--plugins");
            info.ArgumentList.Add(plugins);
            info.ArgumentList.Add("--data");
            info.ArgumentList.Add(data);
            var process = Process.Start(info) ?? throw new InvalidOperationException("The raw host did not start.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var budget = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            NamedPipeClientTransport? transport = null;
            while (true)
            {
                var candidate = new NamedPipeClientTransport(pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(2) });
                try
                {
                    await candidate.StartAsync(budget.Token);
                    transport = candidate;
                    break;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException)
                {
                    await candidate.DisposeAsync();
                    await Task.Delay(TimeSpan.FromMilliseconds(50), budget.Token);
                }
            }

            var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
            await peer.StartAsync(budget.Token);
            return new RawHost(process, transport, peer, root);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Peer.StopAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
            {
            }

            await Peer.DisposeAsync();
            await transport.DisposeAsync();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
            process.Dispose();
            budget.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
