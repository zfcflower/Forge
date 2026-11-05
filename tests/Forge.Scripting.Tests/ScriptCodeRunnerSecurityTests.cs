using Forge.Plugin.Sdk;
using Xunit;

namespace Forge.Scripting.Tests;

/// <summary>Capability-guard coverage for <see cref="ScriptCodeRunner"/>: syntax bans, semantic
/// bans on resolved symbols, bypass attempts, and the ordinary BCL surface that keeps working.</summary>
public sealed class ScriptCodeRunnerSecurityTests
{
    private static Task<CodeExecutionResult> Run(string code) =>
        ScriptCodeSupport.ExecuteAsync(
            new ScriptCodeRunner(),
            ScriptCodeSupport.Request(code),
            new RecordingHostFacade(),
            CancellationToken.None);

    private static async Task AssertRefused(string code, string expectedFragment)
    {
        var result = await Run(code);

        Assert.True(result.IsError, "a banned API must fail, got: " + result.Content);
        Assert.False(result.RequiresTermination);
        Assert.Contains("does not allow", result.Content, StringComparison.Ordinal);
        Assert.Contains("line ", result.Content, StringComparison.Ordinal);
        Assert.Contains("column ", result.Content, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#r \"x.dll\"\nreturn 1;", "#r directives are not allowed")]
    [InlineData("#load \"x.csx\"\nreturn 1;", "#load directives are not allowed")]
    [InlineData("unsafe { int x = 1; } return 1;", "unsafe code is not allowed")]
    [InlineData("unsafe { int* p = null; } return 1;", "unsafe code is not allowed")]
    [InlineData("unsafe { int* p = stackalloc int[4]; } return 1;", "unsafe code is not allowed")]
    [InlineData("return typeof(int).Name;", "typeof is not allowed")]
    public async Task Syntax_bans_are_refused_before_compilation(string code, string expectedFragment)
    {
        await AssertRefused(code, expectedFragment);
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText(\"x\"); return 1;", "banned API: System.IO.File.ReadAllText")]
    [InlineData("return System.IO.File.Exists(\"x\");", "banned API: System.IO.File.Exists")]
    [InlineData("System.IO.Directory.GetFiles(\"/\"); return 1;", "banned API: System.IO.Directory.GetFiles")]
    [InlineData("return System.Environment.GetEnvironmentVariable(\"X\");", "banned API: System.Environment.GetEnvironmentVariable")]
    [InlineData("return System.Environment.NewLine;", "banned API: System.Environment.NewLine")]
    [InlineData("return System.AppContext.BaseDirectory;", "banned API: System.AppContext.BaseDirectory")]
    [InlineData("System.GC.Collect(); return 1;", "banned API: System.GC.Collect")]
    [InlineData("return System.Activator.CreateInstance<int>().ToString();", "banned API: System.Activator.CreateInstance")]
    [InlineData("return System.Reflection.Assembly.GetExecutingAssembly().FullName;", "banned API: System.Reflection.Assembly.GetExecutingAssembly")]
    [InlineData("return System.Runtime.InteropServices.Marshal.SizeOf<int>();", "banned API: System.Runtime.InteropServices.Marshal.SizeOf")]
    [InlineData("return System.Runtime.InteropServices.GCHandle.Alloc(new object()).ToString();", "banned API: System.Runtime.InteropServices.GCHandle.Alloc")]
    [InlineData("return new System.Threading.Thread(() => {}).ManagedThreadId;", "banned API: System.Threading.Thread")]
    [InlineData("System.Threading.ThreadPool.QueueUserWorkItem(_ => {}); return 1;", "banned API: System.Threading.ThreadPool.QueueUserWorkItem")]
    [InlineData("var t = new System.Threading.Timer(_ => {}); return 1;", "banned API: System.Threading.Timer")]
    [InlineData("await System.Threading.Tasks.Task.Factory.StartNew(() => 1); return 1;", "banned API: System.Threading.Tasks.TaskFactory.StartNew")]
    [InlineData("return System.Threading.Tasks.TaskScheduler.Default.ToString();", "banned API: System.Threading.Tasks.TaskScheduler")]
    [InlineData("return System.IntPtr.Zero.ToString();", "banned API: System.IntPtr")]
    [InlineData("return 42.GetType().GetMethods().Length;", "banned API: System.Type.GetMethods")]
    [InlineData("return new Forge.Scripting.VersionedOutputStore(\"/tmp\").ToString();", "banned API: Forge.Scripting.VersionedOutputStore")]
    [InlineData("return Forge.Scripting.ManagedImage.ReadNativeBinaryFormat(null).ToString();", "banned API: Forge.Scripting.ManagedImage")]
    [InlineData("return new Forge.Scripting.ScriptCompiler().ToString();", "banned API: Forge.Scripting.ScriptCompiler")]
    [InlineData("return new Forge.Scripting.ScriptCodeRunner().ToString();", "banned API: Forge.Scripting.ScriptCodeRunner")]
    public async Task Banned_symbols_are_refused_with_position(string code, string expectedFragment)
    {
        await AssertRefused(code, expectedFragment);
    }

    [Theory]
    [InlineData("using IO = System.IO;\nreturn IO.File.Exists(\"x\");", "banned API: System.IO.File.Exists")]
    [InlineData("return global::System.IO.File.Exists(\"x\");", "banned API: System.IO.File.Exists")]
    [InlineData("using static System.IO.File;\nreturn Exists(\"x\");", "banned API: System.IO.File.Exists")]
    [InlineData("var f = System.IO.File.Exists; return 1;", "banned API: System.IO.File.Exists")]
    [InlineData("System.Collections.Generic.List<System.IO.FileInfo> x = null; return 1;", "banned API: System.IO.FileInfo")]
    [InlineData("using E = System.Environment;\nreturn E.GetEnvironmentVariable(\"X\");", "banned API: System.Environment.GetEnvironmentVariable")]
    public async Task Alias_and_member_access_do_not_bypass_the_guard(string code, string expectedFragment)
    {
        await AssertRefused(code, expectedFragment);
    }

    [Fact]
    public async Task Plugin_load_context_is_unreachable_because_its_loader_assembly_is_not_referenced()
    {
        // System.Runtime.Loader is not in the script reference set, so naming it fails with CS0012.
        var result = await Run("return new Forge.Scripting.PluginLoadContext(\"probe\").ToString();");

        Assert.True(result.IsError);
        Assert.False(result.RequiresTermination);
        Assert.Contains("does not compile", result.Content, StringComparison.Ordinal);
        Assert.Contains("line 1, column", result.Content, StringComparison.Ordinal);
        Assert.Contains("CS0012", result.Content, StringComparison.Ordinal);
        Assert.Contains("System.Runtime.Loader", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("return System.Diagnostics.Process.GetCurrentProcess().Id;")]
    [InlineData("return new System.Net.Http.HttpClient().ToString();")]
    [InlineData("System.Console.WriteLine(\"x\"); return 1;")]
    public async Task Unreferenced_namespaces_are_rejected_by_the_compiler_with_position(string code)
    {
        var result = await Run(code);

        Assert.True(result.IsError);
        Assert.False(result.RequiresTermination);
        Assert.Contains("does not compile", result.Content, StringComparison.Ordinal);
        Assert.Contains("line 1, column", result.Content, StringComparison.Ordinal);
        Assert.Contains("CS0234", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("return new[]{3,1,2}.Where(x => x > 1).OrderBy(x => x).Sum();", "5")]
    [InlineData("return System.Text.RegularExpressions.Regex.IsMatch(\"abc123\", @\"\\d+\").ToString();", "True")]
    [InlineData("var r = await System.Threading.Tasks.Task.WhenAll(System.Threading.Tasks.Task.FromResult(1), System.Threading.Tasks.Task.FromResult(2)); return r[0] + r[1];", "3")]
    [InlineData("return await System.Threading.Tasks.Task.Run(() => 6);", "6")]
    [InlineData("return 42.GetType().Name;", "Int32")]
    [InlineData("return Math.Max(3, 7);", "7")]
    [InlineData("return Guid.Empty.ToString();", "00000000-0000-0000-0000-000000000000")]
    [InlineData("return new DateTime(2024, 1, 2).Year;", "2024")]
    [InlineData("return \"abc\".ToUpperInvariant();", "ABC")]
    [InlineData("var d = new System.Collections.Generic.Dictionary<string, int> { [\"a\"] = 4 }; return d[\"a\"];", "4")]
    [InlineData("return System.Text.Json.JsonSerializer.Serialize(new { x = 1 });", "{\"x\":1}")]
    public async Task Ordinary_bcl_usage_stays_allowed(string code, string expected)
    {
        var result = await Run(code);

        Assert.False(result.IsError, "ordinary BCL usage must keep working, got: " + result.Content);
        Assert.Equal(expected, result.Content);
    }
}
