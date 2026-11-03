using System.Security.Cryptography;
using System.Text;
using Forge.Plugin.Sdk;
using Forge.Scripting.Tests.Support;
using Xunit;

namespace Forge.Scripting.Tests;

public class RequestIdentityTests
{
    private const string Hash64 = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public void Source_hash_is_sha256_of_the_normalized_utf8_source()
    {
        const string source = "namespace A;\r\npublic static class B\r\n{\r\n}";
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("namespace A;\npublic static class B\n{\n}")));

        var request = Build.Request(source);
        var result = new ScriptCompiler().CompileDirect(request);

        Assert.Equal(expected, request.ResolvedSourceHash);
        Assert.Equal(expected, result.SourceHash);
    }

    [Theory]
    [InlineData("a\r\nb")]
    [InlineData("a\rb")]
    [InlineData("\uFEFFa\nb")]
    [InlineData("\uFEFFa\r\nb")]
    public void Line_endings_and_byte_order_mark_do_not_change_the_source_hash(string variant)
    {
        var baseline = Build.Request("a\nb").ResolvedSourceHash;

        Assert.Equal(baseline, Build.Request(variant).ResolvedSourceHash);
    }

    [Fact]
    public void Line_ending_variants_of_a_real_plugin_share_source_hash_and_cache_key()
    {
        var lf = PluginSources.Echo.ReplaceLineEndings("\n");
        var crlf = PluginSources.Echo.ReplaceLineEndings("\r\n");
        var cr = PluginSources.Echo.ReplaceLineEndings("\r");

        var results = new[] { lf, crlf, cr }.Select(source => Build.Direct(source)).ToArray();

        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(results[0].SourceHash, results[1].SourceHash);
        Assert.Equal(results[0].SourceHash, results[2].SourceHash);
        Assert.Equal(results[0].CacheKey, results[1].CacheKey);
        Assert.Equal(results[0].CacheKey, results[2].CacheKey);
    }

    [Fact]
    public void Single_trailing_newline_is_ignored_by_the_source_hash()
    {
        Assert.Equal(Build.Request("class A {}").ResolvedSourceHash, Build.Request("class A {}\n").ResolvedSourceHash);
    }

    [Fact]
    public void Stale_expected_source_hash_with_modified_source_does_not_become_the_result_hash()
    {
        var approved = Build.Request(PluginSources.Echo);
        var staleHash = approved.SourceHash;
        var tampered = approved with { Source = PluginSources.Broken, ExpectedSourceHash = staleHash };

        var result = new ScriptCompiler().CompileDirect(tampered);

        Assert.False(result.Success);
        Assert.Null(result.AssemblyBytes);
        Assert.NotEqual(staleHash, result.SourceHash);
        Assert.Equal(tampered.SourceHash, result.SourceHash);
        Assert.Equal(Build.Request(PluginSources.Broken).ResolvedSourceHash, result.SourceHash);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(ScriptDiagnosticCodes.SourceHashMismatch, diagnostic.Code);
        Assert.Contains(staleHash, diagnostic.Message);
    }

    [Fact]
    public void Matching_expected_source_hash_compiles_and_keeps_the_computed_hash()
    {
        var baseline = Build.Request(PluginSources.Echo);
        var request = baseline with { ExpectedSourceHash = baseline.SourceHash };

        var result = new ScriptCompiler().CompileDirect(request);

        Assert.True(result.Success);
        Assert.Equal(baseline.SourceHash, result.SourceHash);
    }

    [Fact]
    public void Expected_source_hash_is_compared_case_insensitively()
    {
        var baseline = Build.Request(PluginSources.Echo);
        var request = baseline with { ExpectedSourceHash = " " + baseline.SourceHash.ToUpperInvariant() + " " };

        var result = new ScriptCompiler().CompileDirect(request);

        Assert.True(result.Success);
        Assert.Equal(baseline.SourceHash, result.SourceHash);
    }

    [Fact]
    public void Blank_expected_source_hash_is_the_same_as_no_expectation()
    {
        var baseline = Build.Request(PluginSources.Echo);
        var blank = baseline with { ExpectedSourceHash = "   " };

        Assert.Equal(baseline.SourceHash, blank.SourceHash);
        Assert.Equal(baseline.CacheKey, blank.CacheKey);
        Assert.True(new ScriptCompiler().CompileDirect(blank).Success);
    }

    [Fact]
    public void Same_request_yields_the_same_hashes_and_byte_identical_images()
    {
        var first = new ScriptCompiler().CompileDirect(Build.Request(PluginSources.Echo));
        var second = new ScriptCompiler().CompileDirect(Build.Request(PluginSources.Echo));

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(first.SourceHash, second.SourceHash);
        Assert.Equal(first.CacheKey, second.CacheKey);
        Assert.Equal(first.AssemblyBytes, second.AssemblyBytes);
    }

    [Fact]
    public void Cache_key_is_stable_across_requests_built_from_the_same_inputs()
    {
        var first = Build.Request(PluginSources.Echo, references: ["System.Net.Http"]) with { Usings = ["System"] };
        var second = Build.Request(PluginSources.Echo, references: ["System.Net.Http"]) with { Usings = ["System"] };

        Assert.Equal(first.CacheKey, second.CacheKey);
    }

    [Theory]
    [InlineData(nameof(CompilationRequest.AssemblyName))]
    [InlineData(nameof(CompilationRequest.ContractVersion))]
    [InlineData(nameof(CompilationRequest.References))]
    [InlineData(nameof(CompilationRequest.Usings))]
    [InlineData(nameof(CompilationRequest.Source))]
    [InlineData(nameof(CompilationRequest.OptimizationLevel))]
    [InlineData(nameof(CompilationRequest.FilePath))]
    [InlineData(nameof(CompilationRequest.ExpectedSourceHash))]
    public void Cache_key_changes_when_an_identity_input_changes(string dimension)
    {
        var baseline = Build.Request(PluginSources.Echo);

        var variant = Vary(baseline, dimension);

        Assert.NotEqual(baseline.CacheKey, variant.CacheKey);
    }

    [Fact]
    public void Cache_key_changes_when_the_reference_set_changes_by_path()
    {
        var sdkPath = typeof(IPlugin).Assembly.Location;
        var baseline = Build.Request(PluginSources.Echo);
        var withPath = Build.Request(PluginSources.Echo, references: [sdkPath]);

        Assert.NotEqual(baseline.CacheKey, withPath.CacheKey);
    }

    [Fact]
    public void Null_contract_version_and_the_compiler_default_share_a_key()
    {
        var implicitVersion = Build.Request(PluginSources.Echo);
        var explicitVersion = implicitVersion with { ContractVersion = ScriptCompiler.ContractVersion };
        var blankVersion = implicitVersion with { ContractVersion = "  " };

        Assert.Equal(ScriptCompiler.ContractVersion, implicitVersion.ResolvedContractVersion);
        Assert.Equal(implicitVersion.CacheKey, explicitVersion.CacheKey);
        Assert.Equal(implicitVersion.CacheKey, blankVersion.CacheKey);
    }

    [Fact]
    public void Contract_version_comes_from_the_plugin_sdk_assembly()
    {
        Assert.Equal(typeof(IPlugin).Assembly.GetName().Version!.ToString(), ScriptCompiler.ContractVersion);
    }

    [Fact]
    public void Reference_order_and_duplicates_do_not_change_the_key()
    {
        var forward = Build.Request(PluginSources.Echo, references: ["System.Net.Http", "System.Xml"]);
        var reversed = Build.Request(PluginSources.Echo, references: ["System.Xml", "System.Net.Http"]);
        var duplicated = Build.Request(PluginSources.Echo, references: ["System.Net.Http", "System.Xml", "System.Net.Http"]);

        Assert.Equal(forward.CacheKey, reversed.CacheKey);
        Assert.Equal(forward.CacheKey, duplicated.CacheKey);
    }

    [Fact]
    public void Null_and_empty_usings_do_not_share_a_cached_result()
    {
        var compiler = new ScriptCompiler();
        var absent = Build.Request(PluginSources.UsesListWithoutDirective, "Lists");
        Assert.True(compiler.Compile(absent).Success);

        Assert.False(compiler.Compile(absent with { Usings = [] }).Success);
    }

    [Fact]
    public void Unresolvable_reference_does_not_reuse_a_cached_success()
    {
        var compiler = new ScriptCompiler();
        var baseline = Build.Request(PluginSources.Echo);
        Assert.True(compiler.Compile(baseline).Success);

        var result = compiler.Compile(baseline with { References = ["No.Such.Assembly"] });

        Assert.False(result.Success);
    }

    [Fact]
    public void Source_hash_always_comes_from_the_source_so_a_modified_source_is_never_served_from_cache()
    {
        var compiler = new ScriptCompiler();
        var approved = Build.Request(PluginSources.Echo);
        var withExpectation = approved with { ExpectedSourceHash = approved.SourceHash };
        Assert.True(compiler.Compile(withExpectation).Success);

        var tampered = withExpectation with { Source = PluginSources.Broken };

        var result = compiler.Compile(tampered);
        Assert.False(result.Success);
        Assert.Equal(tampered.SourceHash, result.SourceHash);
        Assert.NotEqual(withExpectation.SourceHash, result.SourceHash);
    }

    [Fact]
    public void Usings_and_references_with_the_same_concatenation_do_not_collide()
    {
        var sdkPath = typeof(IPlugin).Assembly.Location;
        var viaUsings = Build.Request(PluginSources.Echo) with { Usings = ["System", sdkPath] };
        var viaReference = Build.Request(PluginSources.Echo, references: [sdkPath]) with { Usings = ["System"] };

        Assert.NotEqual(viaUsings.CacheKey, viaReference.CacheKey);
    }

    private static CompilationRequest Vary(CompilationRequest baseline, string dimension) => dimension switch
    {
        nameof(CompilationRequest.AssemblyName) => baseline with { AssemblyName = "OtherPlugin" },
        nameof(CompilationRequest.ContractVersion) => baseline with { ContractVersion = "99.0.0" },
        nameof(CompilationRequest.References) => baseline with { References = ["System.Net.Http"] },
        nameof(CompilationRequest.Usings) => baseline with { Usings = ["System"] },
        nameof(CompilationRequest.Source) => baseline with { Source = baseline.Source + " " },
        nameof(CompilationRequest.OptimizationLevel) => baseline with { OptimizationLevel = ScriptOptimizationLevel.Debug },
        nameof(CompilationRequest.FilePath) => baseline with { FilePath = "other.cs" },
        nameof(CompilationRequest.ExpectedSourceHash) => baseline with { ExpectedSourceHash = Hash64 },
        _ => throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null),
    };
}
