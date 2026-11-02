namespace Forge.Mcp.Tests.Fixtures;

internal static class Clients
{
    public static McpClientOptions DefaultOptions(TimeSpan? timeout = null) => new()
    {
        RequestTimeout = timeout ?? TimeSpan.FromSeconds(5),
    };

    public static async Task<(McpClient Client, LoopbackTransport Transport)> ConnectLoopbackAsync(
        ScriptedServer? server = null,
        McpClientOptions? options = null)
    {
        var transport = new LoopbackTransport();
        transport.Responder = (server ?? new ScriptedServer()).Respond;
        var client = new McpClient(transport, options ?? DefaultOptions());
        await client.ConnectAsync(CancellationToken.None);
        return (client, transport);
    }
}
