using Forge.Core.Agents;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The receipts a reduction writes in place of a dropped tool result. Every rendered receipt must fit inside
/// <see cref="ContextReceipt.MinCroppableChars"/> — the second reduction pass would otherwise crop the receipt itself
/// and the projection would stop being a fixed point — and the find-back reference (<c>read_history</c>) survives every
/// cut, so the model can always retrieve the original.
/// </summary>
public sealed class ContextReceiptTests
{
    /// <summary>The deepest path a real session produces, with a line range: the middle goes, the root and the file name stay.</summary>
    [Fact]
    public void Read_deep_path_with_range_stays_croppable_and_keeps_the_find_back_reference()
    {
        const string path = "D:\\FORGE\\forge\\src\\Forge.App\\ViewModels\\Settings\\Tools\\Sections\\ToolsSettingsSectionViewModel.cs";

        var receipt = ContextReceipt.Read(path, 1, 420, 32_000, 118);

        Assert.True(receipt.Length < ContextReceipt.MinCroppableChars, $"receipt was {receipt.Length} chars: {receipt}");
        Assert.Contains("read_history 118", receipt);
        Assert.Contains("…", receipt);
        Assert.Contains("ToolsSettingsSectionViewModel.cs:1-420", receipt);
    }

    /// <summary>A command line longer than the line budget loses its end, never its verb or the find-back reference.</summary>
    [Fact]
    public void Command_long_line_loses_its_tail_not_the_verb_or_the_reference()
    {
        const string command =
            "dotnet build src/Forge.App -c Release -p:PublishAot=true -p:UseAppHost=false -p:SelfContained=false -v q --nologo --no-restore";

        var receipt = ContextReceipt.Command("bash", command, 480_000, 119);

        Assert.True(receipt.Length < ContextReceipt.MinCroppableChars, $"receipt was {receipt.Length} chars: {receipt}");
        Assert.Contains("read_history 119", receipt);
        Assert.StartsWith("[omitted: read_history 119 · bash dotnet build src/Forge.App", receipt);
        Assert.EndsWith("chars]", receipt);
        Assert.DoesNotContain("--no-restore", receipt);
    }

    [Fact]
    public void Image_receipt_is_the_one_fixed_line()
    {
        Assert.Equal("[omitted: image]", ContextReceipt.Image());
    }

    /// <summary>
    /// The constants must leave room for the whole replacement: line + kept head + kept tail + the markers between them.
    /// The worst real case is checked too, because the fixed frame around the line is longer than the marker alone.
    /// </summary>
    [Fact]
    public void The_worst_replacement_stays_inside_the_croppable_minimum()
    {
        // The real worst case, not a stand-in: the longest croppable text, the widest range, the largest char count and
        // a sequence number at long's ceiling - if this combination fits, every real receipt does, and the second pass
        // can never crop a receipt out from under the fixed point.
        const string path = "D:\\FORGE\\forge\\src\\Forge.App\\ViewModels\\Settings\\Tools\\Sections\\ToolsSettingsSectionViewModel.cs";
        var worst = ContextReceipt.Cropped(
            new string('x', ContextReceipt.MinCroppableChars + 1),
            ContextReceipt.Read(path, 1, int.MaxValue, int.MaxValue, long.MaxValue));
        Assert.True(worst.Length < ContextReceipt.MinCroppableChars, $"worst replacement was {worst.Length} chars");
    }
}
