using System.Text;
using System.Threading.Channels;
using Forge.Abstractions;
using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>The app-side client against a real host: starting, reading, resizing and probing through
/// <c>ExtensionHostClient</c> pins its <c>terminal.*</c> copy against the host's copy end to end, and a
/// dropped main-pipe connection ends the terminal from the app's point of view.</summary>
public sealed class TerminalClientTests
{
    [Fact]
    public async Task Client_starts_reads_resizes_and_probes_a_real_terminal()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var request = new TerminalStartRequest(
            "terminal-client",
            fx.Root,
            new TerminalSize(80, 24));
        await using var session = await fx.Client.StartAsync(request, fx.Ct);
        Assert.False(string.IsNullOrEmpty(session.Id), "the client returned a session without an id.");

        await session.WriteAsync(Encoding.UTF8.GetBytes("echo forge-client-probe\r"), fx.Ct);
        var text = await ReadUntilAsync(session, "forge-client-probe", TimeSpan.FromSeconds(30), fx.Ct);
        Assert.Contains("forge-client-probe", text, StringComparison.Ordinal);

        await session.ResizeAsync(new TerminalSize(40, 12), fx.Ct);
        Assert.False(
            await session.HasForegroundProcessAsync(fx.Ct),
            "an idle shell must report no foreground process.");
    }

    [Fact]
    public async Task Client_connection_close_ends_the_terminal()
    {
        await using var fx = new E2eFixture();
        await fx.StartAsync();

        var request = new TerminalStartRequest(
            "terminal-client",
            fx.Root,
            new TerminalSize(80, 24));
        var session = await fx.Client.StartAsync(request, fx.Ct);
        try
        {
            await session.WriteAsync(Encoding.UTF8.GetBytes("echo forge-disconnect\r"), fx.Ct);
            await ReadUntilAsync(session, "forge-disconnect", TimeSpan.FromSeconds(30), fx.Ct);

            await fx.Client.StopAsync(fx.Ct);

            // From the app's side the connection ending is the terminal ending: buffered output stays
            // readable, then the reader faults and the exit task fails with the same close error.
            var readerError = await DrainUntilErrorAsync(session, TimeSpan.FromSeconds(15));
            var closed = Assert.IsType<ChannelClosedException>(readerError);
            Assert.IsType<IpcConnectionClosedException>(closed.InnerException);

            await Assert.ThrowsAsync<IpcConnectionClosedException>(() =>
                session.Exited.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(0, fx.Client.HostExitCode);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private static async Task<string> ReadUntilAsync(
        Forge.Abstractions.ITerminalSession session,
        string needle,
        TimeSpan limit,
        CancellationToken ct)
    {
        var builder = new StringBuilder();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limit);
        while (true)
        {
            ReadOnlyMemory<byte> chunk;
            try
            {
                chunk = await session.Output.ReadAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for the terminal to show '{needle}'. Saw: '{builder}'.");
            }

            builder.Append(Encoding.UTF8.GetString(chunk.Span));
            if (builder.ToString().Contains(needle, StringComparison.Ordinal))
            {
                return builder.ToString();
            }
        }
    }

    private static async Task<Exception> DrainUntilErrorAsync(
        Forge.Abstractions.ITerminalSession session,
        TimeSpan limit)
    {
        using var deadline = new CancellationTokenSource(limit);
        try
        {
            while (true)
            {
                await session.Output.ReadAsync(deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("The terminal output neither faulted nor closed within the limit after the connection ended.");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
