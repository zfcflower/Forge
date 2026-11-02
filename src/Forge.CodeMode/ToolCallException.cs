namespace Forge.CodeMode;

/// <summary>Raised by <see cref="ToolsFacade"/> for a failed, denied or rejected inner tool call; scripts may catch it.</summary>
public sealed class ToolCallException(string toolName, string message, bool denied = false) : Exception(message)
{
    public string ToolName { get; } = toolName;

    public bool Denied { get; } = denied;
}
