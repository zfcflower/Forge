using System.Globalization;
using System.Text;
using System.Text.Json;
using Forge.Abstractions;

namespace Forge.IO.Tools;

/// <summary>
/// Runs one command line in the workspace: as a program plus argument vector when that is what the text means, through the
/// platform shell otherwise, and answers with the exit code, the duration, and both output streams under a byte cap.
/// A non-zero exit code is a normal answer — compilers and tests fail on purpose — so only a command that could not start,
/// a work directory outside the workspace, or a bad argument is an error result.
/// </summary>
public sealed class BashTool : ToolBase
{
    private const string Schema = """
        {"type":"object","properties":{"command":{"type":"string","description":"The command line to run."},"timeout_seconds":{"type":"integer","description":"Kill the command and its children after this many seconds (1-600, default 120)."},"workdir":{"type":"string","description":"Directory to run in; defaults to the workspace root and must resolve inside it."}},"required":["command"],"additionalProperties":false}
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

    private readonly IProcessRunner runner;
    private readonly Func<ToolOptions> options;

    /// <summary>A tool with the default resource bounds.</summary>
    public BashTool(IProcessRunner runner)
        : this(runner, static () => ToolOptions.Default)
    {
    }

    /// <summary>A tool that reads its bounds per call, so a preference change applies without a restart.</summary>
    public BashTool(IProcessRunner runner, Func<ToolOptions> options)
        : base(
            "bash",
            "Run a command line in the workspace and return its exit code, duration and output. A simple command line " +
            "runs as a program plus arguments (direct mode); anything with shell syntax (pipes, redirection, globs, " +
            "variables, &&, ;) runs through the platform shell (script mode). Each output stream is capped, with the byte " +
            "count reported; a timeout kills the whole process tree. A non-zero exit code is a normal result, not an error.",
            Schema,
            ToolSideEffect.Execute)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        this.runner = runner;
        this.options = options;
    }

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
        var workingDirectory = ResolveWorkingDirectory(Root(context), ToolJson.OptionalString(arguments, "workdir"));

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
            };
            mode = "direct";
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
            };
            mode = "script (" + shell.DialectName + ")";
        }

        var result = await runner.ExecuteAsync(request, ct).ConfigureAwait(false);
        if (result.EndReason == ProcessEndReason.Exited && result.ExitCode == ProcessRunner.StartFailedExitCode)
        {
            return new ToolResult(result.Stderr.Length > 0 ? result.Stderr : $"bash: '{request.FileName}' did not start.", IsError: true);
        }

        return new ToolResult(Format(command, mode, result, seconds));
    }

    private static string ResolveWorkingDirectory(string root, string? requested)
    {
        var path = string.IsNullOrWhiteSpace(requested) ? root : WorkspacePath.Resolve(root, requested);
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

    private static string Format(string command, string mode, ProcessResult result, int timeoutSeconds)
    {
        var text = new StringBuilder(command.Length + result.Stdout.Length + result.Stderr.Length + 128);
        text.Append("$ ").Append(command).Append('\n');
        text.Append("mode: ").Append(mode).Append('\n');

        var milliseconds = (long)Math.Round(result.Duration.TotalMilliseconds);
        if (result.EndReason == ProcessEndReason.TimedOut)
        {
            text.Append("exit: timed out after ").Append(timeoutSeconds).Append("s  duration: ").Append(milliseconds).Append("ms\n");
        }
        else
        {
            text.Append("exit: ").Append(result.ExitCode).Append("  duration: ").Append(milliseconds).Append("ms\n");
        }

        AppendStream(text, "stdout", result.Stdout, result.StdoutBytes, result.StdoutTruncated);
        AppendStream(text, "stderr", result.Stderr, result.StderrBytes, result.StderrTruncated);
        return text.ToString();
    }

    private static void AppendStream(StringBuilder text, string name, string content, long bytes, bool truncated)
    {
        text.Append("--- ").Append(name).Append(" (").Append(bytes.ToString(CultureInfo.InvariantCulture));
        text.Append(truncated ? " bytes, truncated) ---\n" : " bytes) ---\n");
        if (content.Length == 0)
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
