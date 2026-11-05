#if !DEBUG
using System.Reflection;
using Xunit;

namespace Forge.Integration.Tests;

/// <summary>
/// The host's integration-test seam (<c>IHostTestHook</c>/<c>FileHostTestHook</c>) is compiled into Debug builds of
/// <c>Forge.ExtensionHost</c> only. This test compiles only in Release (the <c>#if !DEBUG</c> guard mirrors the hook's
/// own <c>#if DEBUG</c>) and proves the production host carries neither type, so a Release host never reads
/// <c>FORGE_HOST_TEST_HOOK</c> and never pauses a request for a test to catch up.
/// </summary>
public sealed class ReleaseHookAbsenceTests
{
    [Fact]
    public void Release_host_contains_no_test_hook_types()
    {
        var host = typeof(Forge.ExtensionHost.Program).Assembly;
        var configuration = host.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        Assert.Equal("Release", configuration, ignoreCase: true);

        Assert.Null(host.GetType("Forge.ExtensionHost.IHostTestHook"));
        Assert.Null(host.GetType("Forge.ExtensionHost.FileHostTestHook"));
    }
}
#endif
