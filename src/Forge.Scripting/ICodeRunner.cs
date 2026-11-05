using Forge.Plugin.Sdk;

namespace Forge.Scripting;

/// <summary>JIT-only script engine; the caller owns the exclusive process lease and termination.</summary>
public interface ICodeRunner
{
    Task<CodeExecutionResult> ExecuteAsync(
        CodeExecutionRequest request,
        IHostFacade host,
        CancellationToken ct);
}
