namespace Forge.Workspace;

/// <summary>
/// Looks up the platform login the tools act as (the author of the replies they post). <c>null</c> means "could not be
/// determined"; the caller then relies on what the ledger learned from the replies this process posted.
/// </summary>
public delegate Task<string?> PrUserResolver(string workingDirectory, CancellationToken ct);

/// <summary>The one resolver there is today: the account the GitHub CLI is logged in with.</summary>
public static class PrCommentUser
{
    /// <summary>
    /// Reads the login with <c>gh api user --jq .login</c> against the host of the repository's origin. Read fresh on every
    /// call – accounts can be switched with <c>gh auth switch</c> – and never throws except for cancellation: any failure is
    /// <c>null</c>.
    /// </summary>
    public static PrUserResolver FromGh(GhCli gh)
    {
        ArgumentNullException.ThrowIfNull(gh);
        return async (workingDirectory, ct) =>
        {
            var host = await gh.GetOriginHostAsync(workingDirectory, ct).ConfigureAwait(false);
            var arguments = new List<string> { "api", "user" };
            if (!string.IsNullOrEmpty(host))
            {
                arguments.Add("--hostname");
                arguments.Add(host);
            }

            arguments.Add("--jq");
            arguments.Add(".login");

            var result = await gh.RunAsync(workingDirectory, arguments, null, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                return null;
            }

            var login = result.StandardOutput.Trim();
            return login.Length == 0 || login.IndexOfAny(['\n', '\r', ' ']) >= 0 ? null : login;
        };
    }
}
