using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Forge.Scripting.Tests.Support;
using Xunit;

namespace Forge.Scripting.Tests;

public class ScriptCompilerTests
{
    [Fact]
    public void Valid_plugin_compiles_to_a_pe_image_without_diagnostics()
    {
        var result = Build.Direct(PluginSources.Echo);

        Assert.True(result.Success);
        Assert.Null(result.ErrorText);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, result.TotalDiagnosticCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(0, result.WarningCount);
        Assert.Equal("DemoPlugin", result.AssemblyName);
        Assert.NotNull(result.AssemblyBytes);
        Assert.NotEmpty(result.AssemblyBytes);
        Assert.Equal((byte)'M', result.AssemblyBytes[0]);
        Assert.Equal((byte)'Z', result.AssemblyBytes[1]);
        Assert.Matches("^[0-9a-f]{64}$", result.SourceHash);
        Assert.Matches("^[0-9a-f]{64}$", result.CacheKey);
    }

    [Fact]
    public void Image_carries_the_requested_assembly_name_and_an_embedded_portable_pdb()
    {
        var result = Build.Direct(PluginSources.Echo, "Acme.Echo");

        Assert.True(result.Success);
        using var pe = new PEReader(new MemoryStream(result.AssemblyBytes!, writable: false));
        var reader = pe.GetMetadataReader();
        Assert.Equal("Acme.Echo", reader.GetString(reader.GetAssemblyDefinition().Name));
        Assert.Contains(pe.ReadDebugDirectory(), entry => entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
    }

    [Fact]
    public void Broken_plugin_reports_positioned_errors_in_source_order()
    {
        var result = Build.Direct(PluginSources.Broken);
        var (missingLine, missingColumn) = Position.Of(PluginSources.Broken, "Missing");
        var (interfaceLine, interfaceColumn) = Position.Of(PluginSources.Broken, "IPlugin");

        Assert.False(result.Success);
        Assert.Null(result.AssemblyBytes);
        Assert.Equal(2, result.ErrorCount);
        Assert.Equal(2, result.TotalDiagnosticCount);
        Assert.Collection(
            result.Diagnostics,
            first =>
            {
                Assert.Equal("CS0535", first.Code);
                Assert.Equal(ScriptDiagnosticSeverity.Error, first.Severity);
                Assert.Equal(interfaceLine, first.Line);
                Assert.Equal(interfaceColumn, first.Column);
            },
            second =>
            {
                Assert.Equal("CS0103", second.Code);
                Assert.Equal(ScriptDiagnosticSeverity.Error, second.Severity);
                Assert.Equal(missingLine, second.Line);
                Assert.Equal(missingColumn, second.Column);
                Assert.Contains("'Missing'", second.Message);
            });
        Assert.StartsWith("The plugin source does not compile (2 error(s)):", result.ErrorText);
        Assert.Contains($"line {missingLine}, column {missingColumn}: CS0103", result.ErrorText);
    }

    [Fact]
    public void Diagnostics_list_puts_errors_before_earlier_warnings()
    {
        var result = Build.Direct(PluginSources.ErrorAndWarning);

        Assert.False(result.Success);
        Assert.Equal(1, result.ErrorCount);
        Assert.Equal(1, result.WarningCount);
        Assert.Collection(
            result.Diagnostics,
            first =>
            {
                Assert.Equal(ScriptDiagnosticSeverity.Error, first.Severity);
                Assert.Equal("CS0103", first.Code);
            },
            second =>
            {
                Assert.Equal(ScriptDiagnosticSeverity.Warning, second.Severity);
                Assert.Equal("CS0168", second.Code);
                Assert.Equal(7, second.Line);
            });
    }

    [Fact]
    public void Warnings_alone_still_produce_an_image()
    {
        var result = Build.Direct(PluginSources.WarningOnly);

        Assert.True(result.Success);
        Assert.NotNull(result.AssemblyBytes);
        Assert.Null(result.ErrorText);
        Assert.Equal(0, result.ErrorCount);
        Assert.Equal(1, result.WarningCount);
        var warning = Assert.Single(result.Diagnostics);
        Assert.Equal("CS0168", warning.Code);
        Assert.Equal(ScriptCompiler.DefaultFilePath, warning.Path);
    }

    [Fact]
    public void Diagnostic_path_is_the_request_file_path()
    {
        var request = Build.Request(PluginSources.ErrorAndWarning) with { FilePath = "plugins/mixed.cs" };

        var result = new ScriptCompiler().CompileDirect(request);

        Assert.All(result.Diagnostics, diagnostic => Assert.Equal("plugins/mixed.cs", diagnostic.Path));
    }

    [Fact]
    public void Diagnostics_beyond_the_count_limit_are_dropped_and_reported_as_more()
    {
        var result = Build.Direct(PluginSources.ManyErrors(20));

        Assert.False(result.Success);
        Assert.Equal(16, result.Diagnostics.Count);
        Assert.Equal(20, result.TotalDiagnosticCount);
        Assert.Equal(Enumerable.Range(4, 16), result.Diagnostics.Select(diagnostic => diagnostic.Line));
        Assert.Contains("  ... 4 more", result.ErrorText);
    }

    [Fact]
    public void ErrorCount_reports_every_error_when_diagnostics_are_truncated()
    {
        var result = Build.Direct(PluginSources.ManyErrors(20));

        Assert.Equal(20, result.ErrorCount);
    }

    [Fact]
    public void Diagnostic_count_limit_is_configurable()
    {
        var options = new ScriptCompilerOptions { MaxDiagnosticCount = 3 };

        var result = Build.Direct(PluginSources.ManyErrors(20), options: options);

        Assert.Equal(3, result.Diagnostics.Count);
        Assert.Equal(20, result.TotalDiagnosticCount);
        Assert.Contains("  ... 17 more", result.ErrorText);
    }

    [Fact]
    public void Error_text_is_truncated_at_the_character_limit()
    {
        var options = new ScriptCompilerOptions { MaxDiagnosticChars = 200 };

        var result = Build.Direct(PluginSources.ManyErrors(20), options: options);

        Assert.False(result.Success);
        Assert.Contains("[... diagnostics truncated: kept 200 of", result.ErrorText);
        Assert.True(result.ErrorText!.Length < 260);
    }

    [Fact]
    public void Unresolvable_reference_is_reported_as_FORGE0001_without_a_location()
    {
        var request = Build.Request(PluginSources.Echo, references: ["No.Such.Assembly"]);

        var result = new ScriptCompiler().CompileDirect(request);

        Assert.False(result.Success);
        Assert.Null(result.AssemblyBytes);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.MissingReference, diagnostic.Code);
        Assert.Equal(ScriptDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(0, diagnostic.Line);
        Assert.Equal(0, diagnostic.Column);
        Assert.Contains("'No.Such.Assembly'", diagnostic.Message);
    }

    [Fact]
    public void Unknown_using_is_reported_as_FORGE0002()
    {
        var request = new CompilationRequest
        {
            Source = PluginSources.Echo,
            AssemblyName = "DemoPlugin",
            Usings = ["System", "System.Threading", "System.Threading.Tasks", "Forge.Plugin.Sdk", "No.Such.Namespace"],
        };

        var result = new ScriptCompiler().CompileDirect(request);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.UnknownUsing, diagnostic.Code);
        Assert.Equal(0, diagnostic.Line);
    }

    [Fact]
    public void Expected_source_hash_mismatch_fails_before_Roslyn_runs_with_FORGE0004()
    {
        var request = Build.Request(PluginSources.Echo) with { ExpectedSourceHash = new string('a', 64) };

        var result = new ScriptCompiler().CompileDirect(request);

        Assert.False(result.Success);
        Assert.Null(result.AssemblyBytes);
        Assert.Equal(request.SourceHash, result.SourceHash);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.SourceHashMismatch, diagnostic.Code);
        Assert.Equal(ScriptDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(0, diagnostic.Line);
        Assert.Equal(0, diagnostic.Column);
        Assert.Contains("does not match", diagnostic.Message);
        Assert.StartsWith("The plugin source does not compile (1 error(s)):", result.ErrorText);
    }

    [Fact]
    public void Usings_replace_the_default_imports_per_request()
    {
        var withDefaults = new CompilationRequest { Source = PluginSources.UsesListWithoutDirective, AssemblyName = "Lists" };
        var withoutDefaults = withDefaults with { Usings = [] };

        var compiler = new ScriptCompiler();

        Assert.True(compiler.CompileDirect(withDefaults).Success);
        var result = compiler.CompileDirect(withoutDefaults);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CS0246");
    }

    [Fact]
    public void Assembly_name_that_Roslyn_rejects_fails_the_compile_with_a_positioned_error()
    {
        var result = Build.Direct(PluginSources.Echo, "bad:name");

        Assert.False(result.Success);
        Assert.Null(result.AssemblyBytes);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.InvalidAssemblyName, diagnostic.Code);
        Assert.Equal(1, diagnostic.Line);
        Assert.Equal(1, diagnostic.Column);
    }

    [Fact]
    public void Invalid_assembly_name_is_reported_as_FORGE0003()
    {
        var result = Build.Direct(PluginSources.Echo, "bad:name");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == ScriptDiagnosticCodes.InvalidAssemblyName);
    }

    [Fact]
    public void Blank_assembly_name_is_rejected_before_compiling()
    {
        var compiler = new ScriptCompiler();

        Assert.Throws<ArgumentException>(() => compiler.CompileDirect(Build.Request(PluginSources.Echo, "   ")));
    }

    [Fact]
    public void Null_request_is_rejected()
    {
        var compiler = new ScriptCompiler();

        Assert.Throws<ArgumentNullException>(() => compiler.CompileDirect(null!));
        Assert.Throws<ArgumentNullException>(() => compiler.Compile(null!));
    }

    [Fact]
    public void Cancelled_token_stops_compile_before_roslyn_runs()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var compiler = new ScriptCompiler();

        Assert.ThrowsAny<OperationCanceledException>(() => compiler.Compile(Build.Request(PluginSources.Echo), cts.Token));
        Assert.Equal(0, compiler.Cache.Count);
    }

    [Theory]
    [InlineData(nameof(ScriptCompilerOptions.CacheCapacity), -1)]
    [InlineData(nameof(ScriptCompilerOptions.MaxDiagnosticCount), 0)]
    [InlineData(nameof(ScriptCompilerOptions.MaxDiagnosticChars), 0)]
    public void Invalid_options_are_rejected_by_the_compiler(string property, int value)
    {
        var options = property switch
        {
            nameof(ScriptCompilerOptions.CacheCapacity) => new ScriptCompilerOptions { CacheCapacity = value },
            nameof(ScriptCompilerOptions.MaxDiagnosticCount) => new ScriptCompilerOptions { MaxDiagnosticCount = value },
            _ => new ScriptCompilerOptions { MaxDiagnosticChars = value },
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptCompiler(options));
    }

    [Fact]
    public void CompileToStream_writes_the_image_only_on_success()
    {
        var compiler = new ScriptCompiler();
        using var success = new MemoryStream();
        using var failure = new MemoryStream();

        var ok = compiler.CompileToStream(Build.Request(PluginSources.Echo), success);
        var broken = compiler.CompileToStream(Build.Request(PluginSources.Broken), failure);

        Assert.True(ok.Success);
        Assert.Equal(ok.AssemblyBytes, success.ToArray());
        Assert.False(broken.Success);
        Assert.Equal(0, failure.Length);
    }

    [Fact]
    public async Task CompileToStreamAsync_writes_the_image_on_success()
    {
        var compiler = new ScriptCompiler();
        using var destination = new MemoryStream();

        var result = await compiler.CompileToStreamAsync(Build.Request(PluginSources.Echo), destination);

        Assert.True(result.Success);
        Assert.Equal(result.AssemblyBytes, destination.ToArray());
    }
}
