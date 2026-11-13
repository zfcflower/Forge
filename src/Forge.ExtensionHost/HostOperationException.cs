namespace Forge.ExtensionHost;

/// <summary>A request the host cannot serve. <see cref="Code"/> reaches the caller as the IPC error code.</summary>
internal sealed class HostOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
