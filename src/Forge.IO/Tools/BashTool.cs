using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// Runs one command line in the workspace: as a program plus argument vector when that is what the text means, through the
/// platform shell otherwise, and answers with the exit code, the duration, and both output streams under a byte cap.
/// A non-zero exit code is a normal answer — compilers and tests fail on purpose — so only a command that could not start,
/// a work directory the user did not allow outside the workspace, or a bad argument is an error result.
/// </summary>
public sealed class BashTool : ToolBase, IToolCallScope
{
    private const string Schema = """
        {"type":"object","properties":{"command":{"type":"string","description":"The command line to run."},"timeout_seconds":{"type":"integer","description":"Kill the command and its children after this many seconds (1-600, default 120)."},"workdir":{"type":"string","description":"Directory to run in; defaults to the workspace root. A workdir outside the workspace asks the user for that call."}},"required":["command"],"additionalProperties":false}
        """;

    /// <summary>
    /// The environment a command needs to behave like one a developer ran: no credentials or pagers waiting on a terminal
    /// that is not there, no interactive git prompts, and no ANSI colour to strip out of captured output. <c>LC_ALL</c> and
    /// <c>LANG</c> are deliberately left alone, so a command's own locale rules still apply.
    /// </summary>
    private static readonly Dictionary<string, string?> ChildEnvironment = new(StringComparer.Ordinal)
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_PAGER"] = "cat",
        ["PAGER"] = "cat",
        ["TERM"] = "dumb",
        ["NO_COLOR"] = "1",
    };

    /// <summary>How long a command waits for the workspace before it gives up and says who is holding it.</summary>
    internal static readonly TimeSpan MaxExecutionWait = TimeSpan.FromMinutes(10);

    private readonly IProcessRunner runner;
    private readonly Func<ToolOptions> options;
    private readonly ExecutionGate executionGate;

    /// <summary>A tool with the default resource bounds.</summary>
    public BashTool(IProcessRunner runner)
        : this(runner, static () => ToolOptions.Default)
    {
    }

    /// <summary>A tool that reads its bounds per call, so a preference change applies without a restart.</summary>
    public BashTool(IProcessRunner runner, Func<ToolOptions> options)
        : this(runner, options, ExecutionGate.Shared)
    {
    }

    /// <param name="runner">Starts the processes.</param>
    /// <param name="options">Read per call.</param>
    /// <param name="executionGate">Serializes the commands that are not trivially cheap per workspace; tests pass their own.</param>
    public BashTool(IProcessRunner runner, Func<ToolOptions> options, ExecutionGate executionGate)
        : base(
            "bash",
            "Run a command line in the workspace and return its exit code, duration and output. The text is parsed by PowerShell 7 on " +
            "Windows (cmd.exe when it is missing) and by bash or sh on Unix — not by the shell the tool is named after: for several " +
            "lines or nested quotes, write a script with `write` and run that file. For what the built-in tools do not cover: " +
            "building, testing, running programs, package managers; git subcommands no git_* tool covers, and 'gh pr/run/workflow/api' " +
            "(each only if you hold git_* / pr_* tools); network only to localhost. " +
            "Commands a built-in tool already does are refused and say which tool owns them: listing (ls, dir, Get-ChildItem, gci), " +
            "reading (cat, head, tail, Get-Content, gc, type), searching (grep, rg, findstr, Select-String, find, fd), writing files " +
            "(tee, Set-Content, Out-File), and 'sed -i' (use edit). " +
            "Piping a command's output through grep, head or tail is fine. Commands that are not trivially cheap run one at a time per workspace; " +
            "that wait does not count against timeout_seconds.",
            Schema,
            ToolSideEffect.Execute)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(executionGate);
        this.runner = runner;
        this.options = options;
        this.executionGate = executionGate;
    }

    public ToolCallScope ScopeOf(ToolCall call, string workingDirectory) => CallScope.OfCommand(workingDirectory, call);

    protected override async ValueTask<ToolResult> InvokeCoreAsync(JsonElement arguments, ToolContext context, CancellationToken ct)
    {
        var bounds = options();
        var command = ToolJson.RequiredString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ToolArgumentException("'command' is required.");
        }

        // Clamped rather than rejected: a model asking for 1000 seconds gets the longest allowed run, not an argument error.
        var requestedSeconds = ToolJson.OptionalInt(arguments, "timeout_seconds", bounds.ShellTimeoutSeconds, int.MinValue, int.MaxValue);
        var seconds = Math.Clamp(requestedSeconds, ToolOptions.MinShellTimeoutSeconds, ToolOptions.MaxShellTimeoutSeconds);
        var captureBytes = Math.Clamp(bounds.ShellCaptureBytes, 1, ToolOptions.MaxShellCaptureBytes);
        var workingDirectory = ResolveWorkingDirectory(Root(context), ToolJson.OptionalString(arguments, "workdir"), context.OutOfWorkspaceAllowed);

        var commands = BashCommandAnalyzer.Analyze(command);
        if (BashPolicy.Check(commands, context.Tools, Root(context)) is { } refusal)
        {
            return new ToolResult(refusal, IsError: true);
        }

        ProcessRequest request;
        string mode;
        if (CommandLine.TryParseSimple(command, out var argv))
        {
            request = new ProcessRequest
            {
                FileName = argv[0],
                Arguments = Tail(argv),
                WorkingDirectory = workingDirectory,
                Timeout = TimeSpan.FromSeconds(seconds),
                MaxStdoutBytes = captureBytes,
                MaxStderrBytes = captureBytes,
                Environment = ChildEnvironment,
                OnOutput = context.Progress,
            };
            mode = "direct";

            // MSBuild answers a held output file by retrying the copy ten times before failing (77 s in production).
            // A sharing violation is detectable in a millisecond, so the build is not started at all.
            if (BuildLockProbe.Find(argv, workingDirectory) is { } locked)
            {
                return new ToolResult(
                    $"bash: not started: '{locked}' is held open by another process (a running build output is the usual "
                    + "cause); close it or build a copy.",
                    IsError: true);
            }
        }
        else
        {
            var shell = ShellResolver.Resolve(bounds.ShellPath);
            request = new ProcessRequest
            {
                FileName = shell.FileName,
                Arguments = shell.ArgumentsForScript(command),
                WorkingDirectory = workingDirectory,
                Timeout = TimeSpan.FromSeconds(seconds),
                MaxStdoutBytes = captureBytes,
                MaxStderrBytes = captureBytes,
                Environment = ChildEnvironment,
                OnOutput = context.Progress,
            };
            mode = "script (" + shell.DialectName + ")";
        }

        using var lease = BashPolicy.IsCheap(commands) ? null : await EnterWorkspaceAsync(context, command, ct).ConfigureAwait(false);
        if (lease?.Busy is { } busy)
        {
            return new ToolResult(busy, IsError: true);
        }

        var result = await runner.ExecuteAsync(request, ct).ConfigureAwait(false);
        if (result.EndReason == ProcessEndReason.Exited && result.ExitCode == ProcessRunner.StartFailedExitCode)
        {
            return new ToolResult(result.Stderr.Length > 0 ? result.Stderr : $"bash: '{request.FileName}' did not start.", IsError: true);
        }

        return new ToolResult(Format(command, mode, lease?.QueuedNote, result, seconds));
    }

    /// <summary>
    /// Takes the workspace for this command. A free workspace is taken at once; a busy one is waited for with the caller's
    /// parallelism slot handed back, so agents waiting here do not keep others from starting. The lease carries a busy
    /// message instead when the wait ran out.
    /// </summary>
    private async ValueTask<WorkspaceLease> EnterWorkspaceAsync(ToolContext context, string command, CancellationToken ct)
    {
        var workspace = Root(context);
        var holder = Describe(context, command);
        if (executionGate.TryAcquire(workspace, holder, out var immediate))
        {
            return new WorkspaceLease(immediate, null, null);
        }

        var owner = executionGate.HolderOf(workspace) ?? "another command";
        context.Progress?.Invoke("waiting for the workspace: " + owner);
        var started = Stopwatch.GetTimestamp();
        IDisposable? acquired = null;
        try
        {
            await using (context.YieldSlot?.Invoke(ct))
            {
                acquired = await executionGate.AcquireAsync(workspace, holder, MaxExecutionWait, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            acquired?.Dispose();
            throw;
        }

        var waited = Stopwatch.GetElapsedTime(started);
        if (acquired is null)
        {
            return new WorkspaceLease(
                NoLease.Instance,
                null,
                $"bash: the workspace stayed busy for {(int)waited.TotalSeconds}s ({executionGate.HolderOf(workspace) ?? owner}). Nothing was run; try again later.");
        }

        return new WorkspaceLease(
            acquired,
            "queued: waited " + waited.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + "s for " + owner,
            null);
    }

    private static string Describe(ToolContext context, string command)
    {
        var who = context.AgentId ?? context.AgentName ?? "agent";
        var text = command.Length > 60 ? string.Concat(command.AsSpan(0, 60), "...") : command;
        return who + ": " + text.Replace('\n', ' ');
    }

    private sealed class WorkspaceLease(IDisposable inner, string? queuedNote, string? busy) : IDisposable
    {
        public string? QueuedNote { get; } = queuedNote;

        public string? Busy { get; } = busy;

        public void Dispose() => inner.Dispose();
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }

    private static string ResolveWorkingDirectory(string root, string? requested, bool allowOutside)
    {
        var path = string.IsNullOrWhiteSpace(requested) ? root : WorkspacePath.Resolve(root, requested, rootIsReal: false, allowOutside);
        if (!Directory.Exists(path))
        {
            throw new ToolArgumentException($"workdir '{path}' is not an existing directory.");
        }

        return path;
    }

    private static string[] Tail(IReadOnlyList<string> arguments)
    {
        if (arguments.Count <= 1)
        {
            return [];
        }

        var tail = new string[arguments.Count - 1];
        for (var index = 1; index < arguments.Count; index++)
        {
            tail[index - 1] = arguments[index];
        }

        return tail;
    }

    private static string Format(string command, string mode, string? queuedNote, ProcessResult result, int timeoutSeconds)
    {
        var text = new StringBuilder(command.Length + result.Stdout.Length + result.Stderr.Length + 128);
        text.Append("$ ").Append(command).Append('\n');
        text.Append("mode: ").Append(mode).Append('\n');
        if (queuedNote is not null)
        {
            text.Append(queuedNote).Append('\n');
        }


        var milliseconds = (long)Math.Round(result.Duration.TotalMilliseconds);
        if (result.EndReason == ProcessEndReason.TimedOut)
        {
            text.Append("exit: timed out after ").Append(timeoutSeconds).Append("s  duration: ").Append(milliseconds).Append("ms\n");
        }
        else
        {
            text.Append("exit: ").Append(result.ExitCode);
            if (result.Signal is { } signal)
            {
                // A signal death and an ordinary exit code look alike otherwise (SIGKILL surfaces as 137); naming the
                // signal is what tells the model whether the process was killed or chose that code.
                text.Append(" (").Append(signal).Append(')');
            }

            text.Append("  duration: ").Append(milliseconds).Append("ms\n");
        }

        var (stdout, stdoutFolded) = OutputFold.Squeeze(result.Stdout);
        var (stderr, stderrFolded) = OutputFold.Squeeze(result.Stderr);
        AppendStream(text, "stdout", stdout, result.StdoutBytes, result.StdoutTruncated, stdoutFolded);
        AppendStream(text, "stderr", stderr, result.StderrBytes, result.StderrTruncated, stderrFolded);
        return text.ToString();
    }

    private static void AppendStream(StringBuilder text, string name, string content, long bytes, bool truncated, bool folded)
    {
        text.Append("--- ").Append(name).Append(" (").Append(bytes.ToString(CultureInfo.InvariantCulture));
        text.Append(truncated ? " bytes, truncated" : " bytes");
        text.Append(folded ? ", repeated lines folded) ---\n" : ") ---\n");        if (content.Length == 0)
        {
            return;
        }

        text.Append(content);
        if (content[^1] != '\n')
        {
            text.Append('\n');
        }
    }
}
