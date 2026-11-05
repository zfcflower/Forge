using Forge.Editor.Languages;
using Forge.Editor.TextMate;

namespace Forge.App.Composition;

/// <summary>
/// Editor-layer services: the language table, the lazy language-server session pool and the TextMate grammar registry.
/// Nothing is started here; a language server only launches when a document asks for one.
/// </summary>
internal sealed class EditorServices : IAsyncDisposable
{
    private readonly LanguageSessionPool sessions;
    private bool disposed;

    private EditorServices(
        LanguageRegistry languages,
        LanguageSessionPool sessions,
        GrammarRegistry grammars,
        string? problem)
    {
        Languages = languages;
        this.sessions = sessions;
        Grammars = grammars;
        Problem = problem;
    }

    public LanguageRegistry Languages { get; }

    public GrammarRegistry Grammars { get; }

    /// <summary>Why the language table fell back to the built-in defaults, or null.</summary>
    public string? Problem { get; }

    public int LanguageCount => Languages.Languages.Count;

    public int GrammarCount => Grammars.ScopeNames.Count;

    public LanguageSessionPool Sessions => sessions;

    public static EditorServices Create(AppPaths paths)
    {
        var grammars = new GrammarRegistry();
        try
        {
            grammars.AddUserGrammarDirectories(paths.WorkspaceDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        // LoadAsync reads the user and workspace languages.json; a broken file lands in LoadErrors, not in an exception.
        var languages = LanguageRegistry.LoadAsync(paths.WorkspaceDirectory).AsTask().GetAwaiter().GetResult();
        var problem = languages.LoadErrors.Count == 0 ? null : languages.LoadErrors[0];
        return new EditorServices(languages, new LanguageSessionPool(languages), grammars, problem);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await sessions.DisposeAsync().ConfigureAwait(false);
    }
}
