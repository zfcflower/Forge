using Forge.Ipc;

namespace Forge.ExtensionHost;

/// <summary>Decodes the typed payload of an inbound request. A request without a payload, or with one that is not the
/// operation's documented type, is refused with <c>invalid_request</c> instead of crashing the dispatch.</summary>
internal static class HostArguments
{
    public static T Read<T>(IpcMessage message)
    {
        if (!message.HasPayload)
        {
            throw new HostOperationException("invalid_request", $"'{message.Operation}' requires an arguments object.");
        }

        try
        {
            return message.Read<T>();
        }
        catch (IpcProtocolException exception)
        {
            throw new HostOperationException("invalid_request", $"'{message.Operation}' has unreadable arguments: {exception.Message}");
        }
    }

    /// <summary>Reads a notification payload; an unreadable one is dropped, because a notification has no reply channel.</summary>
    public static T? TryRead<T>(IpcMessage message)
        where T : class
    {
        if (!message.HasPayload)
        {
            return null;
        }

        try
        {
            return message.Read<T>();
        }
        catch (IpcProtocolException)
        {
            return null;
        }
    }
}
