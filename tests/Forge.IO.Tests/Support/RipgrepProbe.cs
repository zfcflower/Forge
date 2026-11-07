using System.Diagnostics;
using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// Finds an installed <c>ripgrep</c> for the managed/ripgrep parity tests. The lookup follows <c>PATH</c> the way a shell
/// would (with <c>PATHEXT</c> on Windows) and then requires <c>rg --version</c> to run and exit zero, so a broken or
/// partial install is treated as "not installed" rather than producing a confusing failure later.
/// </summary>
internal static class RipgrepProbe
{
    /// <summary>The full path of a working <c>rg</c>, or <c>null</c> when none is installed.</summary>
    public static string? Path { get; } = Find();

    private static string? Find()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string[] names = OperatingSystem.IsWindows() ? ["rg.exe", "rg"] : ["rg"];
        foreach (var directory in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                string candidate;
                try
                {
                    // A PATH entry may be quoted or relative; an unusable one is not worth failing the probe over.
                    candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(directory.Trim('"'), name));
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    continue;
                }

                if (File.Exists(candidate) && Runs(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool Runs(string executable)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--version");
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that is reported as skipped when ripgrep is not installed. xunit 2 has no runtime skip
/// API, so the condition is evaluated where the attribute is constructed (test discovery) and recorded as
/// <see cref="FactAttribute.Skip"/>.
/// </summary>
public sealed class RipgrepFactAttribute : FactAttribute
{
    public RipgrepFactAttribute()
    {
        if (RipgrepProbe.Path is null)
        {
            Skip = "ripgrep is not installed (rg --version did not run from PATH)";
        }
    }
}
