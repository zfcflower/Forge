using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Forge.Approval;
using Forge.Plugin.Sdk;

namespace Forge.ExtensionHost;

/// <summary>
/// One author file of a draft, already validated: a <c>/</c>-separated relative path inside the package and its full text.
/// </summary>
internal sealed record StagedText(string Path, string Text);

/// <summary>
/// A draft that passed every authoring check: the files to write, the manifest the host generates from the structured
/// fields, and the declarations the approval will cover. Nothing here has touched the disk or the approval store.
/// </summary>
internal sealed record StagedDraft(
    string PluginId,
    string Version,
    string Kind,
    IReadOnlyList<StagedText> Files,
    IReadOnlyList<Permission> Permissions,
    IReadOnlyList<Dependency> Dependencies,
    ExtensionToolSpec? Tool,
    string? SourcePath,
    string? WorkflowFile,
    byte[] ManifestJson);

/// <summary>
/// Checks of an agent-authored draft. The agent never writes <c>manifest.json</c>; the host derives it from these fields,
/// so everything a manifest could declare has to be validated here, with the same rules the manifest reader applies to a
/// hand-written package (unknown permission kind, empty scope and duplicate permissions are errors; a dependency needs a
/// name). Every failure is one <c>invalid_extension</c> error and writes nothing at all.
/// </summary>
internal static class ExtensionStaging
{
    /// <summary>Largest single file an author may stage.</summary>
    public const int MaxFileBytes = 256 * 1024;

    /// <summary>Largest total UTF-8 size of all files of one draft.</summary>
    public const int MaxDraftBytes = 1024 * 1024;

    /// <summary>File the author must not supply: the host generates it from the structured request.</summary>
    public const string ManifestFileName = PluginManifestReader.FileName;

    /// <summary>Workflow document a workflow draft must carry.</summary>
    public const string WorkflowFileName = PluginManifestReader.DefaultWorkflowFileName;

    private static readonly string[] AllowedExtensions = [".cs", ".json", ".md"];

    /// <summary>Validates the request and returns the draft to write; throws <c>invalid_extension</c> on the first problem.</summary>
    public static StagedDraft Validate(ExtensionStageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pluginId = request.PluginId?.Trim() ?? string.Empty;
        if (!PluginIds.IsSafeSegment(pluginId))
        {
            throw Invalid(
                $"'{request.PluginId}' is not a usable extension id: use 1-{PluginIds.MaxSegmentLength} characters with no path "
                + "separators, no '..' and no invalid file-name characters.");
        }

        var version = request.Version?.Trim() ?? string.Empty;
        if (!PluginIds.IsSafeSegment(version))
        {
            throw Invalid(
                $"'{request.Version}' is not a usable extension version: use 1-{PluginIds.MaxSegmentLength} characters with no "
                + "path separators, no '..' and no invalid file-name characters.");
        }

        var kind = NormalizeKind(request.Kind);
        var permissions = ValidatePermissions(request.Permissions);
        var dependencies = ValidateDependencies(request.Dependencies);
        var files = ValidateFiles(request.Files, pluginId);

        string? sourcePath = null;
        string? workflowFile = null;
        ExtensionToolSpec? tool = null;
        if (string.Equals(kind, ExtensionKinds.Workflow, StringComparison.Ordinal))
        {
            if (request.Tool is not null)
            {
                tool = ValidateTool(request.Tool);
            }

            if (tool is null)
            {
                throw Invalid("a workflow draft must declare the tool it exposes (name, description and a JSON-object inputSchema).");
            }

            foreach (var file in files)
            {
                if (string.Equals(file.Path, WorkflowFileName, StringComparison.OrdinalIgnoreCase))
                {
                    workflowFile = file.Path;
                    break;
                }
            }

            if (workflowFile is null)
            {
                throw Invalid($"a workflow draft must contain '{WorkflowFileName}'.");
            }
        }
        else
        {
            if (request.Tool is not null)
            {
                throw Invalid("a 'plugin' draft must not declare a workflow tool; declare kind 'workflow' for a declarative extension.");
            }

            var sources = new List<string>(2);
            foreach (var file in files)
            {
                if (string.Equals(Path.GetExtension(file.Path), ".cs", StringComparison.OrdinalIgnoreCase))
                {
                    sources.Add(file.Path);
                }
            }

            if (sources.Count != 1)
            {
                throw Invalid(
                    $"a plugin draft must contain exactly one .cs source file; this draft contains {sources.Count}.");
            }

            sourcePath = sources[0];
        }

        var manifest = BuildManifest(pluginId, version, kind, permissions, dependencies, sourcePath, workflowFile, tool);
        return new StagedDraft(
            pluginId,
            version,
            kind,
            files,
            permissions,
            dependencies,
            tool,
            sourcePath,
            workflowFile,
            manifest);
    }

    /// <summary>Directory the compiler writes a plugin draft's image into; it is not an author file.</summary>
    private const string CompilerOutputDirectory = "bin";

    /// <summary>
    /// Re-asserts the layout of a directory under <c>.pending</c>, which is plain disk anyone could have written to: only
    /// whitelisted author files, a manifest byte-identical to the one the host would generate from the fields it declares
    /// (so no entryAssembly and no inconsistent kind/source), no stray directories, and no compiler output except
    /// <c>bin/</c> of a plugin draft (its bytes are covered by the dependency hash). Returns the first problem, or null.
    /// </summary>
    internal static string? VerifyLayout(string directory, string expectedId)
    {
        var root = Path.GetFullPath(directory);
        try
        {
            if (!PluginManifestReader.TryRead(root, out var manifest, out _, out var error))
            {
                return $"the manifest cannot be read: {error}";
            }

            if (!string.Equals(manifest.Id, expectedId, StringComparison.Ordinal))
            {
                return $"the manifest declares id '{manifest.Id}', not '{expectedId}'.";
            }

            var allowBin = !manifest.IsWorkflow;
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            var directoriesWithFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                {
                    return $"'{relative}' is a link.";
                }

                if (relative.StartsWith(CompilerOutputDirectory + "/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!allowBin)
                    {
                        return $"'{relative}': a workflow draft has no compiler output.";
                    }

                    continue;
                }

                if (string.Equals(relative, ManifestFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                files[relative] = File.ReadAllText(file);
                for (var slash = relative.LastIndexOf('/'); slash > 0; slash = relative.LastIndexOf('/', slash - 1))
                {
                    directoriesWithFiles.Add(relative[..slash]);
                }
            }

            foreach (var subdirectory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, subdirectory).Replace('\\', '/');
                if (relative.Equals(CompilerOutputDirectory, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(CompilerOutputDirectory + "/", StringComparison.OrdinalIgnoreCase))
                {
                    if (!allowBin)
                    {
                        return $"'{relative}': a workflow draft has no compiler output.";
                    }

                    continue;
                }

                if (!directoriesWithFiles.Contains(relative))
                {
                    return $"'{relative}' is a directory that holds no author file.";
                }
            }

            var dependencies = new List<ExtensionDependency>(manifest.Dependencies.Count);
            foreach (var dependency in manifest.Dependencies)
            {
                dependencies.Add(new ExtensionDependency(dependency.Name, dependency.Version));
            }

            StagedDraft rebuilt;
            try
            {
                rebuilt = Validate(new ExtensionStageRequest(
                    manifest.Id,
                    manifest.Version,
                    manifest.Kind,
                    files,
                    [.. manifest.Permissions],
                    dependencies,
                    manifest.Tool));
            }
            catch (HostOperationException exception)
            {
                return exception.Message;
            }

            if (!File.ReadAllBytes(Path.Combine(root, ManifestFileName)).AsSpan().SequenceEqual(rebuilt.ManifestJson))
            {
                return "manifest.json differs from the manifest the host generates for these files; the host owns that file.";
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"the draft could not be read: {exception.Message}";
        }
    }

    private static string NormalizeKind(string? kind)
    {
        var declared = kind?.Trim();
        if (string.IsNullOrEmpty(declared))
        {
            throw Invalid($"the draft declares no kind; expected '{ExtensionKinds.Plugin}' or '{ExtensionKinds.Workflow}'.");
        }

        if (string.Equals(declared, ExtensionKinds.Plugin, StringComparison.OrdinalIgnoreCase))
        {
            return ExtensionKinds.Plugin;
        }

        if (string.Equals(declared, ExtensionKinds.Workflow, StringComparison.OrdinalIgnoreCase))
        {
            return ExtensionKinds.Workflow;
        }

        throw Invalid($"'{declared}' is not an extension kind; expected '{ExtensionKinds.Plugin}' or '{ExtensionKinds.Workflow}'.");
    }

    /// <summary>
    /// Validates every author file: relative, <c>/</c>-separated, one safe segment per component, no absolute path, no
    /// <c>..</c>, no author-written manifest, an extension from the whitelist, and the size bounds. Sizes are measured in
    /// UTF-8 bytes because that is what lands on disk.
    /// </summary>
    private static List<StagedText> ValidateFiles(IReadOnlyDictionary<string, string>? files, string pluginId)
    {
        if (files is null || files.Count == 0)
        {
            throw Invalid($"the draft of '{pluginId}' contains no files.");
        }

        var staged = new List<StagedText>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var (rawPath, text) in files)
        {
            var path = ValidatePath(rawPath);
            if (!seen.Add(path))
            {
                throw Invalid($"the draft of '{pluginId}' contains '{path}' twice (paths are compared case-insensitively).");
            }

            if (text is null)
            {
                throw Invalid($"'{path}' of the draft of '{pluginId}' has no text.");
            }

            var bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > MaxFileBytes)
            {
                throw Invalid($"'{path}' is {bytes} bytes; a single file may be at most {MaxFileBytes} bytes.");
            }

            total += bytes;
            if (total > MaxDraftBytes)
            {
                throw Invalid($"the draft of '{pluginId}' is larger than {MaxDraftBytes} bytes in total.");
            }

            staged.Add(new StagedText(path, text));
        }

        staged.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return staged;
    }

    private static string ValidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw Invalid("the draft contains a file without a path.");
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            throw Invalid($"'{path}' uses '\\'; file paths of a draft are '/'-separated.");
        }

        if (path[0] == '/' || Path.IsPathRooted(path) || (path.Length > 1 && path[1] == ':'))
        {
            throw Invalid($"'{path}' is an absolute path; draft files are relative to the extension directory.");
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw Invalid($"'{path}' contains an empty path segment.");
            }

            if (!PluginIds.IsSafeSegment(segment))
            {
                throw Invalid(
                    $"'{path}' has the unusable path segment '{segment}': no '..', no path separators, no invalid file-name "
                    + "characters and no leading or trailing whitespace.");
            }

            if (string.Equals(segment, ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                throw Invalid($"'{path}': manifest.json is generated by the host from the draft; an author must not provide it.");
            }
        }

        var extension = Path.GetExtension(path);
        if (!IsAllowedExtension(extension))
        {
            throw Invalid($"'{path}' has the extension '{extension}'; allowed are {string.Join(", ", AllowedExtensions)}.");
        }

        return path;
    }

    /// <summary>True for a file name the host accepts inside a draft; the compiled artifacts of a plugin draft use other
    /// extensions, so this also separates author files from what the compiler wrote.</summary>
    internal static bool IsAllowedExtension(string extension)
    {
        foreach (var allowed in AllowedExtensions)
        {
            if (string.Equals(extension, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<Permission> ValidatePermissions(IReadOnlyList<Permission>? declared)
    {
        if (declared is null || declared.Count == 0)
        {
            return [];
        }

        var parsed = new List<Permission>(declared.Count);
        var seen = new HashSet<(PermissionKind Kind, string Scope)>();
        foreach (var permission in declared)
        {
            var entry = permission ??
                throw Invalid("the draft declares a permission without a kind.");
            if (!Enum.IsDefined(entry.Kind))
            {
                throw Invalid(
                    $"the draft declares the unknown permission kind '{entry.Kind}'; expected one of "
                    + $"{string.Join(", ", Enum.GetNames<PermissionKind>())}.");
            }

            var scope = entry.Scope?.Trim();
            if (scope is { Length: 0 })
            {
                throw Invalid($"the draft declares permission '{entry.Kind}' with an empty scope.");
            }

            if (!seen.Add((entry.Kind, scope ?? string.Empty)))
            {
                throw Invalid(
                    $"the draft declares permission '{entry.Kind}'{(scope is null ? string.Empty : " for '" + scope + "'")} twice.");
            }

            parsed.Add(new Permission(entry.Kind, scope));
        }

        return parsed;
    }

    private static IReadOnlyList<Dependency> ValidateDependencies(IReadOnlyList<ExtensionDependency>? declared)
    {
        if (declared is null || declared.Count == 0)
        {
            return [];
        }

        var parsed = new List<Dependency>(declared.Count);
        foreach (var dependency in declared)
        {
            var name = dependency?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                throw Invalid("the draft declares a dependency without a name.");
            }

            // An empty version means "unpinned", exactly as in a hand-written manifest.
            parsed.Add(new Dependency(name, dependency!.Version?.Trim() ?? string.Empty));
        }

        return parsed;
    }

    private static ExtensionToolSpec ValidateTool(ExtensionToolSpec tool)
    {
        var name = tool.Name?.Trim();
        if (string.IsNullOrEmpty(name) || !PluginIds.IsSafeToolName(name))
        {
            throw Invalid(
                $"the tool name '{tool.Name}' is not usable: use 1-{PluginIds.MaxSegmentLength} characters with no path "
                + "separators, no '..' and no invalid file-name characters.");
        }

        if (string.IsNullOrWhiteSpace(tool.InputSchemaJson))
        {
            throw Invalid($"tool '{name}' declares no inputSchema; a JSON-schema object is required.");
        }

        try
        {
            using var document = JsonDocument.Parse(tool.InputSchemaJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Invalid($"tool '{name}' has an inputSchema that is not a JSON object.");
            }
        }
        catch (JsonException exception)
        {
            throw Invalid($"tool '{name}' has an inputSchema that is not valid JSON: {exception.Message}");
        }

        return new ExtensionToolSpec(name, tool.Description ?? string.Empty, tool.InputSchemaJson);
    }

    /// <summary>
    /// Renders the <c>manifest.json</c> the host writes for the draft: the same document a hand-written package uses, so a
    /// staged draft and the live package it becomes are read by exactly one code path.
    /// </summary>
    private static byte[] BuildManifest(
        string pluginId,
        string version,
        string kind,
        IReadOnlyList<Permission> permissions,
        IReadOnlyList<Dependency> dependencies,
        string? sourcePath,
        string? workflowFile,
        ExtensionToolSpec? tool)
    {
        var document = new PluginManifestDocument
        {
            Id = pluginId,
            Version = version,
            Kind = kind,
            Source = sourcePath,
            Dependencies = new List<DependencyDocument>(dependencies.Count),
            Permissions = new List<PermissionDocument>(permissions.Count),
        };

        foreach (var dependency in dependencies)
        {
            document.Dependencies.Add(new DependencyDocument { Name = dependency.Name, Version = dependency.Version });
        }

        foreach (var permission in permissions)
        {
            document.Permissions.Add(new PermissionDocument
            {
                Kind = JsonSerializer.SerializeToElement(permission.Kind.ToString(), HostJson.Options),
                Scope = permission.Scope,
            });
        }

        if (tool is not null)
        {
            using var schema = JsonDocument.Parse(tool.InputSchemaJson);
            document.Workflow = workflowFile;
            document.Tool = new PluginToolDocument
            {
                Name = tool.Name,
                Description = tool.Description,
                InputSchema = JsonSerializer.SerializeToElement(schema.RootElement, HostJson.Options),
            };
        }

        return JsonSerializer.SerializeToUtf8Bytes(document, HostJson.Options);
    }

    private static HostOperationException Invalid(string message) => new("invalid_extension", message);
}

/// <summary>
/// The host's own areas inside the plugins root. <c>.pending/&lt;id&gt;/</c> holds drafts nothing loads,
/// <c>.trash/&lt;id&gt;-&lt;random&gt;/</c> holds a replaced directory until it can be deleted; both are skipped by
/// discovery because their names start with a dot. Only the host writes here.
/// </summary>
internal sealed class ExtensionStagingArea
{
    public const string PendingDirectoryName = ".pending";

    public const string TrashDirectoryName = ".trash";

    /// <summary>Marker of a directory a stage is still writing; it is inside <c>.pending</c> and never complete.</summary>
    private const string TemporaryInfix = ".tmp-";

    public ExtensionStagingArea(string pluginsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginsDirectory);
        PluginsDirectory = Path.GetFullPath(pluginsDirectory);
    }

    public string PluginsDirectory { get; }

    public string PendingRoot => Path.Combine(PluginsDirectory, PendingDirectoryName);

    public string TrashRoot => Path.Combine(PluginsDirectory, TrashDirectoryName);

    public string PendingDirectory(string pluginId) => Path.Combine(PendingRoot, pluginId);

    public string LiveDirectory(string pluginId) => Path.Combine(PluginsDirectory, pluginId);

    /// <summary>
    /// Writes the draft to <c>.pending/&lt;id&gt;.tmp-&lt;random&gt;/</c> and then replaces <c>.pending/&lt;id&gt;/</c> with
    /// it, so a reader either sees the previous draft or the complete new one, never a half-written mix.
    /// </summary>
    public void Write(StagedDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Directory.CreateDirectory(PendingRoot);
        var temporary = Path.Combine(PendingRoot, draft.PluginId + TemporaryInfix + RandomNumberGenerator.GetHexString(8));
        try
        {
            Directory.CreateDirectory(temporary);
            foreach (var file in draft.Files)
            {
                var target = Path.Combine(temporary, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.WriteAllBytes(target, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(file.Text));
            }

            File.WriteAllBytes(Path.Combine(temporary, ExtensionStaging.ManifestFileName), draft.ManifestJson);
            ReplaceDirectory(temporary, PendingDirectory(draft.PluginId));
        }
        catch
        {
            TryDeleteDirectory(temporary);
            throw;
        }
    }

    /// <summary>Removes a draft; a missing draft is not an error.</summary>
    public void Discard(string pluginId) => TryDeleteDirectory(PendingDirectory(pluginId));

    /// <summary>
    /// The directories under <c>.pending</c>. A leftover of an interrupted stage is filtered by the caller: only a
    /// directory whose manifest declares its own name is a draft, and a temporary directory is named after the id plus a
    /// <c>.tmp-</c> marker, so its manifest never declares the directory's own name.
    /// </summary>
    public IReadOnlyList<string> PendingDirectories()
    {
        if (!Directory.Exists(PendingRoot))
        {
            return [];
        }

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(PendingRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"forge-extension-host: the staging area '{PendingRoot}' could not be read: {exception.Message}");
            return [];
        }

        Array.Sort(directories, StringComparer.Ordinal);
        var pending = new List<string>(directories.Length);
        foreach (var directory in directories)
        {
            if (!PluginIds.IsSafeSegment(Path.GetFileName(directory)))
            {
                // Only an id can name a draft; anything else in .pending is host scratch that was never published.
                continue;
            }

            pending.Add(directory);
        }

        return pending;
    }

    /// <summary>Moves the live directory aside so the staged one can take its place, and returns the trash path (or null
    /// when there was no live directory).</summary>
    public string? MoveLiveAside(string pluginId)
    {
        var live = LiveDirectory(pluginId);
        if (!Directory.Exists(live))
        {
            return null;
        }

        Directory.CreateDirectory(TrashRoot);
        var trash = Path.Combine(TrashRoot, pluginId + "-" + RandomNumberGenerator.GetHexString(8));
        Directory.Move(live, trash);
        return trash;
    }

    /// <summary>Deletes a replaced directory. A failure is reported to stderr and never fails the commit that already
    /// succeeded: the directory is already outside the discovery root.</summary>
    public static void DeleteTrash(string? trash)
    {
        if (trash is null)
        {
            return;
        }

        TryDeleteDirectory(trash);
    }

    private void ReplaceDirectory(string temporary, string target)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(temporary, target);
            return;
        }

        var displaced = Path.Combine(PendingRoot, Path.GetFileName(target) + TemporaryInfix + RandomNumberGenerator.GetHexString(8));
        Directory.Move(target, displaced);
        try
        {
            Directory.Move(temporary, target);
        }
        catch
        {
            Directory.Move(displaced, target);
            throw;
        }

        TryDeleteDirectory(displaced);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"forge-extension-host: '{path}' could not be deleted: {exception.Message}");
        }
    }
}
