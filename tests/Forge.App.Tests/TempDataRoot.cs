using Forge.App.Composition;

namespace Forge.App.Tests;

/// <summary>The data root a composition-root test runs on: a fresh temp directory with its three well-known children.</summary>
internal static class TempDataRoot
{
    /// <summary>
    /// Creates <c>data</c>, <c>workspace</c> and <c>general</c> (plus any <paramref name="extra"/> child) under a new temp
    /// root, points the three data-root variables at them, and returns the root with the values they had before.
    /// </summary>
    public static (string Root, EnvironmentSnapshot Previous) Redirect(string prefix, params string[] extra)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var workspace = Path.Combine(root, "workspace");
        var general = Path.Combine(root, "general");
        foreach (var directory in new[] { data, workspace, general })
        {
            Directory.CreateDirectory(directory);
        }

        foreach (var name in extra)
        {
            Directory.CreateDirectory(Path.Combine(root, name));
        }

        var previous = EnvironmentSnapshot.Capture();
        Environment.SetEnvironmentVariable(AppPaths.DataVariable, data);
        Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, workspace);
        Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, general);
        return (root, previous);
    }
}
