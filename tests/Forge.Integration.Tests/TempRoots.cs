namespace Forge.Integration.Tests;

/// <summary>Teardown of a caller-owned temp root. A host or worker process that has just exited can still hold a handle
/// on a file inside it, so the delete is retried for a short while instead of failing on the first try.</summary>
internal static class TempRoots
{
    /// <summary>Deletes <paramref name="path"/> recursively, retrying while a process that just exited still holds it.
    /// A directory that survives all the retries is rethrown when <paramref name="throwOnPersistentFailure"/> is set and
    /// left behind otherwise.</summary>
    /// <param name="path">Temp root to remove; a path that is already gone is not an error.</param>
    /// <param name="throwOnPersistentFailure">True to surface a temp root that could not be removed; false to tolerate
    /// it, for a test that only cares about the process it started.</param>
    public static void Delete(string path, bool throwOnPersistentFailure = false)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 49)
                {
                    if (throwOnPersistentFailure)
                    {
                        throw;
                    }

                    return;
                }

                Thread.Sleep(100);
            }
        }
    }
}
