using Forge.Ipc;

namespace Forge.ExtensionHost;

/// <summary>
/// The typed answer to one main-program request. The dispatcher routes every operation to a handler with its own result
/// type; this wrapper carries that result to the single place that writes the response, so the reply is serialized with
/// its own MessagePack formatter and never through an untyped intermediate.
/// </summary>
internal abstract class HostReply
{
    public abstract Task SendAsync(IIpcPeer peer, long requestId, CancellationToken ct);

    public static HostReply Of<T>(T value) => new Typed<T>(value);

    public static async Task<HostReply> From<T>(Task<T> pending) => Of(await pending.ConfigureAwait(false));

    private sealed class Typed<T>(T value) : HostReply
    {
        public override Task SendAsync(IIpcPeer peer, long requestId, CancellationToken ct) =>
            peer.RespondAsync(requestId, value, ct);
    }
}
