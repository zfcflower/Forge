using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Forge.CodeMode;

internal sealed class ScriptCompiler
{
    private readonly ScriptOptions scriptOptions;
    private readonly ScriptCache cache;
    private readonly int maxDiagnostics;
    private readonly int maxDiagnosticChars;

    public ScriptCompiler(CodeModeOptions options)
    {
        scriptOptions = ScriptSurface.Create(options);
        cache = new ScriptCache(options.CompilationCacheCapacity);
        maxDiagnostics = options.MaxDiagnosticCount;
        maxDiagnosticChars = options.MaxDiagnosticChars;
    }

    public async ValueTask<CompiledScript> GetAsync(string code, CancellationToken ct)
    {
        var key = Key(code);
        if (cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var compiled = await Task.Run(() => Compile(code, ct), ct).ConfigureAwait(false);
        cache.Set(key, compiled);
        return compiled;
    }

    private CompiledScript Compile(string code, CancellationToken ct)
    {
        var script = CSharpScript.Create<object?>(code, scriptOptions, typeof(ScriptGlobals));
        var diagnostics = script.Compile(ct);

        List<Diagnostic>? errors = null;
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                (errors ??= []).Add(diagnostic);
            }
        }

        if (errors is not null)
        {
            return CompiledScript.Failed(DiagnosticFormat.Errors(
                $"The script does not compile ({errors.Count} error(s)):",
                errors,
                maxDiagnostics,
                maxDiagnosticChars));
        }

        var compilation = script.GetCompilation();
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees.First());
        var violations = ScriptSecurityAnalyzer.Analyze(model, maxDiagnostics, ct);
        if (violations.Count > 0)
        {
            return CompiledScript.Failed(DiagnosticFormat.Violations(
                "The script touches APIs run_code does not allow (no file, network, process, reflection, environment, threading or unsafe access):",
                violations,
                maxDiagnosticChars));
        }

        return CompiledScript.Success(script);
    }

    private static string Key(string code)
    {
        var byteCount = Encoding.UTF8.GetByteCount(code);
        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.UTF8.GetBytes(code.AsSpan(), buffer);
            return Convert.ToHexStringLower(SHA256.HashData(buffer.AsSpan(0, written)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
