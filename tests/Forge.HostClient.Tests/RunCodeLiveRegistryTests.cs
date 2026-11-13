using Forge.Abstractions;
using Xunit;

namespace Forge.HostClient.Tests;

/// <summary>
/// The <c>run_code</c> proxy must survive living inside the registry it describes. The pre-fix proxy
/// re-enters its own <c>Definition</c> from its fingerprint and overflows the stack, so the scenario
/// below runs in an isolated <c>dotnet test</c> child process (see
/// <see cref="RunCodeRegistryChildProcess"/>): a failure kills the child (nonzero exit, no markers)
/// and can never take down the test runner.
/// </summary>
public sealed class RunCodeLiveRegistryTests
{
    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Proxy_inside_its_own_live_registry_survives_definition_and_invoke()
    {
        using var child = RunCodeRegistryChildProcess.Start(RunCodeRegistryChildProcess.LiveRegistryMode);
        try
        {
            var stdout = child.StandardOutput.ReadToEndAsync();
            var stderr = child.StandardError.ReadToEndAsync();
            var exited = await Task.Run(() => child.WaitForExit(ChildTimeout));
            if (!exited)
            {
                Assert.Fail("The registry child did not exit inside the bound; it was killed with its process tree.");
            }

            var output = await stdout.WaitAsync(DrainTimeout);
            var errors = await stderr.WaitAsync(DrainTimeout);
            var combined = output + Environment.NewLine + errors;
            Assert.True(
                child.ExitCode == 0,
                "The registry child exits 0 but saw " + child.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". Output: " + combined);
            Assert.Contains(RunCodeRegistryChildProcess.DefinitionOk, combined);
            Assert.Contains(RunCodeRegistryChildProcess.InvokeOk, combined);
            Assert.Contains(RunCodeRegistryChildProcess.MutualOk, combined);
            Assert.Contains(RunCodeRegistryChildProcess.UpdatesOk, combined);
            Assert.Contains(RunCodeRegistryChildProcess.SuccessMarker, combined);
        }
        finally
        {
            if (!child.HasExited)
            {
                try
                {
                    child.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }

                child.WaitForExit(DrainTimeout);
            }
        }
    }

    /// <summary>
    /// The isolated child entry point the parent test above drives. It only runs the scenario when the
    /// child harness environment requests it; normal suite runs return here so the old fatal stack
    /// overflow can never execute in the test runner.
    /// </summary>
    [Fact]
    public async Task Live_registry_child_scenario()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(RunCodeRegistryChildProcess.ModeVariable),
                RunCodeRegistryChildProcess.LiveRegistryMode,
                StringComparison.Ordinal))
        {
            return;
        }

        await using var host = new FakeHost();
        await using var client = await ClientFactory.StartAsync(host);

        // The proxy lives inside the very registry it describes: pre-fix Definition -> Fingerprint
        // re-enters the proxy's own Definition and overflows the stack right here.
        var registry = new TestRegistry([new StubTool("normal")]);
        var proxy = client.CreateRunCodeTool(registry);
        registry.Add(proxy);

        var description = proxy.Definition.Description;
        RunCodeRegistryChildProcess.Check(
            description.Contains("normal", StringComparison.Ordinal), "the live description lists the normal tool");
        RunCodeRegistryChildProcess.Check(
            !description.Contains("\n  run_code ", StringComparison.Ordinal), "the live description excludes run_code");
        Console.Out.WriteLine(RunCodeRegistryChildProcess.DefinitionOk);
        Console.Out.Flush();

        var hold = new InFlightRoot { ResultJson = """{"content":"ok","isError":false}""" };
        BoundRoots.ParkRoot(host, "code.execute", hold);
        var directory = BoundRoots.AbsoluteDir("child-live");
        var invocation = proxy.InvokeAsync(
            new ToolCall("rc-1", "run_code", "{\"code\":\"1+1\"}"),
            new ToolContext("sess", directory),
            CancellationToken.None).AsTask();
        hold.Release();
        var result = await invocation.Bounded();
        RunCodeRegistryChildProcess.Check(!result.IsError, "the live-registry invoke succeeds");
        var tools = RunCodeRegistryChildProcess.ToolNamesOf(
            Assert.Single(host.Requests("code.execute")));
        RunCodeRegistryChildProcess.Check(
            tools.Count == 1 && tools[0] == "normal", "code.execute carries only the normal tool");
        Console.Out.WriteLine(RunCodeRegistryChildProcess.InvokeOk);
        Console.Out.Flush();

        // Two proxies sharing one registry must not recurse through each other either.
        var shared = new RunCodeRegistryChildProcess.LiveListRegistry();
        shared.Add(new StubTool("normal"));
        var proxyA = client.CreateRunCodeTool(shared);
        shared.Add(proxyA);
        var proxyB = client.CreateRunCodeTool(shared);
        shared.Add(proxyB);
        var descriptionA = proxyA.Definition.Description;
        var descriptionB = proxyB.Definition.Description;
        RunCodeRegistryChildProcess.Check(
            string.Equals(descriptionA, descriptionB, StringComparison.Ordinal),
            "both proxies share one stable description");
        RunCodeRegistryChildProcess.Check(
            !descriptionA.Contains("\n  run_code ", StringComparison.Ordinal),
            "the shared description excludes run_code");
        var second = await proxyA.InvokeAsync(
            new ToolCall("rc-2", "run_code", "{\"code\":\"2\"}"),
            new ToolContext("sess", directory),
            CancellationToken.None);
        RunCodeRegistryChildProcess.Check(!second.IsError, "the shared-registry invoke succeeds");
        foreach (var request in host.Requests("code.execute"))
        {
            var names = RunCodeRegistryChildProcess.ToolNamesOf(request);
            RunCodeRegistryChildProcess.Check(
                !names.Contains("run_code", StringComparer.Ordinal), "every code.execute surface excludes run_code");
            RunCodeRegistryChildProcess.Check(
                names.Contains("normal", StringComparer.Ordinal), "every code.execute surface keeps the normal tool");
        }

        Console.Out.WriteLine(RunCodeRegistryChildProcess.MutualOk);
        Console.Out.Flush();

        // Live add/remove refreshes the proxy and insertion order never leaks into the text.
        RunCodeRegistryChildProcess.Check(
            !descriptionA.Contains("fresh", StringComparison.Ordinal), "the baseline has no fresh tool");
        shared.Add(new StubTool("fresh"));
        var updated = proxyA.Definition.Description;
        RunCodeRegistryChildProcess.Check(
            updated.Contains("fresh", StringComparison.Ordinal), "adding a tool refreshes the description");
        RunCodeRegistryChildProcess.Check(
            updated.Contains("normal", StringComparison.Ordinal), "the refresh keeps the normal tool");
        shared.Remove("fresh");
        var reverted = proxyA.Definition.Description;
        RunCodeRegistryChildProcess.Check(
            string.Equals(reverted, descriptionA, StringComparison.Ordinal),
            "removing a tool restores the description");
        var early = new TestRegistry([new StubTool("b"), new StubTool("a")]);
        var late = new TestRegistry([new StubTool("a"), new StubTool("b")]);
        RunCodeRegistryChildProcess.Check(
            string.Equals(
                client.CreateRunCodeTool(early).Definition.Description,
                client.CreateRunCodeTool(late).Definition.Description,
                StringComparison.Ordinal),
            "the description order is stable");
        Console.Out.WriteLine(RunCodeRegistryChildProcess.UpdatesOk);
        Console.Out.Flush();

        Console.Out.WriteLine(RunCodeRegistryChildProcess.SuccessMarker);
        Console.Out.Flush();
    }
}
