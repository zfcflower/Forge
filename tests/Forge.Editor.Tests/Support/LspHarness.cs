using System.Text.Json;
using Forge.Editor.Lsp;

namespace Forge.Editor.Tests.Support;

internal static class LspHarness
{
    public static async Task<LspClient> ConnectAsync(FakeLanguageServer server, string capabilities = "{}", LspClientOptions? options = null)
    {
        var process = new LanguageServerProcess(new LanguageServerLaunch("fake-language-server"));
        var connecting = LspClient.ConnectAsync(process, server.Streams, options);
        using var initialize = await server.ReceiveAsync().ConfigureAwait(false);
        await server.RespondAsync(initialize.RootElement.GetProperty("id"), $"{{\"capabilities\":{capabilities}}}").ConfigureAwait(false);
        using var initialized = await server.ReceiveAsync().ConfigureAwait(false);
        return await connecting.ConfigureAwait(false);
    }

    public static async Task<JsonDocument> ReceiveMethodAsync(FakeLanguageServer server, string method)
    {
        while (true)
        {
            var message = await server.ReceiveAsync().ConfigureAwait(false);
            if (message.RootElement.TryGetProperty("method", out var name) && name.GetString() == method)
            {
                return message;
            }

            message.Dispose();
        }
    }
}
