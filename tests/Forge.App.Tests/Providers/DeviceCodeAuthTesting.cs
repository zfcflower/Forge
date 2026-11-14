using System.Net;
using System.Text;
using System.Text.Json;

namespace Forge.App.Tests.Providers;

/// <summary>One request a scripted device-code flow made, captured so a test can assert the wire shape.</summary>
internal sealed record RecordedDeviceCodeRequest(
    Uri? Uri,
    string Body,
    IReadOnlyList<string> Accept,
    IReadOnlyDictionary<string, string> Headers,
    string ContentType)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// Answers in order, one queued response per request, and records what was asked: the device flow is a sequence, so a
/// handler that answers everything the same way cannot express "pending, then approved".
/// </summary>
internal sealed class ScriptedDeviceCodeHandler : ScriptedAuthHandler<RecordedDeviceCodeRequest>
{
    public static ScriptedDeviceCodeHandler Answering(HttpStatusCode status, string body)
    {
        var handler = new ScriptedDeviceCodeHandler();
        handler.Then(status, body);
        return handler;
    }

    protected override RecordedDeviceCodeRequest Record(HttpRequestMessage request, string body) =>
        new(
            request.RequestUri,
            body,
            [.. request.Headers.Accept.Select(static accept => accept.MediaType ?? string.Empty)],
            request.Headers.ToDictionary(
                static header => header.Key,
                static header => string.Join(',', header.Value),
                StringComparer.OrdinalIgnoreCase),
            request.Content?.Headers.ContentType?.MediaType ?? string.Empty);

    /// <summary>A ChatGPT poll answer pulled off the event stream is not JSON, so the content type says what the body is.</summary>
    protected override string ContentTypeFor(string body) =>
        body.StartsWith("data:", StringComparison.Ordinal) ? "text/event-stream" : "application/json";
}

/// <summary>
/// A clock for the device-flow tests: its timers fire at once and move it forward by the wait they were asked for, so the poll
/// interval and the refresh backoff cost no real time while the two timestamps <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// compares still advance by exactly that much.
/// </summary>
internal sealed class InstantTime(DateTimeOffset start) : TimeProvider
{
    private long ticks;

    public override DateTimeOffset GetUtcNow() => start + TimeSpan.FromTicks(Interlocked.Read(ref ticks));

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        // The runtime creates the timer for a Task.Delay and expects it to be scheduled from this call: nothing calls Change.
        var timer = new InstantTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock to the moment the timer was scheduled for, then runs its callback on the pool.</summary>
    internal void Fire(TimerCallback callback, object? state, TimeSpan dueTime)
    {
        if (dueTime != Timeout.InfiniteTimeSpan && dueTime > TimeSpan.Zero)
        {
            Interlocked.Add(ref ticks, dueTime.Ticks);
        }

        ThreadPool.QueueUserWorkItem(_ => callback(state));
    }
}

internal sealed class InstantTimer(InstantTime time, TimerCallback callback, object? state) : ITimer
{
    private int disposed;

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        time.Fire(callback, state, dueTime);
        return true;
    }

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A real base64url JWT whose payload carries the OpenAI auth claim; the signature is not verified by the client, so only
/// the payload shape matters. An account id of null leaves the claim out entirely.
/// </summary>
internal static class ChatGptTestTokens
{
    public static string Jwt(string? accountId)
    {
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}""");
        var payload = Base64Url(accountId is null
            ? """{"sub":"user"}"""
            : """{"sub":"user","https://api.openai.com/auth":{"chatgpt_account_id":""" +
              JsonSerializer.Serialize(accountId) + "}}");
        return header + "." + payload + ".not-verified";
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
