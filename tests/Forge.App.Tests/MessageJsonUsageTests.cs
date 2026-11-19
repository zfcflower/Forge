using Forge.Abstractions;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The persisted usage JSON's one-hour cache-write field: it round-trips when present, reads as zero from the rows an
/// older build wrote, and costs the old rows' shape nothing when there is nothing to write.
/// </summary>
public sealed class MessageJsonUsageTests
{
    [Fact]
    public void The_one_hour_cache_write_round_trips()
    {
        var usage = new TokenUsage(100, 50, 20, 10, 5);
        Assert.Equal(usage, MessageJson.ParseUsage(MessageJson.SerializeUsage(usage)));
    }

    [Fact]
    public void A_row_without_the_field_parses_with_one_hour_as_zero()
    {
        Assert.Equal(
            new TokenUsage(10, 20, 1, 2, 0),
            MessageJson.ParseUsage("""{"input":10,"output":20,"cache_read":1,"cache_write":2}"""));
    }

    [Fact]
    public void A_zero_one_hour_write_serializes_to_the_old_four_field_bytes()
    {
        Assert.Equal(
            """{"input":1,"output":2,"cache_read":3,"cache_write":4}""",
            MessageJson.SerializeUsage(new TokenUsage(1, 2, 3, 4, 0)));
    }
}
