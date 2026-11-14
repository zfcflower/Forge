using System.Reflection;

namespace Forge.App.Tests;

/// <summary>
/// Locates the checkout root for the source-scanning tests. The root is stamped into the test assembly at build time
/// (<c>ForgeRepoRoot</c> metadata in the csproj), so the test output directory may live anywhere; walking up from the
/// binaries is only the fallback for an assembly built without that stamp.
/// </summary>
internal static class RepoRoot
{
    private const string Marker = "Forge.slnx";

    public static string? Find()
    {
        var stamped = typeof(RepoRoot).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static a => a.Key == "ForgeRepoRoot")?.Value;
        if (!string.IsNullOrEmpty(stamped) && File.Exists(Path.Combine(stamped, Marker)))
        {
            return Path.GetFullPath(stamped);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, Marker)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
