using System.Text.Json;

namespace Forge.Integration.Tests.Terminal;

/// <summary>Raw wire shapes of the <c>terminal.*</c> family, mirroring the host's
/// <c>Forge.ExtensionHost/TerminalProtocol.cs</c> without referencing its internals.
/// Property names are written exactly as they travel (camelCase).</summary>
internal static class TerminalWire
{
    public const string Start = "terminal.start";
    public const string Input = "terminal.input";
    public const string Resize = "terminal.resize";
    public const string Foreground = "terminal.foreground";
    public const string Stop = "terminal.stop";
    public const string Ack = "terminal.ack";
    public const string Output = "terminal.output";
    public const string Exit = "terminal.exit";

    public static JsonElement StartArgs(
        string workingDirectory,
        int columns,
        int rows,
        string? shell = null,
        string[]? arguments = null,
        Dictionary<string, string>? environment = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["sessionId"] = "terminal-test-" + Guid.NewGuid().ToString("N")[..8],
            ["workingDirectory"] = workingDirectory,
            ["columns"] = columns,
            ["rows"] = rows,
        };
        if (shell is not null)
        {
            args["shell"] = shell;
        }

        if (arguments is not null)
        {
            args["arguments"] = arguments;
        }

        if (environment is not null)
        {
            args["environment"] = environment;
        }

        return JsonSerializer.SerializeToElement(args);
    }

    public static JsonElement InputArgs(string terminalId, byte[] data) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["terminalId"] = terminalId,
            ["data"] = Convert.ToBase64String(data),
        });

    public static JsonElement ResizeArgs(string terminalId, int columns, int rows) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["terminalId"] = terminalId,
            ["columns"] = columns,
            ["rows"] = rows,
        });

    public static JsonElement IdArgs(string terminalId) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["terminalId"] = terminalId,
        });

    public static JsonElement AckArgs(string terminalId, long bytes) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["terminalId"] = terminalId,
            ["bytes"] = bytes,
        });

    public static (string TerminalId, int ProcessId, string Shell) ReadStartResult(JsonElement? result)
    {
        var root = result ?? throw new InvalidOperationException("terminal.start answered without a result.");
        return (
            root.GetProperty("terminalId").GetString() ?? throw new InvalidOperationException("terminal.start result has no terminalId."),
            root.GetProperty("processId").GetInt32(),
            root.GetProperty("shell").GetString() ?? string.Empty);
    }

    public static bool ReadBusy(JsonElement? result)
    {
        var root = result ?? throw new InvalidOperationException("terminal.foreground answered without a result.");
        return root.GetProperty("busy").GetBoolean();
    }

    public static (string TerminalId, long Seq, byte[] Data) ReadOutput(JsonElement? payload)
    {
        var root = payload ?? throw new InvalidOperationException("terminal.output arrived without arguments.");
        var terminalId = root.GetProperty("terminalId").GetString() ?? string.Empty;
        var seq = root.GetProperty("seq").GetInt64();
        var base64 = root.GetProperty("data").GetString() ?? string.Empty;
        return (terminalId, seq, Convert.FromBase64String(base64));
    }

    public static (string TerminalId, int ExitCode) ReadExit(JsonElement? payload)
    {
        var root = payload ?? throw new InvalidOperationException("terminal.exit arrived without arguments.");
        return (
            root.GetProperty("terminalId").GetString() ?? string.Empty,
            root.GetProperty("exitCode").GetInt32());
    }
}
