namespace Forge.Mcp;

public static class McpErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ResourceNotFound = -32002;
}

public class McpException(string message, Exception? inner = null) : Exception(message, inner);

public class McpTransportException(string message, Exception? inner = null) : McpException(message, inner);

public sealed class McpSessionExpiredException(string message, Exception? inner = null) : McpTransportException(message, inner);

/// <summary>
/// A streamable HTTP server answered 401 with an RFC 9728 OAuth challenge and no (refreshable) token was available. The
/// server must be marked as needing sign-in; the interactive flow starts only when the user presses the sign-in action.
/// </summary>
public sealed class McpOAuthRequiredException : McpTransportException
{
    public McpOAuthRequiredException(string message, McpOAuthChallenge? challenge = null, Exception? inner = null)
        : base(message, inner)
    {
        Challenge = challenge;
    }

    public McpOAuthChallenge? Challenge { get; }
}

public sealed class McpConnectionClosedException : McpTransportException
{
    public McpConnectionClosedException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public sealed class McpProtocolException : McpException
{
    public McpProtocolException(string method, int code, string message, string? dataJson = null, Exception? inner = null)
        : base($"{method} failed with JSON-RPC error {code}: {message}", inner)
    {
        Method = method;
        Code = code;
        DataJson = dataJson;
    }

    public string Method { get; }

    public int Code { get; }

    public string? DataJson { get; }

    public bool IsMethodNotFound => Code == McpErrorCodes.MethodNotFound;
}

public sealed class McpTimeoutException : McpException
{
    public McpTimeoutException(string method, TimeSpan timeout)
        : base($"{method} did not complete within {timeout.TotalSeconds:0.###}s.")
    {
        Method = method;
        Timeout = timeout;
    }

    public string Method { get; }

    public TimeSpan Timeout { get; }
}
