using System.Globalization;
using Forge.Plugin.Sdk;
using Forge.ScriptRuntime;

namespace Forge.Scripting;

/// <summary>
/// JIT-only in-process <c>run_code</c> engine: compiles a C# script with <see cref="ScriptCodeCompiler"/>, loads the
/// image from memory and runs it through <see cref="ScriptHarness"/> with <see cref="ScriptGlobals"/>
/// (<c>Tools</c>, <c>Log</c>, <c>Cancellation</c>). Every inner call leaves the process through the supplied
/// <see cref="IHostFacade"/>: this engine resolves no tool itself and never evaluates approval.
/// </summary>
/// <remarks>
/// <para>
/// The instance is reusable and thread-safe, but the caller owns the process lease: the host runs one script in a fresh
/// executor process and never reuses an executed one, so this class is what a single-use executor (or a test) runs once
/// the artifact is loaded. Only compiled images and the fixed compile surface are cached (the cache key covers the whole
/// surface and every option that shapes a cached result); the facade, log and cancellation token are rebuilt per
/// invocation, so no request's capabilities survive into the next.
/// </para>
/// <para>
/// Cancellation is cooperative for the script but never blocking for the caller, and a script that ignores it is
/// abandoned: the result then carries <see cref="CodeExecutionResult.RequiresTermination"/> and the caller must kill the
/// process. Cancellation requested before the script starts (during validation or compilation) throws
/// <see cref="OperationCanceledException"/> without running any script code; cancellation during the run returns a
/// normal error result.
/// </para>
/// </remarks>
public sealed class ScriptCodeRunner : ICodeRunner
{
    private readonly ScriptCodeCompiler compiler = new();

    public async Task<CodeExecutionResult> ExecuteAsync(
        CodeExecutionRequest request,
        IHostFacade host,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);

        var options = request.Options ??
            throw new ArgumentNullException(nameof(request), "The request carries no execution options.");
        ScriptHarness.Validate(new ScriptRunSettings(request.Tools, options, Math.Max(1, request.TimeoutMilliseconds)));
        ArgumentOutOfRangeException.ThrowIfLessThan(request.TimeoutMilliseconds, 1, "request.TimeoutMilliseconds");
        if (request.Context is null)
        {
            throw new ArgumentException("The request must carry the call context assigned by the main program.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return new CodeExecutionResult("run_code needs a non-empty C# script.", IsError: true);
        }

        if (request.Code.Length > options.MaxScriptChars)
        {
            return new CodeExecutionResult(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The script is {request.Code.Length} chars; the limit is {options.MaxScriptChars}."),
                IsError: true);
        }

        // Cancelled before compilation: no compile work, no script code, no facade callback.
        ct.ThrowIfCancellationRequested();

        var compiled = await compiler.CompileAsync(request.Code, options, ct).ConfigureAwait(false);
        if (!compiled.Ok)
        {
            return new CodeExecutionResult(compiled.Error!, IsError: true);
        }

        // Cancelled between compilation and the first line of script code: the script is never started.
        ct.ThrowIfCancellationRequested();

        var entry = LoadEntryPoint(compiled);
        return await ScriptHarness
            .RunAsync(entry, host, new ScriptRunSettings(request.Tools, options, request.TimeoutMilliseconds), ct)
            .ConfigureAwait(false);
    }

    private static ScriptEntryPoint LoadEntryPoint(ScriptCompilation compiled)
    {
        try
        {
            return ScriptCodeLoader.Load(compiled.Image!, compiled.Descriptor!);
        }
        catch (ScriptArtifactException exception)
        {
            throw new InvalidOperationException("The compiled script image could not be loaded: " + exception.Message, exception);
        }
    }
}
