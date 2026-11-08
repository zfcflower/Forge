using Forge.Editor.Languages;

namespace Forge.Editor.Tests.Languages;

/// <summary>
/// Pins automatic language-server discovery: only existing commands count, extension and installation folders are bounded,
/// the newest version wins, and an explicit <c>languages.json</c> choice is never overridden.
/// </summary>
public sealed class LanguageServerDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "forge-lsp-discovery-" + Guid.NewGuid().ToString("N"));

    public LanguageServerDiscoveryTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void A_command_on_the_path_is_used_with_its_declared_arguments()
    {
        var found = LanguageServerDiscovery.Discover(Options(path: new() { ["clangd"] = "/usr/bin/clangd" }));

        var cpp = Assert.Single(found, server => server.LanguageId == "cpp");
        Assert.Equal("/usr/bin/clangd", cpp.Server.Command);
        Assert.Empty(cpp.Server.Arguments);
        Assert.Equal("PATH", cpp.Origin);
        Assert.Contains(".cpp", cpp.Extensions);
    }

    [Fact]
    public void The_first_path_candidate_wins_for_a_language()
    {
        var found = LanguageServerDiscovery.Discover(Options(path: new()
        {
            ["csharp-ls"] = "/bin/csharp-ls",
            ["roslyn-language-server"] = "/bin/roslyn-language-server",
        }));

        var csharp = Assert.Single(found, server => server.LanguageId == "csharp");
        Assert.Equal("/bin/roslyn-language-server", csharp.Server.Command);
        Assert.Equal(["--stdio"], csharp.Server.Arguments);
    }

    [Fact]
    public void A_server_inside_a_vs_code_extension_is_launched_through_dotnet()
    {
        var extension = Path.Combine(root, "extensions", "ms-dotnettools.csharp-2.30.0");
        Directory.CreateDirectory(extension);
        var assembly = Path.Combine(extension, "Microsoft.CodeAnalysis.LanguageServer.dll");
        File.WriteAllText(assembly, string.Empty);

        var found = LanguageServerDiscovery.Discover(Options(path: new() { ["dotnet"] = "/bin/dotnet" }));

        var csharp = Assert.Single(found, server => server.LanguageId == "csharp");
        Assert.Equal("/bin/dotnet", csharp.Server.Command);
        // Roslyn only loads a project when it is told to; without that a .cs file is a miscellaneous file with no analysis.
        Assert.Equal(
            [assembly, "--stdio", "--logLevel", "Information", "--autoLoadProjects", LanguageServerDiscovery.RoslynAutoLoadProjects.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            csharp.Server.Arguments);
        Assert.Equal("VS Code", csharp.Origin);
    }

    [Fact]
    public void Without_the_launcher_the_extension_is_not_used()
    {
        var extension = Path.Combine(root, "extensions", "ms-dotnettools.csharp-2.30.0");
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Combine(extension, "Microsoft.CodeAnalysis.LanguageServer.dll"), string.Empty);

        var found = LanguageServerDiscovery.Discover(Options(path: []));

        Assert.DoesNotContain(found, server => server.LanguageId == "csharp");
    }

    [Fact]
    public void The_newest_extension_version_is_preferred()
    {
        foreach (var version in new[] { "2.9.0", "2.30.0", "2.100.1" })
        {
            var directory = Path.Combine(root, "extensions", "ms-dotnettools.csharp-" + version);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "Microsoft.CodeAnalysis.LanguageServer.dll"), string.Empty);
        }

        var found = LanguageServerDiscovery.Discover(Options(path: new() { ["dotnet"] = "/bin/dotnet" }));

        var csharp = Assert.Single(found, server => server.LanguageId == "csharp");
        Assert.Contains("ms-dotnettools.csharp-2.100.1", string.Join(";", csharp.Server.Arguments), StringComparison.Ordinal);
    }

    [Fact]
    public void A_versioned_editor_installation_is_found_one_level_deep()
    {
        var app = Path.Combine(root, "app", "f3ab91c2", "resources", "app");
        var script = Path.Combine(app, "extensions", "html-language-features", "server", "dist", "node", "htmlServerMain.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, string.Empty);

        var found = LanguageServerDiscovery.Discover(Options(
            path: new() { ["node"] = "/bin/node" },
            installations: [Path.Combine(root, "app")]));

        var html = Assert.Single(found, server => server.LanguageId == "html");
        Assert.Equal([script, "--stdio"], html.Server.Arguments);
    }

    [Fact]
    public void A_typescript_server_is_told_where_tsserver_lives()
    {
        var app = Path.Combine(root, "app", "resources", "app");
        var tsserver = Path.Combine(app, "extensions", "node_modules", "typescript", "lib", "tsserver.js");
        Directory.CreateDirectory(Path.GetDirectoryName(tsserver)!);
        File.WriteAllText(tsserver, string.Empty);

        var found = LanguageServerDiscovery.Discover(Options(
            path: new() { ["typescript-language-server"] = "/bin/typescript-language-server" },
            installations: [Path.Combine(root, "app")]));

        var typescript = Assert.Single(found, server => server.LanguageId == "typescript");
        Assert.NotNull(typescript.InitializationOptions);
        Assert.Equal(tsserver, typescript.InitializationOptions!["tsserver"]!["path"]!.GetValue<string>());

        // JavaScript is the same server, so it carries the same options.
        var javascript = Assert.Single(found, server => server.LanguageId == "javascript");
        Assert.Equal(tsserver, javascript.InitializationOptions!["tsserver"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void Without_a_tsserver_the_typescript_server_is_left_on_its_own()
    {
        var found = LanguageServerDiscovery.Discover(Options(
            path: new() { ["typescript-language-server"] = "/bin/typescript-language-server" }));

        var typescript = Assert.Single(found, server => server.LanguageId == "typescript");
        Assert.Null(typescript.InitializationOptions);
    }

    [Fact]
    public void The_npm_global_layout_supplies_tsserver_next_to_the_server_shim()
    {
        var prefix = Path.Combine(root, "npm");
        var shim = Path.Combine(prefix, "typescript-language-server.cmd");
        Directory.CreateDirectory(prefix);
        File.WriteAllText(shim, string.Empty);
        var tsserver = Path.Combine(prefix, "node_modules", "typescript", "lib", "tsserver.js");
        Directory.CreateDirectory(Path.GetDirectoryName(tsserver)!);
        File.WriteAllText(tsserver, string.Empty);

        var found = LanguageServerDiscovery.Discover(Options(path: new() { ["typescript-language-server"] = shim }));

        var typescript = Assert.Single(found, server => server.LanguageId == "typescript");
        Assert.Equal(tsserver, typescript.InitializationOptions!["tsserver"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void A_server_shipped_with_the_editor_installation_runs_on_node()
    {
        var script = Path.Combine(root, "app", "extensions", "json-language-features", "server", "dist", "node", "jsonServerMain.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, string.Empty);

        var found = LanguageServerDiscovery.Discover(Options(
            path: new() { ["node"] = "/bin/node" },
            installations: [Path.Combine(root, "app")]));

        var json = Assert.Single(found, server => server.LanguageId == "json");
        Assert.Equal("/bin/node", json.Server.Command);
        Assert.Equal([script, "--stdio"], json.Server.Arguments);
        Assert.Equal("VS Code", json.Origin);
    }

    [Fact]
    public void A_missing_root_is_skipped_without_throwing() =>
        Assert.Empty(LanguageServerDiscovery.Discover(Options(path: [], extensions: [Path.Combine(root, "nope")])));

    [Fact]
    public async Task A_language_that_was_never_configured_gets_the_discovered_server_and_its_extensions()
    {
        var registry = await LanguageRegistry.LoadAsync(userConfigPath: Path.Combine(root, "languages.json"));
        Assert.Null(registry.DetectLanguageId("script.py"));

        var merged = registry.WithDiscoveredServers(
        [
            new DiscoveredLanguageServer(
                "python",
                [".py", ".pyi"],
                [],
                new LanguageServerConfig("/bin/pyright-langserver", ["--stdio"], new Dictionary<string, string>()),
                "PATH"),
        ]);

        var python = merged.GetById("python");
        Assert.NotNull(python);
        Assert.Equal("/bin/pyright-langserver", python!.Server!.Command);
        Assert.Equal("PATH", python.ServerOrigin);
        Assert.Equal("python", merged.DetectLanguageId("script.py"));
        Assert.Equal("Python", python.DisplayName);
    }

    [Fact]
    public async Task An_explicit_configuration_wins_over_discovery()
    {
        var config = Path.Combine(root, "languages.json");
        await File.WriteAllTextAsync(config, """
            {"languages":{"csharp":{"server":{"command":"my-own-lsp","args":["--stdio"]}}}}
            """);
        var registry = await LanguageRegistry.LoadAsync(userConfigPath: config);
        Assert.Contains("csharp", registry.ConfiguredLanguageIds);

        var merged = registry.WithDiscoveredServers(
        [
            new DiscoveredLanguageServer(
                "csharp",
                [".cs"],
                [],
                new LanguageServerConfig("/bin/roslyn-language-server", ["--stdio"], new Dictionary<string, string>()),
                "PATH"),
        ]);

        Assert.Equal("my-own-lsp", merged.GetById("csharp")!.Server!.Command);
        Assert.Null(merged.GetById("csharp")!.ServerOrigin);
    }

    [Fact]
    public async Task A_language_configured_without_a_server_is_left_alone()
    {
        var config = Path.Combine(root, "languages.json");
        await File.WriteAllTextAsync(config, """
            {"languages":{"csharp":{"server":null}}}
            """);
        var registry = await LanguageRegistry.LoadAsync(userConfigPath: config);

        var merged = registry.WithDiscoveredServers(
        [
            new DiscoveredLanguageServer(
                "csharp",
                [".cs"],
                [],
                new LanguageServerConfig("/bin/dotnet", [], new Dictionary<string, string>()),
                "PATH"),
        ]);

        Assert.Null(merged.GetById("csharp")!.Server);
    }

    [Fact]
    public async Task Discovery_keeps_the_load_errors_and_the_default_language()
    {
        var registry = await LanguageRegistry.LoadAsync(userConfigPath: Path.Combine(root, "missing.json"));

        var merged = registry.WithDiscoveredServers([]);

        Assert.Empty(merged.LoadErrors);
        Assert.Equal("csharp", Assert.Single(merged.Languages).Id);
    }

    [Fact]
    public void The_extension_table_answers_for_languages_that_are_not_configured_yet()
    {
        Assert.Contains(".py", LanguageServerDiscovery.ExtensionsFor("python"));
        Assert.Empty(LanguageServerDiscovery.ExtensionsFor("unknown-language"));
    }

    [Fact]
    public async Task A_broken_configuration_file_still_lets_discovery_work()
    {
        var config = Path.Combine(root, "languages.json");
        await File.WriteAllTextAsync(config, "{ not json");
        var registry = await LanguageRegistry.LoadAsync(userConfigPath: config);
        Assert.NotEmpty(registry.LoadErrors);

        var merged = registry.WithDiscoveredServers(
        [
            new DiscoveredLanguageServer(
                "lua",
                [".lua"],
                [],
                new LanguageServerConfig("/bin/lua-language-server", [], new Dictionary<string, string>()),
                "PATH"),
        ]);

        Assert.Equal("lua", merged.DetectLanguageId("x.lua"));
        Assert.NotEmpty(merged.LoadErrors);
    }

    [Fact]
    public async Task A_language_disabled_in_the_configuration_is_not_resurrected_by_discovery()
    {
        var config = Path.Combine(root, "languages.json");
        await File.WriteAllTextAsync(config, """
            {"languages":{"csharp":{"disabled":true}}}
            """);
        var registry = await LanguageRegistry.LoadAsync(userConfigPath: config);
        Assert.Null(registry.GetById("csharp"));
        Assert.Contains("csharp", registry.ConfiguredLanguageIds);

        var merged = registry.WithDiscoveredServers(
        [
            new DiscoveredLanguageServer(
                "csharp",
                [".cs", ".csx"],
                [],
                new LanguageServerConfig("/bin/dotnet", [], new Dictionary<string, string>()),
                "PATH"),
        ]);

        Assert.Null(merged.GetById("csharp"));
        Assert.Null(merged.DetectLanguageId("Program.cs"));
    }

    private LanguageServerDiscoveryOptions Options(
        Dictionary<string, string> path,
        IReadOnlyList<string>? extensions = null,
        IReadOnlyList<string>? installations = null) =>
        new(
            extensions ?? [Path.Combine(root, "extensions")],
            installations ?? [Path.Combine(root, "app")],
            command => path.TryGetValue(command, out var resolved) ? resolved : null);
}
