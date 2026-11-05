using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Forge.Ipc;
using Forge.Plugin.Sdk;
using Forge.ScriptRuntime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>Protocol-strictness tests for the single-use executor: <c>executor.run</c> without a successful
/// load must error, a rejected load must still allow a second load, and plugin/worker operations must be
/// refused so the executor never serves the plugin surface.</summary>
public sealed class SingleUseExecutorProtocolTests
{
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(60);

    private static JsonElement EmptyArgs() => JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement WellFormedRunArgs() =>
        JsonSerializer.SerializeToElement(
            new ScriptRunRequest(
                new ExtensionCallContext(
                    Guid.NewGuid().ToString("N"),
                    "executor-probe",
                    Path.GetTempPath(),
                    0,
                    []),
                5000,
                [],
                new CodeExecutionOptions()),
            ScriptExecutorJsonContext.Default.ScriptRunRequest);

    [Fact]
    public async Task Run_WithoutSuccessfulLoad_IsRejected()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        var remote = await Assert.ThrowsAsync<IpcRemoteException>(
            () => session.Peer.SendRequestAsync(ScriptExecutorProtocol.Run, WellFormedRunArgs(), ct));
        Assert.False(string.IsNullOrWhiteSpace(remote.Message), "The executor.run rejection carried no error detail.");
        Assert.Equal("not_loaded", remote.Code);
    }

    [Fact]
    public async Task PluginAndWorkerOps_AreRefused()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        var pluginOp = await Assert.ThrowsAsync<IpcRemoteException>(
            () => session.Peer.SendRequestAsync(ExtensionProtocol.PluginExecute, EmptyArgs(), ct));
        Assert.False(string.IsNullOrWhiteSpace(pluginOp.Message), "The plugin.execute refusal carried no error detail.");
        Assert.Equal("unsupported_operation", pluginOp.Code);
        var workerOp = await Assert.ThrowsAsync<IpcRemoteException>(
            () => session.Peer.SendRequestAsync(ExtensionProtocol.WorkerLoad, EmptyArgs(), ct));
        Assert.False(string.IsNullOrWhiteSpace(workerOp.Message), "The worker.load refusal carried no error detail.");
        Assert.Equal("unsupported_operation", workerOp.Code);
    }

    [Fact]
    public async Task RejectedLoad_KeepsExecutorUsableForSecondLoad()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        await using var session = await SingleUseExecutorSession.StartAsync(ct);
        var first = SingleUseExecutorArtifacts.ImageBytes(seed: 41);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(
            session,
            first,
            SingleUseExecutorArtifacts.Describe(first) with { EntryPointMethod = "" },
            ct);
        var second = SingleUseExecutorArtifacts.ImageBytes(seed: 43);
        var secondValid = SingleUseExecutorArtifacts.Describe(second);
        await SingleUseExecutorArtifacts.ExpectLoadRejectedAsync(
            session,
            second,
            secondValid with { PeSha256 = SingleUseExecutorArtifacts.CorruptHex(secondValid.PeSha256) },
            ct);
        var run = await Assert.ThrowsAsync<IpcRemoteException>(
            () => session.Peer.SendRequestAsync(ScriptExecutorProtocol.Run, WellFormedRunArgs(), ct));
        Assert.False(string.IsNullOrWhiteSpace(run.Message), "The post-rejection executor.run error carried no detail.");
        Assert.Equal("not_loaded", run.Code);
    }
}

/// <summary>Executor hygiene evidence: the published closure must carry no Roslyn, killing the process tree
/// must leave no executor behind, and a same-named decoy beside the executor must never be loaded.</summary>
[Collection("SingleUseExecutorFilesystem")]
public sealed class SingleUseExecutorHygieneTests
{
    private static readonly TimeSpan StepLimit = TimeSpan.FromSeconds(60);

    [Fact]
    public void ExecutorDependencyManifest_DeclaresNoRoslynOrScriptingDependencies()
    {
        // What this checks, honestly: the executor's dependency manifest, not its publish bytes. A publish-dir
        // file check is infeasible without a csproj change because the test output directory is shared: it
        // legitimately carries Microsoft.CodeAnalysis.* and Forge.Scripting.dll (Forge.Scripting is referenced
        // transitively), so the presence of those files beside Forge.ScriptExecutor.dll says nothing about the
        // executor's own closure. The deps.json libraries section is the checkable surface for that closure.
        var depsPath = Path.Combine(AppContext.BaseDirectory, "Forge.ScriptExecutor.deps.json");
        Assert.True(File.Exists(depsPath), "The executor's dependency manifest was not copied to the test output.");
        var deps = File.ReadAllText(depsPath);
        Assert.DoesNotContain("Microsoft.CodeAnalysis", deps, StringComparison.Ordinal);
        Assert.DoesNotContain("Forge.Scripting", deps, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(deps);
        Assert.True(
            document.RootElement.TryGetProperty("libraries", out var libraries),
            "The executor's dependency manifest has no libraries section.");
        foreach (var library in libraries.EnumerateObject())
        {
            Assert.False(
                library.Name.StartsWith("Microsoft.CodeAnalysis/", StringComparison.Ordinal),
                $"The executor closure references '{library.Name}'. ");
            Assert.False(
                library.Name.StartsWith("Forge.Scripting/", StringComparison.Ordinal),
                $"The executor closure references '{library.Name}'.");
        }
    }

    [Fact]
    public async Task KillTree_LeavesNoExecutorProcess()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var session = await SingleUseExecutorSession.StartAsync(ct);
        try
        {
            var pid = session.ProcessId;
            Assert.True(pid > 0 && SingleUseExecutorSession.IsRunning(pid), "The executor was not running after it connected.");
            session.KillTree();
            using var exited = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await session.WaitForExitAsync(exited.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"The executor process tree (pid {pid}) was still alive 10 s after the kill.");
            }

            Assert.False(
                SingleUseExecutorSession.IsRunning(pid),
                $"The executor process tree (pid {pid}) survived the kill.");
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    [Fact]
    public async Task DecoyAssembly_BesideExecutor_IsNotLoaded()
    {
        using var budget = new CancellationTokenSource(StepLimit);
        var ct = budget.Token;
        var options = new CodeExecutionOptions();
        var verified = await SingleUseExecutorArtifacts.CompileValidAsync("return \"verified-marker\";", options, ct);
        Assert.NotNull(verified.Descriptor);
        Assert.NotNull(verified.Image);
        var directory = Path.GetDirectoryName(SingleUseExecutorSession.ExecutorAssemblyPath);
        Assert.False(string.IsNullOrWhiteSpace(directory), "The executor directory could not be determined.");
        var decoyPath = Path.Combine(directory!, verified.Descriptor.AssemblyName + ".dll");
        // The premise must actually hold: the decoy is a real assembly carrying the SAME assembly name as the
        // verified image (emitted with Roslyn, available transitively via Forge.Scripting; no new package), with
        // different bytes. The executor loads its image from memory (LoadFromStream) and never probes the disk,
        // so the same-named file beside it must never be picked up.
        var decoyBytes = EmitDecoyAssembly(verified.Descriptor.AssemblyName);
        Assert.False(decoyBytes.SequenceEqual(verified.Image), "The decoy must differ from the verified image.");
        try
        {
            if (File.Exists(decoyPath))
            {
                File.Delete(decoyPath);
            }

            File.WriteAllBytes(decoyPath, decoyBytes);
            Assert.Equal(
                verified.Descriptor.AssemblyName,
                AssemblyName.GetAssemblyName(decoyPath).Name);
            await using var session = await SingleUseExecutorSession.StartAsync(ct);
            Assert.True(File.Exists(decoyPath), "The decoy was not in place beside the executor when it ran.");
            await SingleUseExecutorArtifacts.LoadAsync(session, verified, ct);
            var result = await SingleUseExecutorArtifacts.RunOnceAsync(
                session,
                SingleUseExecutorArtifacts.RunRequest(SingleUseExecutorArtifacts.NewRunId(), [], options, 15000),
                ct);
            Assert.False(result.IsError, result.Content);
            Assert.Equal("verified-marker", result.Content);
        }
        finally
        {
            try
            {
                if (File.Exists(decoyPath))
                {
                    File.Delete(decoyPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static byte[] EmitDecoyAssembly(string assemblyName)
    {
        var tree = CSharpSyntaxTree.ParseText(
            "public static class DecoyProbe { public static string Marker() => \"decoy-marker\"; }");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var stream = new MemoryStream();
        EmitResult emit = compilation.Emit(stream);
        Assert.True(
            emit.Success,
            "The same-name decoy did not emit: " + string.Join("; ", emit.Diagnostics.Select(d => d.ToString())));
        return stream.ToArray();
    }
}
