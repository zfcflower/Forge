using System.Diagnostics;
using Forge.Mcp;

namespace Forge.App.Services;

/// <summary>
/// The browser side of MCP OAuth: opens the authorization URL with the OS default handler. The loopback listener itself
/// lives in <see cref="McpOAuth"/> (BCL); this type only hands the browser a URL, on whatever thread the flow runs on.
/// </summary>
internal sealed class McpOAuthUi : IMcpOAuthUi
{
    public void OpenBrowser(Uri authorizationUri)
    {
        ArgumentNullException.ThrowIfNull(authorizationUri);
        // `UseShellExecute` hands the string to the shell, which also runs local files and UNC paths: a URL that came back from
        // an authorization server's metadata document must therefore be an absolute http/https one before it gets there.
        if (!authorizationUri.IsAbsoluteUri ||
            (!string.Equals(authorizationUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(authorizationUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(
                $"The OAuth authorization URI '{authorizationUri}' is not an absolute http/https URI.",
                nameof(authorizationUri));
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = authorizationUri.AbsoluteUri,
            UseShellExecute = true,
        });
    }
}
