using Forge.Abstractions;

namespace Forge.Core.Providers;

/// <summary>
/// Which attachments each wire format can carry, so the attaching side can refuse a file before a request is built (the
/// adapters themselves fail a request with a block they cannot write instead of dropping it). This is about the protocol
/// shape only; whether a given model accepts a kind is <see cref="ModelInputs"/> from the model catalogue.
/// </summary>
public static class ProviderAttachments
{
    public const string Pdf = "application/pdf";

    /// <summary>The input kind a media type belongs to: images, PDF, audio, video; <see cref="ModelInputs.None"/> otherwise.</summary>
    public static ModelInputs KindOf(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return ModelInputs.None;
        }

        if (string.Equals(mediaType, Pdf, StringComparison.OrdinalIgnoreCase))
        {
            return ModelInputs.Pdf;
        }

        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return ModelInputs.Image;
        }

        if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return ModelInputs.Audio;
        }

        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return ModelInputs.Video;
        }

        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? ModelInputs.Text : ModelInputs.None;
    }

    /// <summary>The kinds the wire format of provider <paramref name="kind"/> (a <see cref="ProviderFactory"/> kind) can carry.</summary>
    public static ModelInputs WireInputs(string? kind) => Normalize(kind) switch
    {
        ProviderFactory.AnthropicKind => AnthropicProvider.WireInputs,
        ProviderFactory.OpenAiResponsesKind => OpenAiResponsesProvider.WireInputs,
        ProviderFactory.OpenAiCompatibleKind => OpenAiCompatibleProvider.WireInputs,
        _ => ModelInputs.None,
    };

    /// <summary>
    /// Whether the wire format of provider <paramref name="kind"/> has a shape for an attachment of
    /// <paramref name="mediaType"/> (a kind it carries may still be limited to some formats, e.g. Chat Completions audio is
    /// wav or mp3 only). An unknown provider kind carries nothing.
    /// </summary>
    public static bool CanCarry(string? kind, string mediaType) => Normalize(kind) switch
    {
        ProviderFactory.AnthropicKind => AnthropicProvider.CanCarry(mediaType),
        ProviderFactory.OpenAiResponsesKind => OpenAiResponsesProvider.CanCarry(mediaType),
        ProviderFactory.OpenAiCompatibleKind => OpenAiCompatibleProvider.CanCarry(mediaType),
        _ => false,
    };

    /// <summary>The same check by protocol, the way a configured provider names its wire format.</summary>
    public static bool CanCarry(ProviderProtocol protocol, string mediaType) => CanCarry(KindOf(protocol), mediaType);

    /// <summary>The <see cref="ProviderFactory"/> kind a protocol is built as (Chat Completions for anything else).</summary>
    public static string KindOf(ProviderProtocol protocol) => protocol switch
    {
        ProviderProtocol.Anthropic => ProviderFactory.AnthropicKind,
        ProviderProtocol.OpenAiResponses => ProviderFactory.OpenAiResponsesKind,
        _ => ProviderFactory.OpenAiCompatibleKind,
    };

    /// <summary>The Chat Completions <c>input_audio</c> format for a media type: <c>wav</c>, <c>mp3</c>, or null.</summary>
    internal static string? AudioFormat(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave" => "wav",
        "audio/mpeg" or "audio/mp3" or "audio/mpeg3" or "audio/x-mpeg-3" => "mp3",
        _ => null,
    };

    internal static bool IsPdf(string mediaType) => string.Equals(mediaType, Pdf, StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>data:</c> URL the OpenAI protocols take for an inline file.</summary>
    internal static string DataUrl(FileBlock file) => string.Concat("data:", file.MediaType, ";base64,", file.Data);

    /// <summary>The file name the OpenAI file parts require; the media type's subtype stands in for a missing name.</summary>
    internal static string FileName(FileBlock file) =>
        string.IsNullOrWhiteSpace(file.Name)
            ? "attachment." + (file.MediaType.IndexOf('/') is var slash and >= 0 ? file.MediaType[(slash + 1)..] : "bin")
            : file.Name;

    internal static ProviderException Unsupported(string providerId, FileBlock file) =>
        new(
            $"{providerId}: this provider's wire format cannot carry a {file.MediaType} attachment" +
            (string.IsNullOrWhiteSpace(file.Name) ? "." : $" ({file.Name})."),
            retryable: false);

    private static string? Normalize(string? kind) => kind?.Trim().ToLowerInvariant();
}
