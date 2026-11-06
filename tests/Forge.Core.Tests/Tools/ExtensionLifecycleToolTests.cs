using Forge.Core.Tools;
using Xunit;

namespace Forge.Core.Tests.Tools;

/// <summary>disable/delete: argument validation, call passthrough, verbatim result text.</summary>
public sealed class ExtensionLifecycleToolTests
{
    public static IEnumerable<object[]> MissingIdOrVersion()
    {
        yield return new object[] { """{"version":"1.0.0"}""" };
        yield return new object[] { """{"id":"demo"}""" };
        yield return new object[] { """{"id":"","version":"1.0.0"}""" };
        yield return new object[] { """{"id":"demo","version":""}""" };
        yield return new object[] { "{not json" };
        yield return new object[] { "[]" };
    }

    [Theory]
    [MemberData(nameof(MissingIdOrVersion))]
    public async Task Disable_rejects_bad_arguments_without_calling_authoring(string args)
    {
        var authoring = new FakeExtensionAuthoring();
        var tool = new ExtensionDisableTool(authoring);

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, args), ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.StartsWith("extension_disable: ", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, authoring.DisableCalls);
    }

    [Theory]
    [MemberData(nameof(MissingIdOrVersion))]
    public async Task Delete_rejects_bad_arguments_without_calling_authoring(string args)
    {
        var authoring = new FakeExtensionAuthoring();
        var tool = new ExtensionDeleteTool(authoring);

        var result = await tool.InvokeAsync(ExtensionArgs.Call(tool, args), ExtensionArgs.Context, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.StartsWith("extension_delete: ", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, authoring.DeleteCalls);
    }

    [Fact]
    public async Task Disable_forwards_id_and_version_and_returns_the_text_verbatim()
    {
        var authoring = new FakeExtensionAuthoring
        {
            DisableHandler = (id, version, _) => Task.FromResult($"'{id}' {version} is now off; enable it again from settings."),
        };
        var tool = new ExtensionDisableTool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.IdAndVersion("demo", "1.0.0")), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("'demo' 1.0.0 is now off; enable it again from settings.", result.Content);
        var forwarded = Assert.Single(authoring.Disabled);
        Assert.Equal("demo", forwarded.Id);
        Assert.Equal("1.0.0", forwarded.Version);
    }

    [Fact]
    public async Task Delete_forwards_id_and_version_and_returns_the_text_verbatim()
    {
        var authoring = new FakeExtensionAuthoring
        {
            DeleteHandler = (id, version, _) => Task.FromResult($"'{id}' {version} is gone; that version stays blocked."),
        };
        var tool = new ExtensionDeleteTool(authoring);

        var result = await tool.InvokeAsync(
            ExtensionArgs.Call(tool, ExtensionArgs.IdAndVersion("demo", "1.0.0")), ExtensionArgs.Context, CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("'demo' 1.0.0 is gone; that version stays blocked.", result.Content);
        var forwarded = Assert.Single(authoring.Deleted);
        Assert.Equal("demo", forwarded.Id);
        Assert.Equal("1.0.0", forwarded.Version);
    }
}
