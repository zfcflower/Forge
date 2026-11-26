using Forge.Plugin.Sdk;
using Forge.Scripting;
using Xunit;

namespace Forge.App.Tests;

/// <summary>
/// The API whitelist a script compiles against. <c>System.IO</c> stays a banned namespace, but a path is also just a string,
/// so the members that only split and join text are allowed while everything that reads the file system or the environment
/// stays out; a <c>using</c> directive names no API by itself, so the ban lands on the type or member a script binds. These
/// three pin the line between "name a file" and "touch it", because a widening that slipped either way would show up here
/// and nowhere else.
/// </summary>
public sealed class RunCodeApiWhitelistTests
{
    private static async Task<ScriptCompilation> CompileAsync(string code)
    {
        var compiler = new ScriptCodeCompiler();
        return await compiler.CompileAsync(code, new CodeExecutionOptions(), CancellationToken.None);
    }

    /// <summary>Text of the failure, with the whole result on a null so the assertion names what actually happened.</summary>
    private static string Error(ScriptCompilation compilation) =>
        Assert.IsType<string>(compilation.Error);

    [Fact]
    public async Task Path_string_arithmetic_compiles_inside_the_banned_namespace()
    {
        var compilation = await CompileAsync(
            """
            using System.IO;
            return Path.GetFileName(Path.Combine("src/Forge.IO", "ReadTool.cs"));
            """);

        Assert.True(compilation.Ok, compilation.Error);
    }

    [Fact]
    public async Task A_path_call_that_reads_the_environment_is_refused()
    {
        var compilation = await CompileAsync(
            """
            using System.IO;
            return Path.GetFullPath("src");
            """);

        Assert.False(compilation.Ok);
        Assert.Contains("banned API: System.IO.Path.GetFullPath", Error(compilation), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_call_is_still_refused()
    {
        var compilation = await CompileAsync(
            """
            using System.IO;
            return File.ReadAllText("src/Forge.IO/Tools/ReadTool.cs");
            """);

        Assert.False(compilation.Ok);
        Assert.Contains("banned API: System.IO.File.ReadAllText", Error(compilation), StringComparison.Ordinal);
    }
}
