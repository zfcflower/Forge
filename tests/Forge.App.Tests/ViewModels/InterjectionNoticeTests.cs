using Forge.Abstractions;
using Forge.App.Localization;
using Forge.App.Preferences;
using Forge.App.Tests.Localization;
using Forge.App.ViewModels;
using Forge.Core.Storage;
using Xunit;

namespace Forge.App.Tests.ViewModels;

/// <summary>
/// The notice a delivered interjection leaves on the turn card, and the pending chip it takes off the composer queue:
/// an attachment-only interjection counts and names its attachments, a mixed one quotes the text and counts the rest, and
/// the chip is matched by the very attachment blocks the loop reports back.
/// </summary>
[Collection("forge-app-ui")]
public sealed class InterjectionNoticeTests
{
    private static readonly ImageBlock Image = new("image/png", "AAAA");
    private static readonly FileBlock Pdf = new("application/pdf", "JVBERi0=", "report.pdf");

    // ---------------------------------------------------------------- the notice

    [Fact]
    public void Text_only_keeps_the_plain_notice()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        Assert.Equal("插话：also check the tests", SessionStrings.ComposerInterjectedNotice("also check the tests", []).Resolve());
    }

    [Fact]
    public void Attachments_only_counts_and_names_them_with_a_placeholder_for_an_unnamed_image()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        Assert.Equal("插话：2 个附件（图片、report.pdf）", SessionStrings.ComposerInterjectedNotice(string.Empty, [Image, Pdf]).Resolve());
        Assert.Equal("插话：1 个附件（图片）", SessionStrings.ComposerInterjectedNotice("   ", [Image]).Resolve());
    }

    [Fact]
    public void An_inlined_text_file_and_an_unnamed_file_are_named_too()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);
        var text = TextAttachment.Create("notes.md", "# notes");
        var unnamed = new FileBlock("application/pdf", "JVBERi0=");

        Assert.Equal(
            "插话：2 个附件（notes.md、application/pdf）",
            SessionStrings.ComposerInterjectedNotice(string.Empty, [text, unnamed]).Resolve());
    }

    [Fact]
    public void Text_with_attachments_quotes_the_text_and_counts_the_attachments()
    {
        using var language = new LanguageScope(UiLanguage.Chinese);

        Assert.Equal("插话：看这张图（另附 1 个附件）", SessionStrings.ComposerInterjectedNotice("看这张图", [Image]).Resolve());
        Assert.Equal("插话：看看（另附 2 个附件）", SessionStrings.ComposerInterjectedNotice("看看", [Image, Pdf]).Resolve());
    }

    [Fact]
    public void English_uses_the_singular_and_plural_forms_and_follows_a_language_switch()
    {
        using var language = new LanguageScope(UiLanguage.English);
        var notice = SessionStrings.ComposerInterjectedNotice(string.Empty, [Image, Pdf]);

        Assert.Equal("Interjected: 2 attachments (image, report.pdf)", notice.Resolve());
        Assert.Equal("Interjected: 1 attachment (image)", SessionStrings.ComposerInterjectedNotice(string.Empty, [Image]).Resolve());
        Assert.Equal("Interjected: look (+1 attachment)", SessionStrings.ComposerInterjectedNotice("look", [Pdf]).Resolve());
        Assert.Equal("Interjected: look (+2 attachments)", SessionStrings.ComposerInterjectedNotice("look", [Image, Pdf]).Resolve());

        // The notice is resolved when it is rendered: the same instance reads Chinese after a switch, placeholder included.
        Loc.Instance.Language = UiLanguage.Chinese;
        Assert.Equal("插话：2 个附件（图片、report.pdf）", notice.Resolve());
    }

    // ---------------------------------------------------------------- the chip

    [Fact]
    public void Two_attachment_only_chips_are_told_apart_by_their_blocks()
    {
        var other = new ImageBlock("image/png", "BBBB");
        var first = Chip(string.Empty, [Image]);
        var second = Chip(string.Empty, [other]);

        Assert.Same(second, SessionWorkspaceViewModel.FindInterjectionChip([first, second], string.Empty, [other]));
        Assert.Same(first, SessionWorkspaceViewModel.FindInterjectionChip([first, second], string.Empty, [Image]));
    }

    [Fact]
    public void A_mixed_interjection_needs_the_same_text_and_the_same_attachments()
    {
        var plain = Chip("look", []);
        var mixed = Chip("look", [Image, Pdf]);

        Assert.Same(mixed, SessionWorkspaceViewModel.FindInterjectionChip([plain, mixed], "look", [Image, Pdf]));
        Assert.Same(plain, SessionWorkspaceViewModel.FindInterjectionChip([plain, mixed], "look", []));
        Assert.Null(SessionWorkspaceViewModel.FindInterjectionChip([plain, mixed], "other", [Image, Pdf]));
        Assert.Null(SessionWorkspaceViewModel.FindInterjectionChip([plain, mixed], "look", [Image]));
    }

    [Fact]
    public void Without_the_same_instances_the_first_chip_with_the_same_text_and_count_is_taken()
    {
        // Equal by value, not the same instances: a chip carried over from another view.
        var copy = new ImageBlock("image/png", "AAAA");
        var first = Chip(string.Empty, [new ImageBlock("image/png", "CCCC")]);
        var second = Chip(string.Empty, [copy]);

        Assert.Same(first, SessionWorkspaceViewModel.FindInterjectionChip([first, second], "  ", [Image]));
    }

    [Fact]
    public void Queued_messages_that_are_not_interjections_are_never_taken()
    {
        var queued = new QueuedMessageViewModel(string.Empty, isInterjection: false, static _ => { }, [Image]);

        Assert.Null(SessionWorkspaceViewModel.FindInterjectionChip([queued], string.Empty, [Image]));
        Assert.Null(SessionWorkspaceViewModel.FindInterjectionChip([], string.Empty, [Image]));
    }

    private static QueuedMessageViewModel Chip(string text, IReadOnlyList<ContentBlock> attachments) =>
        new(text, isInterjection: true, static _ => { }, attachments);
}
