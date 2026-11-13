using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Core.Providers;

/// <summary>
/// Asks the locally installed Claude Code CLI what it would call itself: its version, and the build suffix it puts in
/// <c>cc_version</c>.
/// </summary>
/// <remarks>
/// <para>
/// The version is cheap and exact — <c>claude --version</c> answers in about a fifth of a second with <c>2.1.317 (Claude
/// Code)</c>. The build suffix is not in the program at all: measured on this machine, the same binary and the same credential
/// produced <c>2.1.317.68a</c> through one destination and <c>2.1.317.d83</c> through another, so it is neither a build id nor a
/// hash of the file, the commit, the build time or the token. The only way to obtain it is to let the CLI build a real request,
/// which is what <see cref="TryReadBuild"/> does: a loopback listener is pointed at with <c>ANTHROPIC_BASE_URL</c>, the CLI
/// sends its request, the billing block is read out of it, and the process is killed immediately.
/// </para>
/// <para>
/// That request carries the user's OAuth token in its headers. This class reads only the billing block from the body, never
/// copies the headers anywhere, and logs nothing: the token is already on this machine, and running this must not widen where
/// it appears. Everything here is best effort — no CLI, a slow start, or an unexpected shape all mean "no suffix", which costs
/// a fingerprint detail and nothing else.
/// </para>
/// </remarks>
public static partial class ClaudeCliProbe
{
    /// <summary>How long the whole probe may take before it is abandoned; the CLI is killed at that point.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(8);

    private const int MaxHeadBytes = 64 * 1024;
    private const int MaxBodyBytes = 4 * 1024 * 1024;

    /// <summary>
    /// What one loopback request from the CLI tells us about the client it claims to be.
    /// </summary>
    /// <param name="Build">The suffix it appends to <c>cc_version</c>, or null when the block was not readable.</param>
    /// <param name="Betas">The <c>anthropic-beta</c> list it announces, or null when the header was not readable.</param>
    public sealed record CliWireFacts(string? Build, IReadOnlyList<string>? Betas);

    /// <summary>
    /// The facts, read once per process and reused. Lazy so a process that never makes a subscription request never pays for
    /// it, and thread safe so two concurrent first turns do not start two CLI runs.
    /// </summary>
    public static Lazy<CliWireFacts?> Cached { get; } = new(ProbeOnce, isThreadSafe: true);

    /// <summary>The suffix alone, for callers that only need that much.</summary>
    public static string? CachedBuild => Cached.Value?.Build;

    /// <summary>
    /// The installed CLI to ask, in the order that matches what the user actually runs: the one on <c>PATH</c>, then the
    /// package manager's link, then a local install, and last the binary bundled with the VS Code extension.
    /// </summary>
    public static string? Locate()
    {
        foreach (var candidate in Candidates())
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    /// <summary><c>2.1.317</c> from <c>claude --version</c>, or null when the CLI is missing or answers something else.</summary>
    public static string? ReadVersion(string? cliPath, TimeSpan? budget = null)
    {
        if (string.IsNullOrEmpty(cliPath))
        {
            return null;
        }

        using var process = Start(cliPath, ["--version"], baseUrl: null);
        if (process is null)
        {
            return null;
        }

        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit((int)(budget ?? DefaultBudget).TotalMilliseconds))
        {
            Kill(process);
            return null;
        }

        return VersionRegex().Match(output.GetAwaiter().GetResult()) is { Success: true } match
            ? match.Groups[1].Value
            : null;
    }

    /// <summary>
    /// The build suffix the CLI puts in <c>cc_version</c>, read from a request it builds itself, or null when it cannot be read
    /// (no CLI, a slow start, an unexpected shape). Never throws and never waits longer than <paramref name="budget"/>.
    /// </summary>
    public static string? TryReadBuild(string? cliPath, TimeSpan? budget = null) => TryReadFacts(cliPath, budget)?.Build;

    /// <summary>
    /// The build suffix and the beta list from one request the CLI builds, or null when nothing could be read. Never throws and
    /// never waits longer than <paramref name="budget"/>.
    /// </summary>
    public static CliWireFacts? TryReadFacts(string? cliPath, TimeSpan? budget = null)
    {
        if (string.IsNullOrEmpty(cliPath))
        {
            return null;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (SocketException)
        {
            return null;
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var process = Start(cliPath, ["-p", "x"], baseUrl: $"http://127.0.0.1:{port}");
        if (process is null)
        {
            listener.Stop();
            return null;
        }

        try
        {
            return ReadFactsFromOneRequest(listener, budget ?? DefaultBudget);
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            Kill(process);
            listener.Stop();
        }
    }

    /// <summary>
    /// The <c>cc_version</c> suffix in <paramref name="requestBody"/>: the JSON body of a Messages request, as its first system
    /// block. Public and separate from the socket work so the parsing has a test that does not need a CLI on the machine.
    /// </summary>
    public static string? ExtractBuild(ReadOnlySpan<byte> requestBody)
    {
        var text = Encoding.UTF8.GetString(requestBody);
        return BillingRegex().Match(text) is { Success: true } match
            ? match.Groups[2].Value
            : null;
    }

    private static CliWireFacts? ProbeOnce() => TryReadFacts(Locate());

    /// <summary>
    /// The <c>anthropic-beta</c> list the CLI announces, in its own order: the list is version specific (a 2.1.317 client does
    /// not send <c>prompt-caching-scope</c>, a 2.1.323 one does), so it is read from the same request as the suffix instead of
    /// being frozen in this program.
    /// </summary>
    public static IReadOnlyList<string>? ExtractBetas(ReadOnlySpan<byte> request)
    {
        var head = Encoding.ASCII.GetString(request[..Math.Min(request.Length, MaxHeadBytes)]);
        if (BetasRegex().Match(head) is not { Success: true } match)
        {
            return null;
        }

        var betas = match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return betas.Length == 0 ? null : betas;
    }

    private static CliWireFacts? ReadFactsFromOneRequest(TcpListener listener, TimeSpan budget)
    {
        var accept = listener.AcceptTcpClientAsync();
        if (!accept.Wait(budget))
        {
            return null;
        }

        using var client = accept.Result;
        using var stream = client.GetStream();
        stream.ReadTimeout = (int)budget.TotalMilliseconds;

        var buffer = new byte[Math.Min(MaxBodyBytes, 4 * 1024 * 1024)];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, Math.Min(16 * 1024, buffer.Length - total));
            if (read <= 0)
            {
                break;
            }

            total += read;
            if (HasCompleteBody(buffer.AsSpan(0, total))
                || (ExtractBuild(buffer.AsSpan(0, total)) is not null && ExtractBetas(buffer.AsSpan(0, total)) is not null))
            {
                break;
            }
        }

        var build = ExtractBuild(buffer.AsSpan(0, total));
        var betas = ExtractBetas(buffer.AsSpan(0, total));

        // Answer with a rejection so the CLI stops waiting, then let the caller kill it: it does not retry a 400 and the probe
        // must not leave a request hanging against a listener that is about to close.
        var refusal = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        try
        {
            stream.Write(refusal, 0, refusal.Length);
            stream.Flush();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }

        return build is null && betas is null ? null : new CliWireFacts(build, betas);
    }

    /// <summary>Whether the head and the declared body have both arrived; a request that never completes is cut by the budget.</summary>
    private static bool HasCompleteBody(ReadOnlySpan<byte> received)
    {
        var separator = received.IndexOf("\r\n\r\n"u8);
        if (separator < 0)
        {
            return false;
        }

        var head = Encoding.ASCII.GetString(received[..separator]);
        var length = 0;
        foreach (var line in head.Split("\r\n"))
        {
            if (line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line["content-length:".Length..].Trim(), out var declared))
            {
                length = declared;
            }
        }

        return length > 0 && received.Length - separator - 4 >= length;
    }

    private static Process? Start(string cliPath, string[] arguments, string? baseUrl)
    {
        var info = new ProcessStartInfo(cliPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (baseUrl is not null)
        {
            // Only the destination changes: everything else about the request the CLI builds — the version, the entrypoint, the
            // beta list — is what it would send anywhere else.
            info.Environment["ANTHROPIC_BASE_URL"] = baseUrl;
            info.Environment.Remove("ANTHROPIC_LOG");
            info.Environment.Remove("CLAUDE_CODE_ATTRIBUTION_HEADER");
        }

        try
        {
            var process = Process.Start(info);
            if (process is not null)
            {
                // Nothing is typed into it; a child holding an open stdin can wait for input forever.
                process.StandardInput.Close();
            }

            return process;
        }
        catch (Exception exception) when (exception is not null
            && (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException
                or UnauthorizedAccessException))
        {
            return null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var executable = OperatingSystem.IsWindows() ? "claude.exe" : "claude";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory, executable);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "claude.exe");
            foreach (var package in SafeEnumerate(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "Anthropic.ClaudeCode*"))
            {
                yield return Path.Combine(package, "claude.exe");
            }
        }

        yield return Path.Combine(home, ".claude", "local", executable);
        foreach (var root in new[] { ".vscode", ".vscode-insiders", ".vscode-server" })
        {
            foreach (var extension in SafeEnumerate(Path.Combine(home, root, "extensions"), "anthropic.claude-code-*"))
            {
                yield return Path.Combine(extension, "resources", "native-binary", executable);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerate(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory)
                ? [.. Directory.EnumerateDirectories(directory, pattern)]
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^\s*(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    /// <summary>The billing block as the CLI writes it: <c>cc_version=2.1.317.d83; cc_entrypoint=cli;</c>.</summary>
    [GeneratedRegex(@"x-anthropic-billing-header:\s*cc_version=(\d+\.\d+\.\d+)\.([0-9a-z]{1,8});", RegexOptions.CultureInvariant)]
    private static partial Regex BillingRegex();

    /// <summary>The beta list header, whose name is case-insensitive on the wire.</summary>
    [GeneratedRegex(@"(?im)^anthropic-beta:[ 	]*(.+?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex BetasRegex();
}
