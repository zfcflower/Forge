using System.Text.Json;
using System.Text.Json.Serialization;
using Forge.Abstractions;

namespace Forge.App.Services;

/// <summary>
/// The composer selection a session was left on: the model, the provider that serves it and the reasoning step. Kept apart
/// from <c>turn_metrics</c> because it is what the user picked, not what a turn measured — a model switched and then closed
/// without sending anything has no turn to be recorded on.
/// </summary>
internal sealed class SessionComposerSelection
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    /// <summary>The step's stored name (<c>off</c>…<c>max</c>); null when the session was left on no known step.</summary>
    [JsonPropertyName("effort")]
    public string? Effort { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SessionComposerSelection))]
internal sealed partial class SessionComposerJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads and writes one session's composer selection in <c>ui_state</c>, one row per session, so reopening a session puts the
/// chip back where the user left it. Best effort by contract: a missing row, a version this build does not know or unparsable
/// JSON is an absent selection (the caller falls back to the last recorded turn), never an exception, and every call reaches
/// the store from a pool thread on the caller's behalf.
/// </summary>
internal sealed class SessionComposerStore
{
    private const string KeyPrefix = "composer:";

    /// <summary>Shape version of the stored document; a different one is ignored rather than misread.</summary>
    private const int Version = 1;

    private readonly IUiStateStore uiState;

    public SessionComposerStore(IUiStateStore uiState)
    {
        ArgumentNullException.ThrowIfNull(uiState);
        this.uiState = uiState;
    }

    public async Task<SessionComposerSelection?> ReadAsync(string sessionId, CancellationToken ct)
    {
        var entry = await uiState.GetAsync(Key(sessionId), ct).ConfigureAwait(false);
        if (entry is null || entry.Version != Version)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(entry.Json, SessionComposerJsonContext.Default.SessionComposerSelection);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task WriteAsync(string sessionId, SessionComposerSelection selection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var json = JsonSerializer.Serialize(selection, SessionComposerJsonContext.Default.SessionComposerSelection);
        return uiState.PutAsync(Key(sessionId), Version, json, ct);
    }

    /// <summary>Drops the session's stored composer selection; part of removing the session for good (7.2).</summary>
    public Task DeleteAsync(string sessionId, CancellationToken ct) => uiState.DeleteAsync(Key(sessionId), ct);

    private static string Key(string sessionId) => KeyPrefix + sessionId;
}
