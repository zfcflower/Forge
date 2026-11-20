using Forge.Abstractions;
using Forge.App.ViewModels;
using Forge.Core.Agents;
using Forge.Core.Prompts;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// A waiting message can be taken back and edited: a queued one leaves the queue, an interjection leaves the running turn's
/// pending list — but only until the loop hands it to the model, and the chip has to keep the very instance it can withdraw,
/// because nothing else identifies it.
/// </summary>
public sealed class InterjectionWithdrawTests
{
    /// <summary>A host whose provider is never called: these tests only queue and withdraw, they never run a turn.</summary>
    private static AgentInstance Root() =>
        new AgentHost(new AgentHostOptions
        {
            Providers = static _ => throw new InvalidOperationException("no turn is run here"),
            Tools = [],
            Layers = new PromptLayers("sys", null, null),
        }).CreateRoot("session-withdraw", Directory.GetCurrentDirectory());

    // ---------------------------------------------------------------- Core

    [Fact]
    public void A_withdrawn_interjection_never_reaches_the_model_and_the_others_still_do()
    {
        var root = Root();
        var first = root.Interject("first");
        var second = root.Interject([new TextBlock("second")]);
        var third = root.Interject("third");

        Assert.True(root.Withdraw(second));

        var delivered = root.TakeInterjections();
        Assert.Equal([first, third], delivered);
        Assert.Equal(["first", "third"], delivered.Select(static interjection => interjection.Text));
    }

    [Fact]
    public void An_interjection_the_loop_already_took_cannot_be_withdrawn()
    {
        // The loop takes every pending one together at a tool-batch boundary; from then on it is part of the history.
        var root = Root();
        var taken = root.Interject("already delivered");
        Assert.Equal([taken], root.TakeInterjections());

        Assert.False(root.Withdraw(taken));
    }

    [Fact]
    public void Withdrawing_the_last_one_leaves_no_pending_interjection_behind()
    {
        // The handover check treats a pending interjection as "busy": a withdrawn one must stop counting.
        var root = Root();
        var only = root.Interject("only");
        Assert.True(root.HasInterjections);

        Assert.True(root.Withdraw(only));
        Assert.False(root.HasInterjections);
        Assert.Empty(root.TakeInterjections());
    }

    // ---------------------------------------------------------------- the chip

    [Fact]
    public void An_interjection_chip_can_be_withdrawn_with_and_hands_its_instance_back()
    {
        var root = Root();
        var interjection = root.Interject("hold on");
        var withdrawn = new List<QueuedMessageViewModel>();
        var chip = new QueuedMessageViewModel(
            new ComposerPrompt(interjection.Text, [.. interjection.Attachments]),
            isInterjection: true,
            withdrawn.Add,
            interjection);

        Assert.True(chip.CanWithdraw);
        Assert.Same(interjection, chip.Interjection);
        chip.WithdrawCommand.Execute(null);
        Assert.Equal([chip], withdrawn);
    }

    [Fact]
    public void A_queued_chip_can_be_withdrawn_too()
    {
        var withdrawn = new List<QueuedMessageViewModel>();
        var chip = new QueuedMessageViewModel(ComposerPrompt.FromText("later"), isInterjection: false, withdrawn.Add);

        Assert.True(chip.CanWithdraw);
        Assert.Null(chip.Interjection);
        chip.WithdrawCommand.Execute(null);
        Assert.Equal([chip], withdrawn);
    }
}
