using Forge.Abstractions;

namespace Forge.HostClient.Tests;

/// <summary>The setup most HostClient tests open with — a <see cref="FakeHost"/>, a connected
/// <see cref="ExtensionHostClient"/> and a <see cref="TestLimit"/> deadline — owned and disposed together.
/// Disposal follows the reverse-declaration order of the three stacked using declarations it replaces:
/// limit first (synchronously), then the client, then the host.</summary>
internal sealed class HostClientHarness : IAsyncDisposable
{
    private HostClientHarness(FakeHost host, ExtensionHostClient client, CancellationTokenSource limit)
    {
        Host = host;
        Client = client;
        Limit = limit;
    }

    public FakeHost Host { get; }

    public ExtensionHostClient Client { get; }

    public CancellationTokenSource Limit { get; }

    /// <summary>Creates the host, starts a client against it and opens the test deadline; the parameters mirror
    /// <see cref="ClientFactory.StartAsync(FakeHost, Func{string, ITool?}, Func{ApprovalRequest, CancellationToken,
    /// ValueTask{ApprovalDecision}}?, ExtensionHostClientOptions?, TimeSpan?)"/>. On a failed client start the host
    /// is disposed, exactly as the original stacked using declarations would have.</summary>
    public static async Task<HostClientHarness> StartAsync(
        Func<string, ITool?>? resolver = null,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>>? approval = null,
        ExtensionHostClientOptions? options = null,
        TimeSpan? startTimeout = null)
    {
        var host = new FakeHost();
        try
        {
            var client = await ClientFactory.StartAsync(host, resolver, approval, options, startTimeout)
                .ConfigureAwait(false);
            return new HostClientHarness(host, client, TestLimit.Create());
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Limit.Dispose();
        try
        {
            await Client.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // The stacked using declarations this replaces would release the host even if the client's
            // disposal faulted; keep that guarantee instead of letting one failure leak the host.
            await Host.DisposeAsync().ConfigureAwait(false);
        }
    }
}
