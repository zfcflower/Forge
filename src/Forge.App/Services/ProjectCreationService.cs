using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.Workspace;

namespace Forge.App.Services;

/// <summary>How a project is put on disk before it is registered: an empty repository, or a clone of a remote one.</summary>
internal enum ProjectCreationMode
{
    /// <summary>A new, empty directory with <c>git init</c> run in it.</summary>
    Create = 0,

    /// <summary>A <c>git clone</c> of an existing remote.</summary>
    Clone = 1,
}

/// <summary>
/// Why a typed project folder name cannot be used, in the order the dialog checks them. The dialog maps each value to one
/// localized hint; the service never formats text, so the rule stays testable without a language.
/// </summary>
internal enum ProjectNameProblem
{
    None = 0,

    /// <summary>Nothing typed, or only whitespace.</summary>
    Empty,

    /// <summary>A character the file system or git refuses (<c>/ \ : * ? " &lt; &gt; |</c> and control characters).</summary>
    InvalidCharacters,

    /// <summary>A Windows device name (<c>CON</c>, <c>NUL</c>, <c>COM1</c>, …), which cannot be a directory anywhere on Windows.</summary>
    ReservedName,

    /// <summary>Ends with a space or a dot: Windows silently trims it, so the folder would not have the typed name.</summary>
    TrailingDotOrSpace,

    /// <summary>A relative step (<c>.</c>, <c>..</c>) or a rooted path: the name is one path segment, nothing else.</summary>
    NotASegment,

    /// <summary>Longer than <see cref="ProjectCreationService.MaxNameLength"/>.</summary>
    TooLong,

    /// <summary>The target directory already exists and is not empty.</summary>
    Exists,
}

/// <summary>What the user asked for: one name inside one parent directory, plus the remote for a clone.</summary>
/// <param name="Mode">Create an empty repository or clone one.</param>
/// <param name="ParentDirectory">The directory the project folder is created in; must exist or be creatable.</param>
/// <param name="Name">The folder name (one path segment), already trimmed by the dialog.</param>
/// <param name="Url">The remote for <see cref="ProjectCreationMode.Clone"/>; ignored otherwise.</param>
internal sealed record ProjectCreationRequest(
    ProjectCreationMode Mode,
    string ParentDirectory,
    string Name,
    string? Url = null);

/// <summary>
/// The outcome of one creation: the folder to register, or the reason nothing was created. A cancelled creation throws
/// <see cref="OperationCanceledException"/> instead; a refused one (bad name, occupied directory) reports here.
/// </summary>
internal sealed record ProjectCreationResult(string? Directory, LocText? Error)
{
    public static ProjectCreationResult Created(string directory) => new(directory, null);

    public static ProjectCreationResult Failed(LocText error) => new(null, error);
}

/// <summary>
/// Creates a project directory: validates the name, resolves the destination, then either runs <c>git init</c> in a new
/// folder or clones a remote into it, cleaning up after itself when that fails. It holds no UI state and no registry: the
/// shell registers the returned directory, so the same code serves the dialog, a headless host and a test.
/// </summary>
internal sealed class ProjectCreationService
{
    /// <summary>Longest folder name accepted; above this the path, not the name, is what Windows refuses.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The suffix a clone URL's folder name may carry and the suggested name must not.</summary>
    private const string Suffix = ".git";

    /// <summary>Directory names Windows reserves for devices, in any extension (<c>con.txt</c> is still the console).</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private readonly GitCli git;
    private readonly AppPreferences preferences;
    private readonly string fallbackParentDirectory;

    /// <param name="git">The git reader every creation goes through.</param>
    /// <param name="preferences">Read for the clone timeout and the default parent directory, live on every use.</param>
    /// <param name="fallbackParentDirectory">
    /// Where a project goes when <see cref="AppPreferences.ProjectsCloneDirectory"/> is empty: the parent of the general
    /// workspace directory, the one place a fresh install already owns.
    /// </param>
    public ProjectCreationService(GitCli git, AppPreferences preferences, string fallbackParentDirectory)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackParentDirectory);
        this.git = git;
        this.preferences = preferences;
        this.fallbackParentDirectory = fallbackParentDirectory;
    }

    /// <summary>
    /// Where the dialog starts: the configured default directory, or <c>fallbackParentDirectory</c> when that is empty. The
    /// preference is read here, so a value saved in the settings page applies to the next dialog without a restart.
    /// </summary>
    public string DefaultParentDirectory
    {
        get
        {
            var configured = preferences.ProjectsCloneDirectory;
            return string.IsNullOrWhiteSpace(configured) ? fallbackParentDirectory : configured;
        }
    }

    /// <summary>The configured clone timeout; <c>null</c> when the setting says "no timeout" (<c>0</c>).</summary>
    public TimeSpan? CloneTimeout =>
        preferences.ProjectsCloneTimeoutSeconds > 0
            ? TimeSpan.FromSeconds(preferences.ProjectsCloneTimeoutSeconds)
            : null;

    /// <summary>
    /// The folder name a clone suggests: the last path segment of the URL without a trailing slash, a query or the
    /// <c>.git</c> suffix (<c>https://host/team/repo.git</c> → <c>repo</c>). Empty when the URL has no segment to take, so
    /// the dialog starts with an empty name instead of a wrong one.
    /// </summary>
    public static string SuggestNameFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var text = url.Trim();
        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        text = text.TrimEnd('/');
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            // An absolute URL: only the path after the authority carries segments, so a bare host names nothing.
            var pathStart = text.IndexOf('/', schemeEnd + 3);
            if (pathStart < 0)
            {
                return string.Empty;
            }

            text = text[pathStart..];
        }
        else if (text.Contains(':'))
        {
            // scp-like syntax (git@host:team/repo.git): the colon is the host/path separator, so it counts like a slash.
            text = text.Replace(':', '/');
        }

        var lastSeparator = text.LastIndexOf('/');
        var segment = lastSeparator >= 0 ? text[(lastSeparator + 1)..] : text;
        return segment.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
            ? segment[..^Suffix.Length]
            : segment;
    }

    /// <summary>Whether the clone's URL is usable at all: an absolute URL or a scp-like <c>host:path</c>, not an option.</summary>
    public static bool IsUsableUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var text = url.Trim();
        if (text.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || ContainsControlCharacter(text))
        {
            return false;
        }

        if (text[0] == '-')
        {
            // A leading dash makes git read the URL as an option; no creation of ours may inject one.
            return false;
        }

        // A bare word is a name, not an address: an absolute URL carries "//", an scp-like one a colon.
        return text.Contains('/') || text.Contains(':');
    }

    /// <summary>
    /// Checks one typed name against the rules in <see cref="ProjectNameProblem"/>. <paramref name="parentDirectory"/> may be
    /// empty while the user has not chosen one yet; the existence check is skipped then.
    /// <para>This reads the file system (<see cref="ProjectNameProblem.Exists"/>), so a UI caller runs it off the dispatcher;
    /// the rules that need no disk are <see cref="ValidateNameShape"/>, which a dialog can apply on every keystroke.</para>
    /// </summary>
    public static ProjectNameProblem ValidateName(string? parentDirectory, string? name)
    {
        var shape = ValidateNameShape(name);
        if (shape != ProjectNameProblem.None)
        {
            return shape;
        }

        if (string.IsNullOrWhiteSpace(parentDirectory) || !DirectoryIsOccupied(parentDirectory.Trim(), name!.Trim()))
        {
            return ProjectNameProblem.None;
        }

        return ProjectNameProblem.Exists;
    }

    /// <summary>
    /// The rules that need nothing but the string: empty, illegal characters, not one path segment, too long, a trailing
    /// dot or space, a Windows device name. Always <see cref="ProjectNameProblem.None"/> or one of those — never
    /// <see cref="ProjectNameProblem.Exists"/>, which is the only answer that costs a disk read.
    /// </summary>
    public static ProjectNameProblem ValidateNameShape(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ProjectNameProblem.Empty;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            ContainsControlCharacter(name) ||
            name.Contains('/') ||
            name.Contains('\\'))
        {
            return ProjectNameProblem.InvalidCharacters;
        }

        if (name is "." or ".." || Path.IsPathRooted(name))
        {
            return ProjectNameProblem.NotASegment;
        }

        if (name.Length > MaxNameLength)
        {
            return ProjectNameProblem.TooLong;
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            return ProjectNameProblem.TrailingDotOrSpace;
        }

        return IsReservedName(name) ? ProjectNameProblem.ReservedName : ProjectNameProblem.None;
    }

    /// <summary>The folder the request would create: <c>parent</c> + the name, normalized to a full path.</summary>
    public static string ResolveDirectory(ProjectCreationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Path.GetFullPath(Path.Combine(request.ParentDirectory, request.Name));
    }

    /// <summary>
    /// Creates the project. A clone reports through <paramref name="onProgress"/>; a new repository has nothing to report.
    /// Cancellation and failure leave no half-created folder behind when this call created it.
    /// </summary>
    public async Task<ProjectCreationResult> RunAsync(
        ProjectCreationRequest request,
        Action<GitCloneProgress>? onProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A refused request is an answer, not an exception: the dialog shows the reason instead of unwinding.
        if (string.IsNullOrWhiteSpace(request.ParentDirectory))
        {
            return ProjectCreationResult.Failed(ProjectCreationStrings.ParentDirectoryMissing);
        }

        var problem = ValidateName(request.ParentDirectory, request.Name);
        if (problem != ProjectNameProblem.None)
        {
            return ProjectCreationResult.Failed(ProjectCreationStrings.NameProblem(problem));
        }

        var url = request.Url?.Trim();
        if (request.Mode == ProjectCreationMode.Clone && !IsUsableUrl(url))
        {
            return ProjectCreationResult.Failed(ProjectCreationStrings.UrlNotUsable);
        }

        var parent = request.ParentDirectory.Trim();
        var name = request.Name.Trim();
        string destination;
        try
        {
            destination = Path.GetFullPath(Path.Combine(parent, name));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ProjectCreationResult.Failed(LocText.Literal(exception.Message));
        }

        try
        {
            // The settings page lets a not-yet-existing parent be configured, so the parent is created on demand.
            Directory.CreateDirectory(parent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ProjectCreationResult.Failed(LocText.Literal(exception.Message));
        }

        return request.Mode == ProjectCreationMode.Clone
            ? await RunCloneAsync(url!, destination, onProgress, ct).ConfigureAwait(false)
            : await RunCreateAsync(destination, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- creation steps

    /// <summary>
    /// Clones the remote. A failed clone reports git's stderr tail; a cancelled one rethrows, because the caller has to
    /// tell "stopped" from "refused". Cleanup is the clone service's own: it removes the folder it half-wrote, so this
    /// method never deletes anything.
    /// </summary>
    private async Task<ProjectCreationResult> RunCloneAsync(
        string url,
        string destination,
        Action<GitCloneProgress>? onProgress,
        CancellationToken ct)
    {
        try
        {
            await git.CloneAsync(
                    url,
                    destination,
                    new GitCloneOptions { OnProgress = onProgress, Timeout = CloneTimeout },
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GitException exception)
        {
            var message = string.IsNullOrWhiteSpace(exception.StandardError)
                ? exception.Message
                : ErrorTail(exception.StandardError);
            return ProjectCreationResult.Failed(LocText.Literal(message));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A launch failure or a refused destination is still a failure the dialog can show, not a crash.
            return ProjectCreationResult.Failed(LocText.Literal(ErrorTail(exception.Message)));
        }

        return ProjectCreationResult.Created(destination);
    }

    /// <summary>
    /// Creates the folder and runs <c>git init</c> in it. When git refuses (or the run is cancelled), the folder this call
    /// created is removed again, best effort; a folder that was already there stays, because it is not ours.
    /// </summary>
    private async Task<ProjectCreationResult> RunCreateAsync(string destination, CancellationToken ct)
    {
        var createdHere = !Directory.Exists(destination);
        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ProjectCreationResult.Failed(LocText.Literal(exception.Message));
        }

        try
        {
            var init = await git.RunAsync(destination, ["init"], ct).ConfigureAwait(false);
            if (init.Success)
            {
                return ProjectCreationResult.Created(destination);
            }

            var message = init.StandardError.Length > 0 ? init.StandardError : init.StandardOutput;
            TryDeleteCreated(destination, createdHere);
            return ProjectCreationResult.Failed(LocText.Literal(ErrorTail(message)));
        }
        catch (OperationCanceledException)
        {
            TryDeleteCreated(destination, createdHere);
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            TryDeleteCreated(destination, createdHere);
            return ProjectCreationResult.Failed(LocText.Literal(ErrorTail(exception.Message)));
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Longest error text kept; git's stderr repeats lines, so only its tail says what failed last.</summary>
    private const int MaxErrorCharacters = 500;

    /// <summary>The tail of git's output, trimmed and capped: enough to name the failure, never a page of noise.</summary>
    private static string ErrorTail(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length <= MaxErrorCharacters ? trimmed : trimmed[^MaxErrorCharacters..];
    }

    /// <summary>Whether a device name hides at the start of the folder name, with or without an extension (<c>con.txt</c>).</summary>
    private static bool IsReservedName(string name)
    {
        var stem = name;
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            stem = name[..dot];
        }

        foreach (var reserved in ReservedNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the parent already holds a folder of that name that is not empty; an empty one is usable.</summary>
    private static bool DirectoryIsOccupied(string parentDirectory, string name)
    {
        string target;
        try
        {
            target = Path.Combine(parentDirectory, name);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(target))
            {
                return false;
            }

            // An empty directory passes: git init and git clone both accept it, so it is not an obstacle.
            return Directory.EnumerateFileSystemEntries(target).GetEnumerator().MoveNext();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A folder this process cannot list is not safe to write into either: the user picks another name or place.
            return true;
        }
    }

    /// <summary>Whether the text holds a control character, which neither a path nor a URL may carry.</summary>
    private static bool ContainsControlCharacter(string text)
    {
        foreach (var character in text)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes the folder this run created when its initialization failed; a folder that survived is left alone.</summary>
    private static void TryDeleteCreated(string destination, bool createdHere)
    {
        if (!createdHere)
        {
            return;
        }

        try
        {
            Directory.Delete(destination, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Best effort: a folder that could not be removed stays and fails the next attempt with "not empty".
        }
    }
}
