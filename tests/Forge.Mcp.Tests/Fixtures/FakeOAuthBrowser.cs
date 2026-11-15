using System.Collections.Concurrent;

namespace Forge.Mcp.Tests.Fixtures;

/// <summary>
/// Stands in for the user's browser behind <see cref="IMcpOAuthUi"/>: <see cref="OpenBrowser"/> records the authorization
/// URL and runs <see cref="Visitor"/> on a background task. The default visitor follows the URL like a browser, letting the
/// fake authorization server redirect into the loopback listener. A test can substitute a visitor to rewrite the redirect,
/// hit a stray path first, or never come back at all.
/// </summary>
internal sealed class FakeOAuthBrowser(HttpClient http) : IMcpOAuthUi
{
    private readonly ConcurrentQueue<Uri> opened = new();

    /// <summary>Replaces the default "follow the authorization URL" behaviour; receives the transport's client.</summary>
    public Func<Uri, HttpClient, Task>? Visitor { get; set; }

    public IReadOnlyList<Uri> Opened => opened.ToArray();

    public int OpenedCount => opened.Count;

    /// <summary>Set when the browser-side task itself faulted; a sign-in that rejects the callback never sets it.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The browser-side task, so a test can await it after the sign-in returned or threw.</summary>
    public Task Visit { get; private set; } = Task.CompletedTask;

    public void OpenBrowser(Uri authorizationUri)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        opened.Enqueue(authorizationUri);
        Visit = Task.Run(async () =>
        {
            try
            {
                if (Visitor is null)
                {
                    await FollowAsync(authorizationUri).ConfigureAwait(false);
                }
                else
                {
                    await Visitor(authorizationUri, http).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                Failure = exception;
            }
        });
    }

    /// <summary>
    /// Fetches the authorization URL and lets it redirect into the loopback listener. A loopback that has already answered
    /// and gone away can reset the connection, which a real browser would show as a failed tab — not a test failure.
    /// </summary>
    public async Task FollowAsync(Uri authorizationUri)
    {
        try
        {
            using var response = await http.GetAsync(authorizationUri).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
        }
    }
}
