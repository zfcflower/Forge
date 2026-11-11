using Forge.App.Notifications;
using Xunit;

namespace Forge.App.Tests.Notifications;

/// <summary>Pins the pure stacking arithmetic of <see cref="ToastStackLayout"/>.</summary>
public sealed class ToastStackLayoutTests
{
    [Fact]
    public void Empty_has_no_offsets()
    {
        Assert.Empty(ToastStackLayout.Offsets([], 8));
    }

    [Fact]
    public void One_item_sits_at_zero()
    {
        Assert.Equal([0d], ToastStackLayout.Offsets([20], 8));
    }

    [Fact]
    public void Three_items_stack_with_spacing_between_them_only()
    {
        var offsets = ToastStackLayout.Offsets([10, 20, 30], 8);

        Assert.Equal([0d, 18d, 46d], offsets);
    }

    [Fact]
    public void Spacing_is_not_added_after_the_last_item()
    {
        var one = ToastStackLayout.Offsets([10], 8);
        var two = ToastStackLayout.Offsets([10, 10], 8);

        Assert.Equal([0d], one);
        Assert.Equal([0d, 18d], two);
    }

    [Fact]
    public void Zero_height_item_still_advances_by_the_spacing()
    {
        Assert.Equal([0d, 8d], ToastStackLayout.Offsets([0, 10], 8));
        Assert.Equal([0d, 10d], ToastStackLayout.Offsets([10, 0], 0));
    }
}
