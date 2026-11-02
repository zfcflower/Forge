using System.Text.Json.Serialization;

namespace Forge.Ipc;

/// <summary>IPC protocol constants shared by the AOT main program, the Extension Host and Workers.</summary>
public static class IpcProtocol
{
    public const int Version = 1;

    /// <summary>Maximum JSON frame size in bytes; larger frames are rejected before deserialization.</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;
}

/// <summary>A protocol error carried in a response envelope.</summary>
public sealed record IpcError(string Code, string Message, string? Data = null);

/// <summary>One JSON-RPC-style envelope. A message with <see cref="Id"/> == 0 is a notification.</summary>
public sealed record IpcMessage
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = IpcProtocol.Version;

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("op")]
    public string Operation { get; init; } = "";

    [JsonPropertyName("args")]
    public System.Text.Json.JsonElement? Arguments { get; init; }

    [JsonPropertyName("result")]
    public System.Text.Json.JsonElement? Result { get; init; }

    [JsonPropertyName("error")]
    public IpcError? Error { get; init; }

    public bool IsNotification => Id == 0;
    public bool IsResponse => Error is not null || Result.HasValue;

    public static IpcMessage Request(long id, string operation, System.Text.Json.JsonElement? args = null) =>
        new() { Id = id, Operation = operation, Arguments = args };

    public static IpcMessage Notification(string operation, System.Text.Json.JsonElement? args = null) =>
        new() { Operation = operation, Arguments = args };

    public static IpcMessage ResultMessage(long id, System.Text.Json.JsonElement result) =>
        new() { Id = id, Result = result };

    public static IpcMessage ErrorMessage(long id, IpcError error) =>
        new() { Id = id, Error = error };
}

[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(IpcError))]
public partial class IpcJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
