using System.Diagnostics;
using Forge.Ipc;
using Xunit;

namespace Forge.Integration.Tests.Terminal;

/// <summary>The Windows default shell skips Microsoft Store app execution aliases (<c>%LOCALAPPDATA%\Microsoft\WindowsApps</c>):
/// a packaged shell starts its children outside the private job the host puts the shell in. The host here runs with its own
/// <c>LOCALAPPDATA</c> and <c>PATH</c> pointing at a scratch tree, so a fake alias sits first on the PATH
/// without touching this machine's real environment. The fake shells are copies of <c>cmd.exe</c>, which keeps them runnable.
/// An explicitly requested alias is used as given.</summary>
public sealed class TerminalDefaultShellTests
{
    [Fact]
    public async Task Default_shell_skips_a_store_alias_on_the_path_and_falls_back_to_a_real_shell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // The alias rule exists only for the Windows default shell.
        }

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var scene = await ScratchHost.StartAsync(ct);

        var (terminalId, _, shell) = await scene.StartAsync(shell: null, ct);
        try
        {
            Assert.False(
                string.Equals(shell, scene.AliasShell, StringComparison.OrdinalIgnoreCase),
                $"the default shell is the Store alias '{shell}'.");
            // Whatever the fallback finds (this machine's PowerShell 7, Windows PowerShell or COMSPEC), it is a real file outside the alias directory.
            Assert.True(File.Exists(shell), $"the default shell '{shell}' does not exist.");
            Assert.DoesNotContain("WindowsApps", shell, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await scene.StopAsync(terminalId);
        }
    }

    [Fact]
    public async Task An_explicitly_requested_alias_is_used_exactly_as_given()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        var ct = budget.Token;
        await using var scene = await ScratchHost.StartAsync(ct);

        var (terminalId, _, shell) = await scene.StartAsync(shell: scene.AliasShell, ct);
        try
        {
            Assert.Equal(scene.AliasShell, shell, ignoreCase: true);
        }
        finally
        {
            await scene.StopAsync(terminalId);
        }
    }

    private sealed class ScratchHost : IAsyncDisposable
    {
        private readonly Process process;
        private readonly IpcPeer peer;
        private readonly string scratch;

        private ScratchHost(Process process, IpcPeer peer, string scratch, string aliasShell)
        {
            this.process = process;
            this.peer = peer;
            this.scratch = scratch;
            AliasShell = aliasShell;
        }

        public string AliasShell { get; }

        public static async Task<ScratchHost> StartAsync(CancellationToken ct)
        {
            var scratch = Path.Combine(Path.GetTempPath(), "forge-shell-" + Guid.NewGuid().ToString("N")[..10]);
            var aliasDirectory = Path.Combine(scratch, "local", "Microsoft", "WindowsApps");
            Directory.CreateDirectory(aliasDirectory);
            Directory.CreateDirectory(Path.Combine(scratch, "plugins"));
            Directory.CreateDirectory(Path.Combine(scratch, "data"));
            var cmd = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            var aliasShell = Path.Combine(aliasDirectory, "pwsh.exe");
            File.Copy(cmd, aliasShell);

            var pipe = "forge-shell-" + Guid.NewGuid().ToString("N")[..20];
            var info = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add(E2eFixture.HostAssemblyPath);
            info.ArgumentList.Add("--pipe");
            info.ArgumentList.Add(pipe);
            info.ArgumentList.Add("--plugins");
            info.ArgumentList.Add(Path.Combine(scratch, "plugins"));
            info.ArgumentList.Add("--data");
            info.ArgumentList.Add(Path.Combine(scratch, "data"));
            info.Environment["LOCALAPPDATA"] = Path.Combine(scratch, "local");
            // Only the fake alias directory: the product must not find a real pwsh.exe through the PATH.
            info.Environment["PATH"] = aliasDirectory;

            var process = Process.Start(info) ?? throw new InvalidOperationException("The Extension Host did not start.");
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try
            {
                var peer = await ConnectAsync(process, ct).ConfigureAwait(false);
                return new ScratchHost(process, peer, scratch, aliasShell);
            }
            catch
            {
                Kill(process);
                TryDelete(scratch);
                throw;
            }
        }

        public async Task<(string TerminalId, int ProcessId, string Shell)> StartAsync(string? shell, CancellationToken ct)
        {
            var response = await peer.RequestAsync(
                TerminalWire.Start,
                TerminalWire.StartArgs(scratch, 80, 24, shell),
                ct).ConfigureAwait(false);
            return TerminalWire.ReadStartResult(response);
        }

        public async Task StopAsync(string terminalId)
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await peer.RequestAsync(TerminalWire.Stop, TerminalWire.IdArgs(terminalId), limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IpcRemoteException or OperationCanceledException or IpcConnectionClosedException)
            {
                // The host teardown in DisposeAsync ends whatever is left.
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await peer.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            await peer.DisposeAsync().ConfigureAwait(false);
            Kill(process);
            TryDelete(scratch);
        }

        private static async Task<IpcPeer> ConnectAsync(Process host, CancellationToken ct)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            var pipe = host.StartInfo.ArgumentList[2];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (host.HasExited)
                {
                    throw new InvalidOperationException($"The Extension Host exited with code {host.ExitCode} before accepting a connection.");
                }

                var transport = new NamedPipeClientTransport(pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(2) });
                try
                {
                    await transport.StartAsync(deadline.Token).ConfigureAwait(false);
                    var peer = new IpcPeer(transport, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(60) });
                    await peer.StartAsync(deadline.Token).ConfigureAwait(false);
                    return peer;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or OperationCanceledException)
                {
                    await transport.DisposeAsync().ConfigureAwait(false);
                    if (deadline.Token.IsCancellationRequested)
                    {
                        throw new TimeoutException("The Extension Host did not accept a pipe connection within 60 s.");
                    }

                    // A host that is still starting has not created the pipe yet: the next attempt is bounded by the 2 s connect timeout.
                }
            }
        }

        private static void Kill(Process host)
        {
            try
            {
                if (!host.HasExited)
                {
                    host.Kill(entireProcessTree: true);
                    host.WaitForExit(10_000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a shell that is still closing may hold its copy for a moment.
            }
        }
    }
}
