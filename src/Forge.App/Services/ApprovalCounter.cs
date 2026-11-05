using Forge.Approval;

namespace Forge.App.Services;

/// <summary>Counts the approval records for the status bar without blocking the caller.</summary>
internal interface IApprovalCounter
{
    Task<int?> CountAsync(CancellationToken ct);
}

/// <summary>
/// Counts over a read-only connection to the approvals database on the caller's thread; the App never holds a writable
/// approval store, and a database that cannot be read is reported as unavailable (null) instead of failing the shell.
/// </summary>
internal sealed class ApprovalDatabaseCounter(string databasePath) : IApprovalCounter
{
    public async Task<int?> CountAsync(CancellationToken ct)
    {
        try
        {
            return await ApprovalStore.CountReadOnlyAsync(databasePath, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Microsoft.Data.Sqlite.SqliteException or InvalidDataException
            or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
