using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Forge.Workspace;

/// <summary>Whether the <c>gh</c> executable can be used.</summary>
public enum GhAvailability { NotInstalled, NotAuthenticated, Ready }

/// <summary>
/// Result of <see cref="GhCli.GetAvailabilityAsync"/>. <see cref="Failure"/> is <see cref="GitRemoteFailureKind.None"/>
/// except when the login could not be verified because of connectivity (<see cref="GitRemoteFailureKind.Network"/> or
/// <see cref="GitRemoteFailureKind.TimedOut"/>); <see cref="State"/> is then <see cref="GhAvailability.NotAuthenticated"/>,
/// because <see cref="GhAvailability.Ready"/> is only reported for a verified login.
/// </summary>
public sealed record GhAvailabilityResult(
    GhAvailability State,
    string Detail,
    string? Host = null,
    GitRemoteFailureKind Failure = GitRemoteFailureKind.None);

/// <summary>Outcome of one <c>gh</c> invocation. A failing exit code is reported, not thrown.</summary>
public sealed record GhCommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false)
{
    /// <summary>
    /// The executable could not be started at all (not installed, not on PATH, not executable);
    /// <see cref="StandardError"/> then holds the reason.
    /// </summary>
    public bool StartFailed { get; init; }

    /// <summary>Captured stdout hit the capture limit, so it is only a part of what gh wrote.</summary>
    public bool IsTruncated { get; init; }

    public bool Success => !TimedOut && !StartFailed && ExitCode == 0;
}

/// <summary>Per-invocation knobs for <see cref="GhCli.RunAsync"/>.</summary>
public sealed record GhRunOptions
{
    /// <summary>Overrides <see cref="GhCli.NetworkTimeout"/>; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> disables it.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Overrides <see cref="GhCli.MaxCapturedCharacters"/> for this call.</summary>
    public int? MaxCapturedCharacters { get; init; }

    /// <summary>
    /// When the output exceeds the capture limit keep its end instead of its beginning. For CI logs the failure is at
    /// the bottom, so a head-only capture would return the noise and drop the cause.
    /// </summary>
    public bool KeepTail { get; init; }
}

/// <summary>
/// Async wrapper over the GitHub CLI, built like <see cref="GitCli"/>: arguments go through
/// <see cref="ProcessStartInfo.ArgumentList"/> (never a shell), stdout and stderr are read as raw UTF-8 bytes with a
/// capture cap, stdin is closed at once so nothing can wait for input, a timeout or cancellation kills the whole process
/// tree, and the child environment is pinned so output stays English and parseable and no prompt, pager, colour or update
/// notice can appear. The executable defaults to <c>gh</c> from PATH and is injectable, so tests point it at a fake.
/// </summary>
public sealed class GhCli
{
    private const string FallbackExecutable = "gh";
    private const int ErrorCaptureCharacters = 64 * 1024;
    private const int ByteBufferSize = 16 * 1024;
    private const int MaxCaptureCeiling = 256 * 1024 * 1024;
    private const int MaxStatusCharacters = 500;
    private const string DefaultRemoteName = "origin";

    private static readonly TimeSpan PumpGracePeriod = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultNetworkTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan AvailabilityTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Variables never inherited from the parent process. The first ten are the repository-location and discovery
    /// variables <see cref="GitCli"/> removes too (repository discovery must depend on the working directory only);
    /// <c>GH_REPO</c> is gh's own repository override, <c>GH_FORCE_TTY</c> and <c>CLICOLOR_FORCE</c> would bring back
    /// terminal-style output, and <c>GH_DEBUG</c> would dump HTTP traffic into stderr, where the raw text is shown to the
    /// user and the model. <c>GH_HOST</c> and the token variables are kept: they select the account, not the repository.
    /// </summary>
    private static readonly string[] RemovedVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_COMMON_DIR",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_NAMESPACE",
        "GIT_PREFIX",
        "GIT_CEILING_DIRECTORIES",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM",
        "GH_REPO",
        "GH_FORCE_TTY",
        "CLICOLOR_FORCE",
        "GH_DEBUG",
    ];

    private readonly GitCli git;

    public GhCli(string? executablePath = null, GitCli? git = null)
    {
        ExecutablePath = string.IsNullOrWhiteSpace(executablePath) ? FallbackExecutable : executablePath;
        this.git = git ?? new GitCli();
    }

    public string ExecutablePath { get; }

    /// <summary>
    /// Timeout for every call unless <see cref="GhRunOptions.Timeout"/> overrides it. A function, like
    /// <see cref="GitToolSettings.NetworkTimeout"/>, so a change in the settings applies to the next call; zero or
    /// negative means the 2 minute default.
    /// </summary>
    public Func<TimeSpan> NetworkTimeout { get; init; } = static () => DefaultNetworkTimeout;

    public int MaxCapturedCharacters { get; init; } = 8 * 1024 * 1024;

    /// <summary>
    /// Runs gh to completion. Cancellation kills the process tree and throws <see cref="OperationCanceledException"/>; a
    /// timeout kills the tree and returns a result with <see cref="GhCommandResult.TimedOut"/> set; an executable that
    /// cannot be started returns <see cref="GhCommandResult.StartFailed"/>. Output past the cap is cut off with a marker
    /// and <see cref="GhCommandResult.IsTruncated"/> set instead of exhausting memory.
    /// </summary>
    public async Task<GhCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        GhRunOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"The working directory '{workingDirectory}' does not exist.");
        }

        var captureLimit = Math.Clamp(options?.MaxCapturedCharacters ?? MaxCapturedCharacters, 0, MaxCaptureCeiling);
        var keepTail = options?.KeepTail ?? false;
        var timeout = ResolveTimeout(options?.Timeout);

        var process = TryStart(workingDirectory, arguments, out var startError);
        if (process is null)
        {
            return new GhCommandResult(-1, string.Empty, startError) { StartFailed = true };
        }

        using (process)
        {
            using var timeoutSource = CreateTimeoutSource(ct, timeout);
            using var killOnCancel = timeoutSource.Token.Register(static state => TryKill((Process)state!), process);
            try
            {
                var stdoutTask = PumpAsync(process.StandardOutput.BaseStream, captureLimit, keepTail, timeoutSource.Token);
                var stderrTask = PumpAsync(process.StandardError.BaseStream, Math.Min(captureLimit, ErrorCaptureCharacters), false, timeoutSource.Token);

                var timedOut = false;
                try
                {
                    await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    timedOut = !ct.IsCancellationRequested;
                }

                var stdout = await AwaitPumpAsync(stdoutTask).ConfigureAwait(false);
                var stderr = await AwaitPumpAsync(stderrTask).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                var exitCode = timedOut ? -1 : ExitCodeOf(process);
                return new GhCommandResult(exitCode, stdout.Text, stderr.Text, timedOut) { IsTruncated = stdout.Truncated };
            }
            finally
            {
                TryKill(process);
            }
        }
    }

    /// <summary>
    /// Host of the repository's <c>origin</c> remote (e.g. <c>github.com</c>), or null when there is no repository, no
    /// origin, or its address is a local path. Reads the local configuration only; no network.
    /// </summary>
    public async Task<string?> GetOriginHostAsync(string workingDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        try
        {
            var remotes = await git.GetRemotesAsync(workingDirectory, ct).ConfigureAwait(false);
            foreach (var remote in remotes)
            {
                if (string.Equals(remote.Name, DefaultRemoteName, StringComparison.Ordinal))
                {
                    return GhHostParser.FromRemoteUrl(remote.FetchUrl);
                }
            }
        }
        catch (GitException)
        {
        }

        return null;
    }

    /// <summary>
    /// Checks <c>gh auth status</c> for the host of the repository's origin (all hosts when it cannot be determined).
    /// Never throws except for cancellation: a missing executable is <see cref="GhAvailability.NotInstalled"/>.
    /// </summary>
    public async Task<GhAvailabilityResult> GetAvailabilityAsync(string workingDirectory, CancellationToken ct = default)
    {
        var directory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : Path.GetTempPath();

        var host = await GetOriginHostAsync(directory, ct).ConfigureAwait(false);
        var arguments = new List<string> { "auth", "status" };
        if (host is not null)
        {
            arguments.Add("--hostname");
            arguments.Add(host);
        }

        var timeout = ResolveTimeout(null);
        var options = new GhRunOptions
        {
            Timeout = timeout < AvailabilityTimeout ? timeout : AvailabilityTimeout,
            MaxCapturedCharacters = ErrorCaptureCharacters,
        };

        var result = await RunAsync(directory, arguments, options, ct).ConfigureAwait(false);
        if (result.StartFailed)
        {
            return new GhAvailabilityResult(
                GhAvailability.NotInstalled,
                $"The GitHub CLI (gh) could not be started: {result.StandardError} Install it from https://cli.github.com/ and make sure it is on PATH.",
                host);
        }

        if (result.Success)
        {
            var summary = Summarize(result.StandardOutput.Length > 0 ? result.StandardOutput : result.StandardError);
            return new GhAvailabilityResult(
                GhAvailability.Ready,
                summary.Length > 0 ? summary : "The GitHub CLI is logged in.",
                host);
        }

        var kind = GhFailureClassifier.Classify(result);
        var failure = kind is GitRemoteFailureKind.Network or GitRemoteFailureKind.TimedOut ? kind : GitRemoteFailureKind.None;
        var text = Summarize(result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput);
        var detail = result.TimedOut
            ? "Checking the GitHub CLI login timed out."
            : text.Length > 0 ? text : "The GitHub CLI is not logged in. Run 'gh auth login'.";
        return new GhAvailabilityResult(GhAvailability.NotAuthenticated, detail, host, failure);
    }

    private static string Summarize(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= MaxStatusCharacters ? trimmed : trimmed[..MaxStatusCharacters];
    }

    private TimeSpan ResolveTimeout(TimeSpan? requested)
    {
        if (requested is { } explicitTimeout)
        {
            return explicitTimeout;
        }

        var configured = NetworkTimeout();
        return configured > TimeSpan.Zero || configured == Timeout.InfiniteTimeSpan ? configured : DefaultNetworkTimeout;
    }

    private static CancellationTokenSource CreateTimeoutSource(CancellationToken ct, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout >= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(timeout);
        }

        return source;
    }

    private Process? TryStart(string workingDirectory, IReadOnlyList<string> arguments, out string error)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in RemovedVariables)
        {
            startInfo.Environment.Remove(name);
        }

        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";
        startInfo.Environment["LANGUAGE"] = "C";
        startInfo.Environment["TERM"] = "dumb";
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["CLICOLOR"] = "0";
        startInfo.Environment["GH_PROMPT_DISABLED"] = "1";
        startInfo.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["GH_NO_EXTENSION_UPDATE_NOTIFIER"] = "1";
        startInfo.Environment["GH_SPINNER_DISABLED"] = "1";
        startInfo.Environment["GH_PAGER"] = "cat";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                error = $"Could not start '{ExecutablePath}'.";
                return null;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            process.Dispose();
            error = $"Could not start '{ExecutablePath}': {exception.Message}";
            return null;
        }

        // Close stdin at once: gh must see EOF instead of waiting on a console that nobody is typing into.
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        error = string.Empty;
        return process;
    }

    /// <summary>
    /// Reads a stream as raw bytes into one decoded string. The cap is enforced while reading, so memory stays bounded
    /// for a huge output, and the stream is drained to the end either way so the child never blocks on a full pipe.
    /// Head mode keeps the first <paramref name="limit"/> characters, tail mode the last ones.
    /// </summary>
    private static async Task<Captured> PumpAsync(Stream stream, int limit, bool keepTail, CancellationToken ct)
    {
        var capture = new StringBuilder(Math.Min(limit, 64 * 1024));
        var truncated = false;
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = ArrayPool<byte>.Shared.Rent(ByteBufferSize);
        var chars = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(ByteBufferSize));
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = await stream.ReadAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException or NotSupportedException)
                {
                    truncated = true;
                    break;
                }

                if (read == 0)
                {
                    break;
                }

                var count = decoder.GetChars(bytes.AsSpan(0, read), chars, flush: false);
                if (count > 0)
                {
                    Append(capture, chars.AsSpan(0, count), limit, keepTail, ref truncated);
                }
            }

            var tail = decoder.GetChars(ReadOnlySpan<byte>.Empty, chars, flush: true);
            if (tail > 0)
            {
                Append(capture, chars.AsSpan(0, tail), limit, keepTail, ref truncated);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
        }

        if (keepTail && capture.Length > limit)
        {
            DropLeading(capture, capture.Length - limit);
            truncated = true;
        }

        if (truncated && capture.Length > 0)
        {
            if (keepTail)
            {
                capture.Insert(0, $"[… output truncated; showing the last {capture.Length} characters]\n");
            }
            else
            {
                capture.Append("\n… [output truncated]");
            }
        }

        return new Captured(capture.ToString(), truncated);
    }

    private static void Append(StringBuilder capture, ReadOnlySpan<char> span, int limit, bool keepTail, ref bool truncated)
    {
        if (keepTail)
        {
            capture.Append(span);

            // Trim in bulk once the buffer reaches twice the limit, so keeping the tail costs O(1) per character.
            if (capture.Length >= 2L * limit && capture.Length > limit)
            {
                DropLeading(capture, capture.Length - limit);
                truncated = true;
            }

            return;
        }

        var room = limit - capture.Length;
        if (room <= 0)
        {
            truncated = true;
        }
        else if (span.Length <= room)
        {
            capture.Append(span);
        }
        else
        {
            capture.Append(span[..room]);
            truncated = true;
        }
    }

    /// <summary>Removes the first <paramref name="count"/> characters, and a low surrogate this leaves at the front.</summary>
    private static void DropLeading(StringBuilder capture, int count)
    {
        if (count < capture.Length && char.IsLowSurrogate(capture[count]))
        {
            count++;
        }

        capture.Remove(0, Math.Min(count, capture.Length));
    }

    private static async Task<Captured> AwaitPumpAsync(Task<Captured> pump)
    {
        try
        {
            return await pump.WaitAsync(PumpGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _ = pump.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return new Captured(string.Empty, false);
        }
    }

    private static int ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception or ObjectDisposedException)
        {
        }
    }

    private readonly record struct Captured(string Text, bool Truncated);
}
