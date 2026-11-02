using Forge.Abstractions;

namespace Forge.CodeMode;

/// <summary>Composition entry point: one <c>run_code</c> tool wired to the host's tool registry and approval stack.</summary>
public static class CodeModeRegistration
{
    public static CodeModeTool Create(
        IToolRegistry tools,
        IApprovalPolicy policy,
        IApprovalHandler? handler = null,
        CodeModeOptions? options = null) =>
        new(tools, ApprovalGate.FromPolicy(policy, handler), options);

    public static CodeModeTool Create(
        IToolRegistry tools,
        Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>> approval,
        CodeModeOptions? options = null) =>
        new(tools, approval, options);
}
