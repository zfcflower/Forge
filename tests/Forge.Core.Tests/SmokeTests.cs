using Forge.Abstractions;
using Xunit;

namespace Forge.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void TokenUsage_adds() =>
        Assert.Equal(new TokenUsage(3, 5), new TokenUsage(1, 2) + new TokenUsage(2, 3));
}
