namespace Forge.Mcp.Tests.ChildProcess;

/// <summary>
/// The same newline-delimited JSON-RPC child used by <c>StdioTransportTests</c>, implemented as a PowerShell script on
/// Windows (Windows PowerShell 5.1 ships with the OS) and a Python 3 script on Linux/macOS, where no PowerShell is
/// guaranteed: <c>python3</c> is part of both GitHub runner images and of the Xcode command line tools macOS requires.
/// Both implementations expose the same tools (echo, env, hang, exit), report every received method on stderr as
/// <c>received:&lt;method&gt;</c> and exit with <see cref="ExitCode"/> on "exit".
/// </summary>
internal static class PlatformStdioServer
{
    public const string ExitCode = "3";

    private static readonly Lazy<string> PosixScript = new(WritePosixScript);

    public static string ScriptPath => OperatingSystem.IsWindows()
        ? Forge.Mcp.Tests.Fixtures.FakeStdioServer.ScriptPath
        : PosixScript.Value;

    public static string Command => OperatingSystem.IsWindows() ? "powershell.exe" : "python3";

    public static IReadOnlyList<string> Args => OperatingSystem.IsWindows()
        ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath]
        : [ScriptPath, ExitCode];

    public static McpStdioOptions Options(
        IReadOnlyDictionary<string, string>? env = null,
        string? workingDirectory = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return Forge.Mcp.Tests.Fixtures.FakeStdioServer.Options(env, workingDirectory);
        }

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
            Command = Command,
            Args = Args,
            Env = merged,
            WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
            KillTimeout = TimeSpan.FromSeconds(3),
        };
    }

    private static string WritePosixScript()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-mcp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fake-mcp-stdio-server.py");
        File.WriteAllText(path, Body.ReplaceLineEndings("\n"), System.Text.Encoding.ASCII);
        return path;
    }

    private const string Body = """
import json, os, sys

exit_code = int(sys.argv[1]) if len(sys.argv) > 1 else 0

print('fake-ready', file=sys.stderr, flush=True)
print('banner line', flush=True)

def send(obj):
    sys.stdout.write(json.dumps(obj, separators=(',', ':')) + '\n')
    sys.stdout.flush()

for line in sys.stdin:
    line = line.strip()
    if not line:
        continue
    try:
        message = json.loads(line)
    except Exception:
        continue
    method = message.get('method')
    if method:
        print('received:' + str(method), file=sys.stderr, flush=True)
    if 'id' not in message:
        continue
    request_id = message['id']
    result = None
    if method == 'initialize':
        result = {
            'protocolVersion': '2025-06-18',
            'capabilities': {'tools': {'listChanged': True}},
            'serverInfo': {'name': 'fake-stdio', 'version': '0.1'},
        }
    elif method == 'tools/list':
        result = {
            'tools': [
                {'name': 'echo', 'inputSchema': {'type': 'object'}},
                {'name': 'env'},
                {'name': 'hang'},
                {'name': 'exit'},
            ]
        }
    elif method == 'ping':
        result = {}
    elif method == 'tools/call':
        params = message.get('params') or {}
        name = params.get('name')
        arguments = params.get('arguments') or {}
        if name == 'echo':
            result = {'content': [{'type': 'text', 'text': 'echo:' + str(arguments.get('text', ''))}]}
        elif name == 'env':
            result = {'content': [{'type': 'text', 'text': os.environ.get('FAKE_MCP_VALUE', '') + '|' + os.getcwd()}]}
        elif name == 'exit':
            sys.exit(exit_code)
    if result is not None:
        send({'jsonrpc': '2.0', 'id': request_id, 'result': result})

sys.exit(0)
""";
}
