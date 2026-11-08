namespace Forge.Editor.Languages;

/// <summary>
/// One usable language server found on this machine: the language it serves and how to start it. <see cref="Origin"/> is
/// where it came from ("PATH", "VS Code", …), which is what the editor shows so the user knows why a feature suddenly
/// works.
/// </summary>
public sealed record DiscoveredLanguageServer(
    string LanguageId,
    IReadOnlyList<string> Extensions,
    IReadOnlyList<string> FileNames,
    LanguageServerConfig Server,
    string Origin,
    System.Text.Json.Nodes.JsonNode? InitializationOptions = null);

/// <summary>Where discovery looks. The defaults are this machine; a test passes a temporary tree instead.</summary>
public sealed record LanguageServerDiscoveryOptions(
    IReadOnlyList<string> ExtensionRoots,
    IReadOnlyList<string> InstallationRoots,
    Func<string, string?>? PathResolver = null)
{
    /// <summary>
    /// The real search space: VS Code / Cursor extension and application folders of the current user, plus <c>PATH</c>.
    /// Nothing is downloaded, started or modified — discovery only decides whether a command exists.
    /// </summary>
    public static LanguageServerDiscoveryOptions Default => new(DefaultExtensionRoots(), DefaultInstallationRoots());

    private static IReadOnlyList<string> DefaultExtensionRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return [];
        }

        return
        [
            Path.Combine(home, ".vscode", "extensions"),
            Path.Combine(home, ".vscode-insiders", "extensions"),
            Path.Combine(home, ".vscode-server", "extensions"),
            Path.Combine(home, ".vscode-oss", "extensions"),
            Path.Combine(home, ".cursor", "extensions"),
        ];
    }

    /// <summary>
    /// Installation folders, not their <c>resources/app</c> inside: the layout differs per installer and version (newer Windows
    /// installers add a version folder, e.g. <c>Microsoft VS Code/&lt;hash&gt;/resources/app</c>), so
    /// <see cref="ApplicationRoots"/> expands each base into the app roots that exist.
    /// </summary>
    private static IReadOnlyList<string> DefaultInstallationRoots()
    {
        var roots = new List<string>(6);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
        {
            roots.Add(Path.Combine(local, "Programs", "Microsoft VS Code"));
            roots.Add(Path.Combine(local, "Programs", "cursor"));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            roots.Add(Path.Combine(programFiles, "Microsoft VS Code"));
        }

        roots.Add("/Applications/Visual Studio Code.app/Contents");
        roots.Add("/usr/share/code");
        roots.Add("/usr/lib/code");
        roots.Add("/opt/visual-studio-code");
        roots.Add("/snap/code/current/usr/share/code");
        return roots;
    }
}

/// <summary>
/// Finds language servers that are already installed. Bounded and offline: a fixed table of commands on <c>PATH</c>, the
/// servers shipped inside VS Code / Cursor extension folders, and the JSON/CSS/HTML servers shipped with a VS Code
/// installation. Directories that do not exist are skipped, at most
/// <see cref="MaxExtensionVersions"/> versions per extension are examined, and every candidate is only checked for
/// existence — nothing is executed here.
/// </summary>
public static class LanguageServerDiscovery
{
    /// <summary>Most versions of one extension that are scanned; an extension folder keeps a handful, not hundreds.</summary>
    public const int MaxExtensionVersions = 32;

    /// <summary>Most sub-folders of an installation folder that are examined for a nested <c>resources/app</c>.</summary>
    public const int MaxInstallationChildren = 8;

    /// <summary>Upper bound of projects the Roslyn server auto-loads for one workspace.</summary>
    public const int RoslynAutoLoadProjects = 100;

    private const string RoslynAssembly = "Microsoft.CodeAnalysis.LanguageServer.dll";

    /// <summary>Every server this build knows how to start, whether it exists or not.</summary>
    public static IReadOnlyList<DiscoveredLanguageServer> Discover(LanguageServerDiscoveryOptions? options = null)
    {
        var search = options ?? LanguageServerDiscoveryOptions.Default;
        var resolve = search.PathResolver ?? ResolveOnPath;
        var found = new List<DiscoveredLanguageServer>(12);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in PathCandidates)
        {
            if (taken.Contains(candidate.LanguageId) || resolve(candidate.Command) is not { } path)
            {
                continue;
            }

            taken.Add(candidate.LanguageId);
            found.Add(new DiscoveredLanguageServer(
                candidate.LanguageId,
                candidate.Extensions,
                candidate.FileNames,
                new LanguageServerConfig(path, candidate.Arguments, new Dictionary<string, string>()),
                "PATH"));
        }

        DiscoverExtensionServers(search, resolve, found, taken);
        DiscoverApplicationServers(search, resolve, found, taken);
        AttachTypeScriptServerPath(search, resolve, found);
        return found;
    }

    /// <summary>
    /// <c>typescript-language-server</c> exits with "Could not find a valid TypeScript installation" unless it is told where
    /// <c>tsserver.js</c> lives (a workspace-local <c>node_modules</c> is its only other source). The path is looked up next
    /// to the server binary, in the npm global prefix, and in the editor installations; when nothing is found the server is
    /// left without options so a workspace-local TypeScript can still serve it.
    /// </summary>
    private static void AttachTypeScriptServerPath(
        LanguageServerDiscoveryOptions search,
        Func<string, string?> resolve,
        List<DiscoveredLanguageServer> found)
    {
        string? tsserver = null;
        for (var index = 0; index < found.Count; index++)
        {
            var server = found[index];
            if (server.LanguageId is not ("typescript" or "javascript"))
            {
                continue;
            }

            tsserver ??= FindTsserver(server.Server.Command, search, resolve);
            if (tsserver is null)
            {
                continue;
            }

            found[index] = server with
            {
                InitializationOptions = new System.Text.Json.Nodes.JsonObject
                {
                    ["tsserver"] = new System.Text.Json.Nodes.JsonObject { ["path"] = tsserver },
                },
            };
        }
    }

    /// <summary>The <c>tsserver.js</c> a TypeScript server should use, or null when this machine has none.</summary>
    private static string? FindTsserver(string serverPath, LanguageServerDiscoveryOptions search, Func<string, string?> resolve)
    {
        foreach (var candidate in TsserverCandidates(serverPath, search))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return resolve("tsserver") is { } onPath ? onPath : null;
    }

    private static IEnumerable<string> TsserverCandidates(string serverPath, LanguageServerDiscoveryOptions search)
    {
        // The npm global layout: <prefix>/typescript-language-server.CMD next to <prefix>/node_modules/typescript/lib/tsserver.js.
        var directory = Path.GetDirectoryName(serverPath);
        for (var depth = 0; directory is { Length: > 0 } && depth < 4; depth++)
        {
            yield return Path.Combine(directory, "node_modules", "typescript", "lib", "tsserver.js");
            yield return Path.Combine(directory, "typescript", "lib", "tsserver.js");
            directory = Path.GetDirectoryName(directory);
        }

        foreach (var appRoot in ApplicationRoots(search))
        {
            yield return Path.Combine(appRoot, "extensions", "node_modules", "typescript", "lib", "tsserver.js");
            yield return Path.Combine(appRoot, "node_modules", "typescript", "lib", "tsserver.js");
        }
    }

    /// <summary>The language table for a discovered id, so a server found for an unconfigured language still gets files.</summary>
    public static IReadOnlyList<string> ExtensionsFor(string languageId) =>
        Table.FirstOrDefault(entry => string.Equals(entry.LanguageId, languageId, StringComparison.OrdinalIgnoreCase))?.Extensions ?? [];

    private static void DiscoverExtensionServers(
        LanguageServerDiscoveryOptions search,
        Func<string, string?> resolve,
        List<DiscoveredLanguageServer> found,
        HashSet<string> taken)
    {
        foreach (var root in search.ExtensionRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var candidate in ExtensionCandidates)
            {
                if (taken.Contains(candidate.LanguageId))
                {
                    continue;
                }

                foreach (var directory in VersionsNewestFirst(root, candidate.FolderPrefix))
                {
                    var launch = candidate.Launch(directory, resolve);
                    if (launch is null)
                    {
                        continue;
                    }

                    taken.Add(candidate.LanguageId);
                    found.Add(new DiscoveredLanguageServer(
                        candidate.LanguageId,
                        candidate.Extensions,
                        candidate.FileNames,
                        launch,
                        "VS Code"));
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Every <c>resources/app</c> an installation base can hold: the base itself, <c>&lt;base&gt;/resources/app</c>, and one level
    /// of version folders (<c>&lt;base&gt;/&lt;version&gt;/resources/app</c>), which is what the current Windows installer creates.
    /// </summary>
    private static IEnumerable<string> ApplicationRoots(LanguageServerDiscoveryOptions search)
    {
        foreach (var root in search.InstallationRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            yield return root;

            var classic = Path.Combine(root, "resources", "app");
            if (Directory.Exists(classic))
            {
                yield return classic;
            }

            string[] children;
            try
            {
                children = Directory.GetDirectories(root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                children = [];
            }

            Array.Sort(children, static (left, right) => string.CompareOrdinal(right, left));
            var probed = 0;
            foreach (var child in children)
            {
                if (probed++ >= MaxInstallationChildren)
                {
                    break;
                }

                var nested = Path.Combine(child, "resources", "app");
                if (Directory.Exists(nested))
                {
                    yield return nested;
                }
            }
        }
    }

    private static void DiscoverApplicationServers(
        LanguageServerDiscoveryOptions search,
        Func<string, string?> resolve,
        List<DiscoveredLanguageServer> found,
        HashSet<string> taken)
    {
        foreach (var root in ApplicationRoots(search))
        {
            foreach (var candidate in ApplicationCandidates)
            {
                if (taken.Contains(candidate.LanguageId))
                {
                    continue;
                }

                var script = Path.Combine(root, candidate.RelativeScript);
                if (!File.Exists(script) || resolve(candidate.Launcher) is not { } launcher)
                {
                    continue;
                }

                taken.Add(candidate.LanguageId);
                found.Add(new DiscoveredLanguageServer(
                    candidate.LanguageId,
                    candidate.Extensions,
                    candidate.FileNames,
                    new LanguageServerConfig(
                        launcher,
                        [script, .. candidate.Arguments],
                        new Dictionary<string, string>()),
                    "VS Code"));
                break;
            }
        }
    }

    /// <summary>Extension folders for one prefix, newest version first (the folder name carries the version).</summary>
    private static IEnumerable<string> VersionsNewestFirst(string root, string prefix)
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(root, prefix + "*");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        Array.Sort(directories, static (left, right) => CompareVersions(Path.GetFileName(right), Path.GetFileName(left)));
        return directories.Length <= MaxExtensionVersions ? directories : directories[..MaxExtensionVersions];
    }

    /// <summary>
    /// Compares two extension folder names by the version suffix, numerically when both carry one
    /// (<c>…csharp-2.100.1</c> is newer than <c>…csharp-2.30.0</c>), otherwise ordinally.
    /// </summary>
    private static int CompareVersions(string left, string right)
    {
        var leftVersion = VersionOf(left);
        var rightVersion = VersionOf(right);
        if (leftVersion is not null && rightVersion is not null)
        {
            var comparison = leftVersion.CompareTo(rightVersion);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return string.CompareOrdinal(left, right);
    }

    /// <summary>The trailing version of an extension folder name, or null when it has none.</summary>
    private static Version? VersionOf(string folderName)
    {
        var dash = folderName.LastIndexOf('-');
        if (dash < 0 || dash + 1 >= folderName.Length)
        {
            return null;
        }

        return Version.TryParse(folderName[(dash + 1)..], out var version) ? version : null;
    }

    /// <summary>The default <c>PATH</c> lookup; a bare command is tried with every <c>PATHEXT</c> suffix on Windows.</summary>
    private static string? ResolveOnPath(string command)
    {
        if (command.Length == 0)
        {
            return null;
        }

        if (command.Contains('/') || command.Contains('\\'))
        {
            return File.Exists(command) ? command : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var suffixes = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var suffix in suffixes)
            {
                string full;
                try
                {
                    full = Path.Combine(directory.Trim('"'), command + suffix);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(full))
                {
                    return full;
                }
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- the table

    private sealed record Entry(
        string LanguageId,
        string[] Extensions,
        string[] FileNames,
        string Command,
        string[] Arguments);

    private static readonly string[] CSharpExtensions = [".cs", ".csx"];
    private static readonly string[] TypeScriptExtensions = [".ts", ".mts", ".cts", ".tsx"];
    private static readonly string[] JavaScriptExtensions = [".js", ".mjs", ".cjs", ".jsx"];
    private static readonly string[] JsonExtensions = [".json"];
    private static readonly string[] JsoncExtensions = [".jsonc"];
    private static readonly string[] JsoncFileNames = ["tsconfig.json", "jsconfig.json", "devcontainer.json"];

    private static readonly string[] CssExtensions = [".css", ".scss", ".less"];
    private static readonly string[] HtmlExtensions = [".html", ".htm", ".xhtml"];
    private static readonly string[] PythonExtensions = [".py", ".pyi", ".pyw"];
    private static readonly string[] GoExtensions = [".go"];
    private static readonly string[] RustExtensions = [".rs"];
    private static readonly string[] CppExtensions = [".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh", ".hxx"];
    private static readonly string[] ShellExtensions = [".sh", ".bash", ".zsh"];
    private static readonly string[] YamlExtensions = [".yaml", ".yml"];
    private static readonly string[] LuaExtensions = [".lua"];

    /// <summary>Servers looked for on <c>PATH</c>, in preference order per language.</summary>
    private static readonly Entry[] PathCandidates =
    [
        new("csharp", CSharpExtensions, [], "roslyn-language-server", ["--stdio"]),
        new("csharp", CSharpExtensions, [], "csharp-ls", []),
        new("csharp", CSharpExtensions, [], "OmniSharp", ["-lsp"]),
        new("typescript", TypeScriptExtensions, [], "typescript-language-server", ["--stdio"]),
        new("javascript", JavaScriptExtensions, [], "typescript-language-server", ["--stdio"]),
        new("json", JsonExtensions, [], "vscode-json-language-server", ["--stdio"]),
        new("jsonc", JsoncExtensions, JsoncFileNames, "vscode-json-language-server", ["--stdio"]),
        new("css", CssExtensions, [], "vscode-css-language-server", ["--stdio"]),
        new("html", HtmlExtensions, [], "vscode-html-language-server", ["--stdio"]),
        new("python", PythonExtensions, [], "pyright-langserver", ["--stdio"]),
        new("python", PythonExtensions, [], "pylsp", []),
        new("go", GoExtensions, [], "gopls", []),
        new("rust", RustExtensions, [], "rust-analyzer", []),
        new("cpp", CppExtensions, [], "clangd", []),
        new("shell", ShellExtensions, [], "bash-language-server", ["start"]),
        new("yaml", YamlExtensions, [], "yaml-language-server", ["--stdio"]),
        new("lua", LuaExtensions, [], "lua-language-server", []),
    ];

    /// <summary>Servers that ship inside a VS Code extension folder.</summary>
    private static readonly ExtensionCandidate[] ExtensionCandidates =
    [
        new(
            "csharp",
            CSharpExtensions,
            [],
            "ms-dotnettools.csharp-",
            (directory, resolve) => LaunchFile(directory, resolve, "dotnet", RoslynAssembly, "--stdio", "--logLevel", "Information", "--autoLoadProjects", RoslynAutoLoadProjects.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ?? LaunchFile(directory, resolve, "dotnet", Path.Combine(".roslyn", RoslynAssembly), "--stdio", "--logLevel", "Information", "--autoLoadProjects", RoslynAutoLoadProjects.ToString(System.Globalization.CultureInfo.InvariantCulture))
                ?? LaunchFile(directory, resolve, null, Path.Combine("omnisharp", OperatingSystem.IsWindows() ? "OmniSharp.exe" : "OmniSharp"), "-lsp")),
    ];

    /// <summary>Servers that ship with the VS Code installation itself and run on node.</summary>
    private static readonly ApplicationCandidate[] ApplicationCandidates =
    [
        new("json", JsonExtensions, [], "node", Path.Combine("extensions", "json-language-features", "server", "dist", "node", "jsonServerMain.js"), ["--stdio"]),
        new("jsonc", JsoncExtensions, JsoncFileNames, "node", Path.Combine("extensions", "json-language-features", "server", "dist", "node", "jsonServerMain.js"), ["--stdio"]),
        new("css", CssExtensions, [], "node", Path.Combine("extensions", "css-language-features", "server", "dist", "node", "cssServerMain.js"), ["--stdio"]),
        new("html", HtmlExtensions, [], "node", Path.Combine("extensions", "html-language-features", "server", "dist", "node", "htmlServerMain.js"), ["--stdio"]),
    ];

    /// <summary>The language table used for <see cref="ExtensionsFor"/>; the same data the candidates carry.</summary>
    private static readonly Entry[] Table = PathCandidates;

    private sealed record ExtensionCandidate(
        string LanguageId,
        string[] Extensions,
        string[] FileNames,
        string FolderPrefix,
        Func<string, Func<string, string?>, LanguageServerConfig?> Launch);

    private sealed record ApplicationCandidate(
        string LanguageId,
        string[] Extensions,
        string[] FileNames,
        string Launcher,
        string RelativeScript,
        string[] Arguments);

    /// <summary>Builds a launch for one extension directory, or null when the expected file is missing.</summary>
    private static LanguageServerConfig? LaunchFile(
        string directory,
        Func<string, string?> resolve,
        string? launcher,
        string relativePath,
        params string[] arguments)
    {
        var file = Path.Combine(directory, relativePath);
        if (!File.Exists(file))
        {
            return null;
        }

        if (launcher is null)
        {
            return new LanguageServerConfig(file, arguments, new Dictionary<string, string>());
        }

        return resolve(launcher) is { } resolved
            ? new LanguageServerConfig(resolved, [file, .. arguments], new Dictionary<string, string>())
            : null;
    }
}
