using Forge.App.Providers;
using Forge.App.Services;
using Forge.App.ViewModels;
using Forge.Core.Prompts.Templates;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The product invariant behind session grants (forge-core "授权通信"): only a session mention the user picked in the
/// composer's <c>@</c> picker exists as a <see cref="SessionMention"/>, so a typed <c>@name</c> authorizes nothing.
/// <see cref="ComposerViewModel.TakeMentions"/> is the seam the session tab reads on every send (queue, interjection and
/// redirect too), and what it returns is exactly what becomes a grant.
/// </summary>
public sealed class SessionMentionAuthorizationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "forge-session-mention-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient http = new();
    private readonly List<ComposerViewModel> composers = [];

    public void Dispose()
    {
        foreach (var composer in composers)
        {
            composer.Dispose();
        }

        http.Dispose();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A background model-catalog read may still hold a handle; the tree is only this test's temp folder.
        }
    }

    private ComposerViewModel CreateComposer()
    {
        Directory.CreateDirectory(directory);
        var providers = new ProviderServices(Path.Combine(directory, "settings.json"), directory, http);
        var composer = new ComposerViewModel(
            providers,
            _ => { },
            new ComposerPreferences(Path.Combine(directory, "composer.json")),
            directory,
            fileSource: _ => Task.FromResult<IReadOnlyList<string>>([]),
            skillSource: () => [],
            templateSource: () => PromptTemplateSet.BuiltIn());
        composers.Add(composer);
        return composer;
    }

    [Fact]
    public void A_typed_at_name_authorizes_nothing_while_the_same_text_picked_from_the_list_does()
    {
        var composer = CreateComposer();
        composer.SetSessionMentions([new SessionMentionTarget("child-1", "Worker")]);

        // Typing "@Worker" is what the user does first; the picker opens, and sending without picking a row must carry no mention.
        const string draft = "@Worker inspect the change";
        composer.UpdateSuggestions(draft, "@Worker".Length);
        Assert.True(composer.IsSuggestionOpen);
        Assert.Equal(ComposerTokenKind.Mention, composer.SuggestionKind);
        Assert.Equal(0, composer.PickedMentionCount);
        Assert.Empty(composer.TakeMentions(draft));

        // The same name reached by highlighting the picker's session row and accepting it: the picker rewrites the draft into
        // exactly the text that was typed, yet only this path records the mention a grant is written from.
        composer.UpdateSuggestions(draft, "@Worker".Length);
        var accepted = composer.Accept();
        Assert.True(accepted.HasValue, "the picker was open on the session row, so accepting it must rewrite the draft");
        Assert.Equal(draft, accepted.GetValueOrDefault().Text);
        var mention = Assert.Single(composer.TakeMentions(draft));

        Assert.Equal("child-1", mention.SessionId);
        Assert.Equal("Worker", mention.Name);
        Assert.Equal("@Worker", mention.Token);
    }

    [Fact]
    public void A_mention_token_edited_out_of_the_draft_is_no_longer_taken()
    {
        var composer = CreateComposer();
        composer.SetSessionMentions([new SessionMentionTarget("child-1", "Worker")]);
        composer.UpdateSuggestions("@Wor", "@Wor".Length);
        var accepted = composer.Accept();
        Assert.True(accepted.HasValue, "the picker was open on the session row, so accepting it must rewrite the draft");
        Assert.Equal(1, composer.PickedMentionCount);

        // The text sent is the mention's token or nothing: an edited-away token is no longer a mention, so the pair is not granted.
        Assert.Empty(composer.TakeMentions("@Wor inspect the change"));
    }
}
