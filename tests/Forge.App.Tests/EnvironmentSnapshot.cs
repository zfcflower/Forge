using Forge.App.Composition;

namespace Forge.App.Tests;

/// <summary>The data-root variables the composition root reads, captured so a test leaves no trace.</summary>
internal readonly record struct EnvironmentSnapshot(string DataRoot, string Workspace, string GeneralWorkspace)
{
    public static EnvironmentSnapshot Capture() =>
        new(
            Environment.GetEnvironmentVariable(AppPaths.DataVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(AppPaths.WorkspaceVariable) ?? string.Empty,
            Environment.GetEnvironmentVariable(AppPaths.GeneralVariable) ?? string.Empty);

    public void Restore()
    {
        Environment.SetEnvironmentVariable(AppPaths.DataVariable, DataRoot);
        Environment.SetEnvironmentVariable(AppPaths.WorkspaceVariable, Workspace);
        Environment.SetEnvironmentVariable(AppPaths.GeneralVariable, GeneralWorkspace);
    }
}
