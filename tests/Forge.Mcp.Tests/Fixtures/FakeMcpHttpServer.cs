using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Forge.Mcp.Tests.Fixtures;

internal sealed record RecordedHttpRequest(string Method, string Body, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

internal sealed record HttpReply(int Status, string Body = "", string ContentType = "application/json", IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// In-process streamable HTTP MCP endpoint on 127.0.0.1 built on <see cref="HttpListener"/>. JSON-RPC answers come
/// from a <see cref="ScriptedServer"/>; the response framing (JSON or SSE), session issuing and expiry, and the GET
/// stream status are switchable per test. <see cref="Override"/> can take over any request.
/// </summary>
internal sealed class FakeMcpHttpServer : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly ConcurrentQueue<RecordedHttpRequest> requests = new();
    private readonly Task acceptLoop;
    private int disposed;

    public FakeMcpHttpServer()
    {
        var port = FreePort();
        Endpoint = new Uri($"http://127.0.0.1:{port}/mcp/");
        listener.Prefixes.Add(Endpoint.AbsoluteUri);
        listener.Start();
        acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Uri Endpoint { get; }

    public ScriptedServer Rpc { get; } = new();

    public string SessionId { get; set; } = "session-1";

    public bool UseSse { get; set; }

    public bool CrLf { get; set; }

    public bool SplitSseData { get; set; }

    public int GetStatus { get; set; } = 405;

    public string GetBody { get; set; } = string.Empty;

    public Func<RecordedHttpRequest, HttpReply?>? Override { get; set; }

    public IReadOnlyList<RecordedHttpRequest> Requests => requests.ToArray();

    public int CountRequests(string httpMethod, string? jsonRpcMethod = null) =>
        requests.Count(request => request.Method == httpMethod &&
                                  (jsonRpcMethod is null || JsonRpcMethodOf(request.Body) == jsonRpcMethod));

    public static string SseEvent(string json, string? id = null, bool splitData = false, bool crlf = false)
    {
        var newline = crlf ? "\r\n" : "\n";
        var builder = new StringBuilder();
        if (id is not null)
        {
            builder.Append("id: ").Append(id).Append(newline);
        }

        if (splitData && json.IndexOf(',') is var comma and > 0)
        {
            builder.Append("data: ").Append(json, 0, comma + 1).Append(newline);
            builder.Append("data: ").Append(json, comma + 1, json.Length - comma - 1).Append(newline);
        }
        else
        {
            builder.Append("data: ").Append(json).Append(newline);
        }

        builder.Append(newline);
        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        listener.Close();
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static string? JsonRpcMethodOf(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("method", out var method)
                ? method.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            string body;
            using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in request.Headers.AllKeys)
            {
                if (key is not null)
                {
                    headers[key] = request.Headers[key] ?? string.Empty;
                }
            }

            var recorded = new RecordedHttpRequest(request.HttpMethod, body, headers);
            requests.Enqueue(recorded);
            var reply = Override?.Invoke(recorded) ?? Default(recorded);
            await WriteAsync(context.Response, reply).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch (Exception)
            {
            }
        }
    }

    private HttpReply Default(RecordedHttpRequest request)
    {
        if (request.Method == "GET")
        {
            return GetStatus == 200
                ? new HttpReply(200, GetBody, "text/event-stream")
                : new HttpReply(GetStatus);
        }

        if (request.Method == "DELETE")
        {
            return new HttpReply(200);
        }

        if (request.Header(McpProtocol.SessionIdHeader) == SessionId && IsExpired)
        {
            return new HttpReply(404, """{"error":"session not found"}""");
        }

        using var document = JsonDocument.Parse(request.Body);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out _))
        {
            return new HttpReply(202);
        }

        var messages = Rpc.Respond(root);
        if (messages.Count == 0)
        {
            return new HttpReply(202);
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (JsonRpcMethodOf(request.Body) == McpProtocol.Initialize)
        {
            headers[McpProtocol.SessionIdHeader] = SessionId;
        }

        if (UseSse)
        {
            var stream = string.Concat(messages.Select(message => SseEvent(message, null, SplitSseData, CrLf)));
            return new HttpReply(200, stream, "text/event-stream", headers);
        }

        return new HttpReply(200, messages[0], "application/json", headers);
    }

    public bool IsExpired { get; set; }

    private static async Task WriteAsync(HttpListenerResponse response, HttpReply reply)
    {
        response.StatusCode = reply.Status;
        response.ContentType = reply.ContentType;
        if (reply.Headers is not null)
        {
            foreach (var (name, value) in reply.Headers)
            {
                response.Headers[name] = value;
            }
        }

        var bytes = Encoding.UTF8.GetBytes(reply.Body);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }
}
