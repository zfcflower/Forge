using System.Collections.Frozen;
using System.Text.Json;

namespace Forge.Mcp;

/// <summary>One entry of the <c>mcpServers</c> map; either a stdio command or a streamable HTTP endpoint.</summary>
public sealed record McpServerConfig
{
    public required string Name { get; init; }

    public string? Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = [];

    public IReadOnlyDictionary<string, string> Env { get; init; } = FrozenDictionary<string, string>.Empty;

    public string? WorkingDirectory { get; init; }

    public string? Url { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = FrozenDictionary<string, string>.Empty;

    public bool Disabled { get; init; }

    public IReadOnlyList<string> ReadOnlyTools { get; init; } = [];

    public TimeSpan? Timeout { get; init; }

    public bool IsHttp => !string.IsNullOrWhiteSpace(Url);

    public bool IsStdio => !string.IsNullOrWhiteSpace(Command);

    public bool IsReadOnlyTool(string toolName) =>
        ReadOnlyTools.Count != 0 && ReadOnlyTools.Contains(toolName, StringComparer.Ordinal);
}

public sealed record McpConfigDiagnostic(string Source, string? ServerName, string Message);

public sealed record McpConfig(IReadOnlyList<McpServerConfig> Servers, IReadOnlyList<McpConfigDiagnostic> Diagnostics)
{
    public static McpConfig Empty { get; } = new([], []);

    public McpServerConfig? Find(string name)
    {
        foreach (var server in Servers)
        {
            if (string.Equals(server.Name, name, StringComparison.Ordinal))
            {
                return server;
            }
        }

        return null;
    }

    public McpConfig WithServer(McpServerConfig server)
    {
        var servers = new List<McpServerConfig>(Servers.Count + 1);
        var replaced = false;
        foreach (var existing in Servers)
        {
            if (string.Equals(existing.Name, server.Name, StringComparison.Ordinal))
            {
                servers.Add(server);
                replaced = true;
            }
            else
            {
                servers.Add(existing);
            }
        }

        if (!replaced)
        {
            servers.Add(server);
        }

        return new McpConfig(servers, Diagnostics);
    }
}
