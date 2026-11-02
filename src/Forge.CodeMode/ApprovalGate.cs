using Forge.Abstractions;

namespace Forge.CodeMode;

/// <summary>
/// Turns an <see cref="IApprovalPolicy"/> plus an optional <see cref="IApprovalHandler"/> into the per-call hook
/// <see cref="ToolsFacade"/> uses. Read-only calls are whatever the policy says; interactive prompts are
/// serialized so the UI never shows two at once.
/// </summary>
public static class ApprovalGate
{
    public static Func<ApprovalRequest, CancellationToken, ValueTask<ApprovalDecision>> FromPolicy(
        IApprovalPolicy policy,
        IApprovalHandler? handler)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var prompts = new SemaphoreSlim(1, 1);
        return ApproveAsync;

        async ValueTask<ApprovalDecision> ApproveAsync(ApprovalRequest request, CancellationToken ct)
        {
            var mode = policy.Evaluate(request);
            if (mode == ApprovalMode.Allow)
            {
                return ApprovalDecision.Allow;
            }

            if (mode == ApprovalMode.Deny)
            {
                throw new ToolCallException(
                    request.ToolName,
                    $"The approval policy denies '{request.ToolName}' ({request.SideEffect}).",
                    denied: true);
            }

            if (handler is null)
            {
                throw new ToolCallException(
                    request.ToolName,
                    $"'{request.ToolName}' ({request.SideEffect}) needs user approval and no approval handler is registered, so the call was denied. Ask the user to approve it outside the script or pick a read-only tool.",
                    denied: true);
            }

            await prompts.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await handler.RequestAsync(request, ct).ConfigureAwait(false) != ApprovalDecision.Allow)
                {
                    throw new ToolCallException(
                        request.ToolName,
                        $"The user denied the inner call to '{request.ToolName}'.",
                        denied: true);
                }

                return ApprovalDecision.Allow;
            }
            finally
            {
                prompts.Release();
            }
        }
    }
}
