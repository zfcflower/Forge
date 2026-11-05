using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Forge.Scripting;
using Forge.ScriptRuntime;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Spawns one real <c>Forge.ScriptExecutor</c> with <c>--pipe &lt;name&gt;</c> and speaks to it
/// directly over its pipe, mirroring <see cref="DirectWorker"/>. Everything started here is torn down
/// by the test; no foreign processes are ever touched.</summary>
internal sealed class SingleUseExecutorSession : IAsyncDisposable
{
    private readonly Process process;
    private readonly NamedPipeServerTransport server;
    private readonly ConcurrentQueue<string> stderr;
    private bool disposed;

    private SingleUseExecutorSession(
        Process process,
        IpcPeer peer,
        NamedPipeServerTransport server,
        ConcurrentQueue<string> stderr,
        string pipe)
    {
        this.process = process;
        Peer = peer;
        this.server = server;
        this.stderr = stderr;
        Pipe = pipe;
    }

    public static string ExecutorAssemblyPath =>
        Path.Combine(AppContext.BaseDirectory, "Forge.ScriptExecutor.dll");

    public IpcPeer Peer { get; }

    public string Pipe { get; }

    public int ProcessId
    {
        get
        {
            try
            {
                return process.Id;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }
    }

    /// <summary>Optional override for <c>host.tool_call</c>: when set, the session answers inner tool calls with it
    /// instead of the default refusal. It receives the bound request (run id, name, arguments), so a test can check
    /// the binding and reply with a canned result.</summary>
    public Func<BoundToolCallRequest, ToolResultPayload>? ToolResponder { get; set; }

    private static Task DispatchAsync(IpcPeer peer, SingleUseExecutorSession? session, IpcMessage message, CancellationToken ct)
    {
        if (session?.ToolResponder is { } responder &&
            string.Equals(message.Operation, ScriptExecutorProtocol.HostToolCall, StringComparison.Ordinal) &&
            message.Arguments is { } args)
        {
            BoundToolCallRequest? call = null;
            try
            {
                call = args.Deserialize(ScriptExecutorJsonContext.Default.BoundToolCallRequest);
            }
            catch (JsonException)
            {
            }

            if (call is not null)
            {
                return peer.RespondAsync(
                    message.Id,
                    JsonSerializer.SerializeToElement(responder(call), ScriptExecutorJsonContext.Default.ToolResultPayload),
                    ct);
            }
        }

        return peer.RespondAsync(
            message.Id,
            JsonDocument.Parse("{\"content\":\"the test host provides no tools.\",\"isError\":true}").RootElement.Clone(),
            ct);
    }

    public bool HasExited
    {
        get
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public IReadOnlyList<string> Stderr => stderr.ToArray();

    public static async Task<SingleUseExecutorSession> StartAsync(CancellationToken ct)
    {
        var pipe = "forge-executor-" + Guid.NewGuid().ToString("N")[..20];
        var server = new NamedPipeServerTransport(pipe, new IpcPipeOptions { ConnectTimeout = TimeSpan.FromSeconds(30) });
        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(ExecutorAssemblyPath);
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipe);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The script executor did not start.");
        var captured = new ConcurrentQueue<string>();
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                captured.Enqueue(args.Data);
            }
        };
        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (InvalidOperationException)
        {
        }

        var accept = server.StartAsync(ct);
        try
        {
            while (!accept.IsCompleted)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The script executor exited with code {process.ExitCode} before connecting on pipe '{pipe}'"
                        + $" (stderr: [{string.Join(" | ", captured)}]); it served no executor.load.");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException(
                        $"The script executor did not connect on pipe '{pipe}' within the bound"
                        + $" (stderr: [{string.Join(" | ", captured)}]).");
                }
            }

            await accept.ConfigureAwait(false);
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            RawHost.Kill(process);
            process.Dispose();
            throw;
        }

        var peer = new IpcPeer(server, new IpcPeerOptions { DefaultTimeout = TimeSpan.FromSeconds(30) });
        SingleUseExecutorSession? created = null;
        peer.RequestReceived += (message, requestCt) => SingleUseExecutorSession.DispatchAsync(peer, created, message, requestCt);
        var session = new SingleUseExecutorSession(process, peer, server, captured, pipe);
        created = session;
        await peer.StartAsync(ct).ConfigureAwait(false);
        return session;
    }

    public void KillTree() => RawHost.Kill(process);

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    public static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            await Peer.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }

        await Peer.DisposeAsync().ConfigureAwait(false);
        await server.DisposeAsync().ConfigureAwait(false);
        RawHost.Kill(process);
        process.Dispose();
    }
}

/// <summary>Builds valid-shaped <c>executor.load</c> payloads over synthetic image bytes so each test can
/// corrupt exactly one descriptor field. The bytes are arbitrary data, never a real assembly: the
/// executor must reject them before loading, so no script code can run.</summary>
internal static class SingleUseExecutorArtifacts
{
    public static byte[] ImageBytes(int seed = 0)
    {
        var bytes = new byte[128];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)((index + seed) & 0xFF);
        }

        return bytes;
    }

    public static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static string CorruptHex(string hex) =>
        (hex[0] == 'a' ? 'b' : 'a') + hex[1..];

    public static string NewRunId() => Guid.NewGuid().ToString("N");

    /// <summary>Compiles <paramref name="code"/> with a fresh compiler and returns the raw compilation, so a test can
    /// assert on a failed compile without ever starting an executor.</summary>
    public static Task<ScriptCompilation> CompileRawAsync(string code, CodeExecutionOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(options);
        return new ScriptCodeCompiler().CompileAsync(code, options, ct);
    }

    /// <summary>Compiles <paramref name="code"/> and fails the test with the compiler's bounded error text when it
    /// does not compile.</summary>
    public static async Task<ScriptCompilation> CompileValidAsync(string code, CodeExecutionOptions options, CancellationToken ct)
    {
        var compiled = await CompileRawAsync(code, options, ct).ConfigureAwait(false);
        Assert.True(compiled.Ok, "The script did not compile: " + compiled.Error);
        Assert.NotNull(compiled.Image);
        Assert.NotNull(compiled.Descriptor);
        return compiled;
    }

    /// <summary>Delivers a compiled image to the executor; the request succeeding means the bytes validated and loaded.</summary>
    public static Task<IpcMessage> LoadAsync(SingleUseExecutorSession session, ScriptCompilation compiled, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(compiled);
        Assert.NotNull(compiled.Image);
        Assert.NotNull(compiled.Descriptor);
        return session.Peer.SendRequestAsync(ScriptExecutorProtocol.Load, LoadArgs(compiled.Image, compiled.Descriptor), ct);
    }

    /// <summary>Builds the <c>executor.run</c> arguments for one bound run.</summary>
    public static ScriptRunRequest RunRequest(
        string runId,
        IReadOnlyList<ToolDeclaration> tools,
        CodeExecutionOptions options,
        int timeoutMilliseconds) =>
        new(
            new ExtensionCallContext(runId, "single-use-test", Path.GetTempPath(), 0, []),
            timeoutMilliseconds,
            tools,
            options);

    /// <summary>Runs the loaded image once and decodes the <c>CodeExecutionResult</c> the executor reports.</summary>
    public static async Task<CodeExecutionResult> RunOnceAsync(SingleUseExecutorSession session, ScriptRunRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var response = await session.Peer.SendRequestAsync(
            ScriptExecutorProtocol.Run,
            JsonSerializer.SerializeToElement(request, ScriptExecutorJsonContext.Default.ScriptRunRequest),
            ct).ConfigureAwait(false);
        Assert.True(response.Result.HasValue, "The executor.run response carries no result.");
        var result = response.Result.Value.Deserialize(ScriptExecutorJsonContext.Default.CodeExecutionResult);
        Assert.NotNull(result);
        return result;
    }

    public static ScriptArtifactDescriptor Describe(byte[] image) => new(
        AssemblyName: "rejection-probe",
        SourceHash: Sha256Hex("rejection-probe-source"u8.ToArray()),
        CacheKey: "rejection-probe-cache-key",
        ContractVersion: ScriptArtifact.ContractVersion,
        RuntimeVersion: Environment.Version.ToString(),
        EntryPointType: "Probe.Entry",
        EntryPointMethod: "Run",
        GlobalsMvid: typeof(ScriptArtifact).Assembly.ManifestModule.ModuleVersionId.ToString("D"),
        PeSha256: Sha256Hex(image),
        PeLength: image.Length);

    public static JsonElement LoadArgs(byte[] image, ScriptArtifactDescriptor descriptor) =>
        JsonSerializer.SerializeToElement(
            new ScriptArtifactLoadRequest(Convert.ToBase64String(image), descriptor),
            ScriptExecutorJsonContext.Default.ScriptArtifactLoadRequest);

    public static async Task<IpcRemoteException> ExpectLoadRejectedAsync(
        SingleUseExecutorSession session, byte[] image, ScriptArtifactDescriptor descriptor, CancellationToken ct)
    {
        var remote = await Assert.ThrowsAsync<IpcRemoteException>(
            () => session.Peer.SendRequestAsync(ScriptExecutorProtocol.Load, LoadArgs(image, descriptor), ct));
        Assert.False(string.IsNullOrWhiteSpace(remote.Message), "The executor rejection carried no error detail.");
        Assert.Equal("invalid_artifact", remote.Code);
        return remote;
    }
}

/// <summary>Wire-contract rejection tests for the single-use <c>run_code</c> executor: every malformed
/// <c>executor.load</c> must answer with an <c>IpcError</c> envelope and run nothing. Each test then sends
/// a second, differently-malformed load to prove the executor is still serving and never loaded the
/// first image; there is no marker file because the images are not real assemblies and cannot run.</summary>
public sealed class SingleUseExecutorRejectionTests
{
    private static readonly TimeSpan StepLimit = TestBudgets.Safety(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(10));

    [Fact]
    public async Task Load_WithLengthMismatch_IsRejectedAndRunsNothing()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var image = SingleUseExecutorArtifacts.ImageBytes();
        var bad = SingleUseExecutorArtifacts.Describe(image) with { PeLength = image.Length + 1 };
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(session, image, bad, ct);
        Assert.False(session.HasExited, "The executor died on a rejected load; a rejection must not kill the process.");
        var second = SingleUseExecutorArtifacts.ImageBytes(seed: 7);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(
            session, second, SingleUseExecutorArtifacts.Describe(second) with { PeLength = second.Length - 1 }, ct);
    }

    [Fact]
    public async Task Load_WithHashMismatch_IsRejectedAndRunsNothing()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var image = SingleUseExecutorArtifacts.ImageBytes(seed: 3);
        var valid = SingleUseExecutorArtifacts.Describe(image);
        var bad = valid with { PeSha256 = SingleUseExecutorArtifacts.CorruptHex(valid.PeSha256) };
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(session, image, bad, ct);
        Assert.False(session.HasExited, "The executor died on a rejected load; a rejection must not kill the process.");
        var second = SingleUseExecutorArtifacts.ImageBytes(seed: 11);
        var secondValid = SingleUseExecutorArtifacts.Describe(second);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(
            session, second, secondValid with { PeSha256 = SingleUseExecutorArtifacts.CorruptHex(secondValid.PeSha256) }, ct);
    }

    [Fact]
    public async Task Load_WithUnknownGlobalsMvid_IsRejected()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var image = SingleUseExecutorArtifacts.ImageBytes(seed: 5);
        var bad = SingleUseExecutorArtifacts.Describe(image) with { GlobalsMvid = Guid.NewGuid().ToString("D") };
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(session, image, bad, ct);
        Assert.False(session.HasExited, "The executor died on a rejected load; a rejection must not kill the process.");
        var second = SingleUseExecutorArtifacts.ImageBytes(seed: 13);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(
            session, second, SingleUseExecutorArtifacts.Describe(second) with { GlobalsMvid = Guid.NewGuid().ToString("D") }, ct);
    }

    [Fact]
    public async Task Load_WithAnotherArtifactContract_IsRejected()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var image = SingleUseExecutorArtifacts.ImageBytes(seed: 17);
        var bad = SingleUseExecutorArtifacts.Describe(image) with { ContractVersion = "forge-script-artifact/v0" };
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        var remote = await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(session, image, bad, ct);
        Assert.Contains("forge-script-artifact/v0", remote.Message, StringComparison.Ordinal);
        Assert.False(session.HasExited, "The executor died on a rejected load; a rejection must not kill the process.");
        var second = SingleUseExecutorArtifacts.ImageBytes(seed: 19);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(
            session, second, SingleUseExecutorArtifacts.Describe(second) with { ContractVersion = "1" }, ct);
    }

    [Fact]
    public async Task Load_WithOversizedImage_IsRejected()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var image = SingleUseExecutorArtifacts.ImageBytes(seed: 9);
        var bad = SingleUseExecutorArtifacts.Describe(image) with { PeLength = ScriptArtifact.MaxAssemblyBytes + 1 };
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(session, image, bad, ct);
        Assert.False(session.HasExited, "The executor died on a rejected load; a rejection must not kill the process.");
        var second = SingleUseExecutorArtifacts.ImageBytes(seed: 17);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(
            session,
            second,
            SingleUseExecutorArtifacts.Describe(second) with { PeLength = ScriptArtifact.MaxAssemblyBytes + 1 },
            ct);
    }

    [Fact]
    public async Task Load_WithMismatchedRuntimeVersion_IsRejected()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var image = SingleUseExecutorArtifacts.ImageBytes(seed: 21);
        var bad = SingleUseExecutorArtifacts.Describe(image) with { RuntimeVersion = "0.0.0-fake" };
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(session, image, bad, ct);
        Assert.False(session.HasExited, "The executor died on a rejected load; a rejection must not kill the process.");
        var second = SingleUseExecutorArtifacts.ImageBytes(seed: 29);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(
            session, second, SingleUseExecutorArtifacts.Describe(second) with { RuntimeVersion = "0.0.0-fake" }, ct);
    }

    [Fact]
    public async Task Load_WithBlankEntryPointType_IsRejected()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var image = SingleUseExecutorArtifacts.ImageBytes(seed: 31);
        var bad = SingleUseExecutorArtifacts.Describe(image) with { EntryPointType = "" };
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(session, image, bad, ct);
        Assert.False(session.HasExited, "The executor died on a rejected load; a rejection must not kill the process.");
        var second = SingleUseExecutorArtifacts.ImageBytes(seed: 37);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(
            session, second, SingleUseExecutorArtifacts.Describe(second) with { EntryPointType = "  " }, ct);
    }

    [Fact]
    public async Task Load_WithPatchLevelRuntimeDifference_IsAccepted()
    {
        // Boundary pin for the documented rule: a descriptor whose runtime shares major.minor with the executor
        // and differs only in patch is accepted, so a servicing update does not invalidate cached scripts.
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var options = new CodeExecutionOptions();
        var compiled = await SingleUseExecutorArtifacts.CompileValidAsync("return \"patch-ok\";", options, ct);
        Assert.NotNull(compiled.Image);
        Assert.NotNull(compiled.Descriptor);
        var current = Environment.Version;
        var patched = $"{current.Major}.{current.Minor}.{Math.Max(0, current.Build) + 1}";
        Assert.NotEqual(compiled.Descriptor.RuntimeVersion, patched);
        Assert.True(
            ScriptArtifact.IsCompatibleRuntimeVersion(patched),
            $"A patch-only runtime '{patched}' should be compatible with '{compiled.Descriptor.RuntimeVersion}'.");
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        await session.Peer.SendRequestAsync(
            ScriptExecutorProtocol.Load,
            SingleUseExecutorArtifacts.LoadArgs(compiled.Image, compiled.Descriptor with { RuntimeVersion = patched }),
            ct);
        var result = await SingleUseExecutorArtifacts.RunOnceAsync(
            session,
            SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), [], options, 15000),
            ct);
        Assert.False(result.IsError, result.Content);
        Assert.Equal("patch-ok", result.Content);
    }

    [Fact]
    public void Decode_WithOverlongBase64_IsRejectedBeforeAllocating()
    {
        // This trips the encoded-length bound inside ScriptArtifact.Decode itself. The oversized executor.load
        // test only trips the descriptor PeLength check, which is a different bound on a different field.
        var overlong = new string('A', ScriptArtifact.MaxEncodedAssemblyChars + 1);
        Assert.True(
            overlong.Length > ScriptArtifact.MaxEncodedAssemblyChars,
            "The test input must exceed the encoded-image bound.");
        var exception = Assert.Throws<ScriptArtifactException>(() => ScriptArtifact.Decode(overlong));
        Assert.Contains("the limit is", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
