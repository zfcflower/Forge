namespace Forge.Abstractions;

/// <summary>
/// One model request as it actually went out and came back: who served it, what shape it claimed, what it cost and what the account's
/// meters looked like afterwards.
/// </summary>
/// <remarks>
/// Written for later analysis rather than for reading: the fields are the ones a question like "which cache lifetime paid off for this
/// provider" or "what did the window look like when this turn ran" needs, and nothing is derived from another field so the file can be
/// re-analysed after this build is gone. Prompt text is never recorded.
/// </remarks>
/// <param name="At">When the request was sent (UTC).</param>
/// <param name="SessionId">The session the turn belongs to; null outside a session (a connection test, a model listing).</param>
/// <param name="ProviderId">The provider entry that served it, as configured.</param>
/// <param name="Kind">The protocol the adapter speaks (<c>anthropic</c>, <c>openai-compatible</c>, <c>openai-responses</c>).</param>
/// <param name="Model">The model id that was asked for.</param>
/// <param name="Entrypoint">The <c>cc_entrypoint</c> the request claimed, when it claimed one.</param>
/// <param name="CacheTtl">The prompt-cache lifetime the request asked for, when it asked for one.</param>
/// <param name="Downgraded">Whether this attempt was sent in a reduced shape because the previous one was refused.</param>
/// <param name="Outcome"><c>ok</c> or <c>error</c>.</param>
/// <param name="StatusCode">The HTTP status, when the request reached a server.</param>
/// <param name="Usage">Tokens as the provider reported them; empty when the request failed before any usage.</param>
/// <param name="ElapsedMilliseconds">Time to the last stream event (or to the failure).</param>
/// <param name="RequestId">The server's request id, for correlating with a support ticket.</param>
/// <param name="RateLimit">The account meter the response carried, when it carried one.</param>
public sealed record UsageRecord(
    DateTimeOffset At,
    string? SessionId,
    string ProviderId,
    string Kind,
    string Model,
    string? Entrypoint,
    string? CacheTtl,
    bool Downgraded,
    string Outcome,
    int? StatusCode,
    TokenUsage Usage,
    long ElapsedMilliseconds,
    string? RequestId,
    ProviderRateLimit? RateLimit)
{
    public const string Ok = "ok";

    public const string Error = "error";
}

/// <summary>
/// Where usage records go. Called once per model request, including the ones that failed.
/// </summary>
/// <remarks>
/// Implementations must never throw: a full disk or a locked file is a reason to stop recording, never a reason for a turn to fail.
/// </remarks>
public interface IUsageRecorder
{
    void Record(UsageRecord record);
}
