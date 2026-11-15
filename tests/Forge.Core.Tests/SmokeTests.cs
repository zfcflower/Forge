using Forge.Abstractions;
using Xunit;

namespace Forge.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Token_usage_addition_adds_all_four_counters() =>
        Assert.Equal(
            new TokenUsage(3, 5, 7, 11),
            new TokenUsage(1, 2, 3, 4) + new TokenUsage(2, 3, 4, 7));
}
