using System.Diagnostics;
using System.Text;

namespace Forge.Workspace.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-workspace-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose() => DeleteTree(Path);

    public static void DeleteTree(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

internal static class GitProbe
{
    public static bool IsAvailable { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>Runs git directly (not through the code under test) to build fixtures.</summary>
internal static class GitFixture
{
    public static (int ExitCode, string Output, string Error) Run(string directory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-c", "user.name=Forge Test", "-c", "user.email=forge@example.com", "-c", "core.autocrlf=false", "-c", "core.safecrlf=false" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr.Result);
    }

    public static string RunChecked(string directory, params string[] arguments)
    {
        var (exitCode, output, error) = Run(directory, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({exitCode}): {error}");
        }

        return output;
    }
}

/// <summary>A throwaway git repository on the <c>main</c> branch; local identity and autocrlf are pinned.</summary>
internal sealed class TestRepo : IDisposable
{
    private readonly TempDirectory sandbox;
    private readonly bool ownsSandbox;

    public TestRepo()
        : this(new TempDirectory(), ownsSandbox: true)
    {
    }

    public TestRepo(TempDirectory sandbox, string name = "repo")
        : this(sandbox, ownsSandbox: false, name)
    {
    }

    private TestRepo(TempDirectory sandbox, bool ownsSandbox, string name = "repo")
    {
        this.sandbox = sandbox;
        this.ownsSandbox = ownsSandbox;
        Root = System.IO.Path.Combine(sandbox.Path, name);
        Directory.CreateDirectory(Root);
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "--local", "user.name", "Forge Test");
        RunGit("config", "--local", "user.email", "forge@example.com");
        RunGit("config", "--local", "core.autocrlf", "false");
    }

    public string Root { get; }

    public string FullPath(string relative) => System.IO.Path.Combine(Root, relative);

    public void Write(string relative, string content) => WriteBytes(relative, new UTF8Encoding(false).GetBytes(content));

    public void WriteBytes(string relative, byte[] content)
    {
        var path = FullPath(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    public string Commit(string message)
    {
        RunGit("add", "-A");
        RunGit("commit", "-q", "--allow-empty", "-m", message);
        return Head();
    }

    public string Head() => RunGit("rev-parse", "HEAD").Trim();

    public string RunGit(params string[] arguments) => GitFixture.RunChecked(Root, arguments);

    public void Dispose()
    {
        if (ownsSandbox)
        {
            sandbox.Dispose();
        }
    }
}

internal static class SpecFixture
{
    public static string Spec(string id, string type, string? extra = null, string body = "\n# Title\n")
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("id: ").Append(id).Append('\n');
        builder.Append("type: ").Append(type).Append('\n');
        builder.Append("title: Title of ").Append(id).Append('\n');
        if (extra is not null)
        {
            builder.Append(extra);
            if (!extra.EndsWith('\n'))
            {
                builder.Append('\n');
            }
        }

        builder.Append("---\n");
        builder.Append(body);
        return builder.ToString();
    }

    public static void Write(string root, string relative, string content)
    {
        var path = System.IO.Path.Combine(root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }
}

internal static class Internals
{
    private static readonly System.Reflection.Assembly Assembly = typeof(GitCli).Assembly;

    /// <summary>Invokes an internal static method by type and name; the parsers are not public API.</summary>
    public static T InvokeStatic<T>(string typeName, string methodName, params object?[] arguments)
    {
        var type = Assembly.GetType("Forge.Workspace." + typeName, throwOnError: true)!;
        var method = type.GetMethod(methodName, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeName, methodName);
        return (T)method.Invoke(null, arguments)!;
    }
}

internal static class TestPaths
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool Same(string left, string right) =>
        string.Equals(Trimmed(left), Trimmed(right), Comparison);

    private static string Trimmed(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsInside(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
