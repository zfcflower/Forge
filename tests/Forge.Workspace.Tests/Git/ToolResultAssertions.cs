using Forge.Abstractions;
using Xunit;

namespace Forge.Workspace.Tests.Git;

/// <summary>How a tool explains a refusal, asserted the same way all over the tool smoke tests.</summary>
internal static class ToolResultAssertions
{
    /// <summary>Awaits <paramref name="pending"/> and asserts the tool refused with an error message carrying every
    /// <paramref name="fragments"/> entry; returns the result so the test can keep asserting on it.</summary>
    public static async Task<ToolResult> RefusedAsync(Task<ToolResult> pending, params string[] fragments)
    {
        var result = await pending;
        Assert.True(result.IsError, result.Content);
        foreach (var fragment in fragments)
        {
            Assert.Contains(fragment, result.Content, StringComparison.Ordinal);
        }

        return result;
    }
}
