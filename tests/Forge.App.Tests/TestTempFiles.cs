namespace Forge.App.Tests;

/// <summary>Temp-file cleanup shared by the suites that build a throw-away directory in a constructor and delete it in Dispose.</summary>
internal static class TestTempFiles
{
    /// <summary>Creates a fresh directory named "<paramref name="prefix"/>&lt;guid&gt;" under the temp path and returns it.</summary>
    public static string NewDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Best-effort recursive delete of a throw-away tree: git object files are read-only on Windows, so every file gets
    /// its attributes reset first and a locked or unreadable leftover must not fail a test in teardown.
    /// </summary>
    public static void DeleteTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        DeleteQuietly(path);
    }

    /// <summary>Writes <paramref name="content"/> to "<paramref name="root"/>/<paramref name="relativePath"/>", creating the parent directories.</summary>
    public static string WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Best-effort recursive delete: a locked or unreadable file left behind must not fail a test in teardown.</summary>
    public static void DeleteQuietly(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
