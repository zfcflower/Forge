using Forge.Mcp;

namespace Forge.Mcp.Tests.Fixtures;

/// <summary>
/// A real child process speaking newline-delimited JSON-RPC on stdio, implemented as a PowerShell script
/// (Windows PowerShell 5.1 ships with Windows, so no extra build artifact is needed).
/// Tools: echo (text argument), env (FAKE_MCP_VALUE plus working directory), hang (never answers), exit (exit code 3).
/// Every received method is reported on stderr as "received:&lt;method&gt;".
/// </summary>
internal static class FakeStdioServer
{
    public const string ExitCode = "3";

    private static readonly Lazy<string> Script = new(WriteScript);

    public static string ScriptPath => Script.Value;

    public static McpStdioOptions Options(
        IReadOnlyDictionary<string, string>? env = null,
        string? workingDirectory = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal) { ["FAKE_MCP_VALUE"] = "unset" };
        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                merged[key] = value;
            }
        }

        return new McpStdioOptions
        {
            Command = "powershell.exe",
            Args = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath],
            Env = merged,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
            KillTimeout = TimeSpan.FromSeconds(3),
        };
    }

    private static string WriteScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fake-mcp-stdio-server.ps1");
        File.WriteAllText(path, Body, System.Text.Encoding.ASCII);
        return path;
    }

    private const string Body = """
        $ErrorActionPreference = 'Stop'
        [Console]::Error.WriteLine('fake-ready')
        [Console]::Out.WriteLine('banner line')
        while ($true) {
            $line = [Console]::In.ReadLine()
            if ($null -eq $line) { break }
            if ($line.Trim().Length -eq 0) { continue }
            $msg = $line | ConvertFrom-Json
            $method = [string]$msg.method
            if ($method.Length -gt 0) { [Console]::Error.WriteLine('received:' + $method) }
            if ($null -eq $msg.id) { continue }
            $id = [string]$msg.id
            $result = $null
            switch ($method) {
                'initialize' { $result = '{"protocolVersion":"2025-06-18","capabilities":{"tools":{"listChanged":true}},"serverInfo":{"name":"fake-stdio","version":"0.1"}}' }
                'tools/list' { $result = '{"tools":[{"name":"echo","inputSchema":{"type":"object"}},{"name":"env"},{"name":"hang"},{"name":"exit"}]}' }
                'ping' { $result = '{}' }
                'tools/call' {
                    $name = [string]$msg.params.name
                    if ($name -eq 'echo') {
                        $text = [string]$msg.params.arguments.text
                        $result = '{"content":[{"type":"text","text":"echo:' + $text + '"}]}'
                    }
                    elseif ($name -eq 'env') {
                        $cwd = (Get-Location).ProviderPath.Replace('\', '\\')
                        $result = '{"content":[{"type":"text","text":"' + $env:FAKE_MCP_VALUE + '|' + $cwd + '"}]}'
                    }
                    elseif ($name -eq 'exit') {
                        exit 3
                    }
                }
            }
            if ($null -ne $result) {
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":' + $id + ',"result":' + $result + '}')
            }
        }
        exit 0
        """;
}
