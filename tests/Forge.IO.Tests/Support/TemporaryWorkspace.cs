using System.Text;
using System.Text.Json;
using Forge.Abstractions;
using Xunit;

namespace Forge.IO.Tests;

/// <summary>
/// A temporary workspace root plus the helper calls the path, file and tool tests share: writing text and bytes with
/// forward-slash relative names, and invoking an <see cref="ITool"/> the way the agent loop does (a JSON argument object
/// and a <see cref="ToolContext"/> whose working directory is the workspace root).
/// </summary>
internal sealed class TemporaryWorkspace : IDisposable
{
    private readonly TestDirectory directory = new();

    public string Root => directory.Path;

    public ToolContext Context => new("test-session", Root);

    public string Full(string relativePath) => Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public string Write(string relativePath, string content)
    {
        var path = Full(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public string WriteBytes(string relativePath, byte[] bytes)
    {
        var path = Full(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public string CreateDirectory(string relativePath)
    {
        var path = Full(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    public string[] TemporaryFiles() => directory.TemporaryFiles();

    public async Task<ToolResult> InvokeAsync(ITool tool, string argumentsJson, CancellationToken ct = default) =>
        await tool.InvokeAsync(new ToolCall("call-1", tool.Definition.Name, argumentsJson), Context, ct);

    /// <summary>Invokes <paramref name="tool"/> and fails the test with the tool's own message when it answered an error.</summary>
    public async Task<ToolResult> InvokeSuccessAsync(ITool tool, string argumentsJson, CancellationToken ct = default)
    {
        var result = await InvokeAsync(tool, argumentsJson, ct);
        Assert.False(result.IsError, $"{tool.Definition.Name} {argumentsJson} failed: {result.Content}");
        return result;
    }

    /// <summary>The argument object as a JSON string; avoids hand-quoting every call site.</summary>
    public static string Json(object arguments) => JsonSerializer.Serialize(arguments, JsonSerializerOptions);

    private static readonly JsonSerializerOptions JsonSerializerOptions = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public void Dispose() => directory.Dispose();
}

/// <summary>
/// Directory links for the path escape tests. A symbolic link needs <c>SeCreateSymbolicLinkPrivilege</c> on Windows,
/// which is only present with Developer Mode or elevation, but a junction does not; both are reparse points, so both
/// exercise the same link resolution. The helper reports when neither could be created so a test can say why it did not
/// run instead of failing on a machine that forbids links.
/// </summary>
internal static class DirectoryLinks
{
    /// <summary>Creates a link at <paramref name="linkPath"/> that points at <paramref name="targetPath"/>.</summary>
    /// <returns><c>true</c> when a link exists at <paramref name="linkPath"/> afterwards.</returns>
    public static bool TryCreate(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or NotSupportedException)
        {
        }

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        // mklink /J needs no elevation, and an unprivileged process may create one.
        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } command
            ? command
            : "cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/s");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add($"mklink /J \"{linkPath}\" \"{targetPath}\"");
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);
            return Directory.Exists(linkPath);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates a file link at <paramref name="linkPath"/> pointing at <paramref name="targetPath"/>. There is no junction
    /// equivalent for a file, so on a machine that refuses symbolic links this reports failure and the test says why.
    /// </summary>
    /// <returns><c>true</c> when a link exists at <paramref name="linkPath"/> afterwards.</returns>
    public static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return new FileInfo(linkPath).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
