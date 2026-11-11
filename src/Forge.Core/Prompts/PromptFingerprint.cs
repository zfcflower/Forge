using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Forge.Abstractions;

namespace Forge.Core.Prompts;

/// <summary>
/// <c>prefix_fingerprint</c>: a hash of everything a provider caches ahead of the messages, the stable system prompt and
/// the tool definitions in the order they are sent. Two sessions with the same fingerprint send the same bytes before
/// their first message, so one can hit the other's prompt cache; a different fingerprint means it will not.
/// </summary>
public static class PromptFingerprint
{
    private const int FingerprintBytes = 16;

    /// <summary>Lower-case hex of the first 16 bytes of a SHA-256 over the length-prefixed UTF-8 of every part.</summary>
    public static string Compute(string systemPrompt, IReadOnlyList<ToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(tools);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, systemPrompt);
        Span<byte> count = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(count, tools.Count);
        hash.AppendData(count);
        for (var index = 0; index < tools.Count; index++)
        {
            var tool = tools[index];
            Append(hash, tool.Name);
            Append(hash, tool.Description);
            Append(hash, tool.InputSchemaJson);
        }

        Span<byte> digest = stackalloc byte[32];
        hash.GetHashAndReset(digest);
        return Convert.ToHexStringLower(digest[..FingerprintBytes]);
    }

    private static void Append(IncrementalHash hash, string? text)
    {
        text ??= string.Empty;
        var length = Encoding.UTF8.GetByteCount(text);
        var rented = ArrayPool<byte>.Shared.Rent(length + 4);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(rented, length);
            Encoding.UTF8.GetBytes(text, rented.AsSpan(4));
            hash.AppendData(rented, 0, length + 4);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
