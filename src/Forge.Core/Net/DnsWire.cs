using System.Buffers.Binary;
using System.Net;

namespace Forge.Core.Net;

/// <summary>The record types the resolvers ask for or follow.</summary>
public enum DnsRecordType : ushort
{
    A = 1,
    CName = 5,
    Aaaa = 28,
}

/// <summary>A DNS message that does not follow the wire format (RFC 1035 §4); never a network or server failure.</summary>
public sealed class DnsFormatException(string message) : FormatException(message);

/// <summary>One answer record: an address for A/AAAA, the target name for CNAME, neither for other types.</summary>
public readonly record struct DnsRecord(string Name, ushort Type, uint Ttl, IPAddress? Address, string? CanonicalName);

/// <summary>A decoded response: header bits, the single question (when there is exactly one) and the answer section.</summary>
public sealed record DnsResponse(
    ushort Id,
    bool IsResponse,
    bool Truncated,
    int ResponseCode,
    string? QuestionName,
    ushort QuestionType,
    IReadOnlyList<DnsRecord> Answers);

/// <summary>
/// The minimal DNS wire codec the plain and DoH resolvers share: a one-question recursive query for A or AAAA, and a
/// decoder for the header, question and answer sections (authority and additional records are not read). Names are
/// ASCII (callers pass IDN hosts already punycoded, as <see cref="Uri.IdnHost"/> gives them). Decoding is bounded:
/// every read is range-checked, labels are at most 63 bytes, names at most 255, and a compression pointer must point
/// strictly before every position the name has visited so far, so a pointer loop cannot run forever.
/// </summary>
public static class DnsWire
{
    public const int HeaderLength = 12;
    public const int MaxLabelLength = 63;
    public const int MaxNameLength = 255;
    public const int ResponseCodeNoError = 0;
    public const int ResponseCodeServerFailure = 2;
    public const int ResponseCodeNameError = 3;
    public const int ResponseCodeRefused = 5;

    private const ushort FlagResponse = 0x8000;
    private const ushort FlagTruncated = 0x0200;
    private const ushort FlagRecursionDesired = 0x0100;
    private const ushort ClassInternet = 1;
    private const int MaxCNameHops = 16;

    /// <summary>The wire length of a query for <paramref name="name"/> (validated by <see cref="WriteQuery"/>).</summary>
    public static int QueryLength(string name) => HeaderLength + EncodedNameLength(name) + 4;

    /// <summary>A new buffer holding the query; see <see cref="WriteQuery"/>.</summary>
    public static byte[] EncodeQuery(ushort id, string name, DnsRecordType type)
    {
        var buffer = new byte[QueryLength(name)];
        WriteQuery(buffer, id, name, type);
        return buffer;
    }

    /// <summary>
    /// Writes a recursion-desired query with one question (<paramref name="name"/>, <paramref name="type"/>, class IN).
    /// A trailing dot is ignored. Throws <see cref="ArgumentException"/> for an empty, non-ASCII or over-long name or label.
    /// </summary>
    public static int WriteQuery(Span<byte> destination, ushort id, string name, DnsRecordType type)
    {
        var nameLength = EncodedNameLength(name);
        var total = HeaderLength + nameLength + 4;
        if (destination.Length < total)
        {
            throw new ArgumentException("The destination is too small for the query.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16BigEndian(destination, id);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], FlagRecursionDesired);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], 1);
        destination.Slice(6, 6).Clear();
        var position = HeaderLength;
        var trimmed = TrimDot(name);
        var start = 0;
        for (var i = 0; i <= trimmed.Length; i++)
        {
            if (i == trimmed.Length || trimmed[i] == '.')
            {
                var label = trimmed.AsSpan(start, i - start);
                destination[position++] = (byte)label.Length;
                foreach (var c in label)
                {
                    destination[position++] = (byte)c;
                }

                start = i + 1;
            }
        }

        destination[position++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(destination[position..], (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(destination[(position + 2)..], ClassInternet);
        return total;
    }

    /// <summary>Decodes a message; throws <see cref="DnsFormatException"/> for anything malformed or truncated.</summary>
    public static DnsResponse Decode(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderLength)
        {
            throw new DnsFormatException("The DNS message is shorter than its header.");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(message);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        var questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var position = HeaderLength;

        string? questionName = null;
        ushort questionType = 0;
        for (var i = 0; i < questions; i++)
        {
            var name = ReadName(message, ref position);
            Require(message, position, 4);
            var type = BinaryPrimitives.ReadUInt16BigEndian(message[position..]);
            position += 4;
            if (i == 0)
            {
                questionName = name;
                questionType = type;
            }
        }

        if (questions != 1)
        {
            questionName = null;
            questionType = 0;
        }

        // Each record takes at least 11 bytes, so a forged count cannot make this loop outrun the message.
        var records = new List<DnsRecord>(Math.Min((int)answers, 16));
        for (var i = 0; i < answers; i++)
        {
            var name = ReadName(message, ref position);
            Require(message, position, 10);
            var type = BinaryPrimitives.ReadUInt16BigEndian(message[position..]);
            var recordClass = BinaryPrimitives.ReadUInt16BigEndian(message[(position + 2)..]);
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(message[(position + 4)..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(message[(position + 8)..]);
            position += 10;
            Require(message, position, length);
            var data = message.Slice(position, length);
            IPAddress? address = null;
            string? canonical = null;
            if (recordClass == ClassInternet)
            {
                switch ((DnsRecordType)type)
                {
                    case DnsRecordType.A:
                        address = length == 4 ? new IPAddress(data) : throw new DnsFormatException("An A record does not hold 4 bytes.");
                        break;
                    case DnsRecordType.Aaaa:
                        address = length == 16 ? new IPAddress(data) : throw new DnsFormatException("An AAAA record does not hold 16 bytes.");
                        break;
                    case DnsRecordType.CName:
                        // The target may be compressed against the whole message, so it is read there and must end inside the record.
                        var target = position;
                        canonical = ReadName(message, ref target);
                        if (target > position + length)
                        {
                            throw new DnsFormatException("A CNAME target runs past its record.");
                        }

                        break;
                }
            }

            records.Add(new DnsRecord(name, type, ttl, address, canonical));
            position += length;
        }

        return new DnsResponse(
            id,
            (flags & FlagResponse) != 0,
            (flags & FlagTruncated) != 0,
            flags & 0x000F,
            questionName,
            questionType,
            records);
    }

    /// <summary>
    /// Whether <paramref name="response"/> is the answer to our query: a response with the same id and the same single
    /// question (name compared case-insensitively, without the trailing dot).
    /// </summary>
    public static bool IsAnswerTo(DnsResponse response, ushort id, string name, DnsRecordType type)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.IsResponse
            && response.Id == id
            && response.QuestionType == (ushort)type
            && response.QuestionName is { } question
            && SameName(question, name);
    }

    /// <summary>
    /// The addresses of <paramref name="type"/> for <paramref name="name"/>, following CNAME records in the answer section
    /// (at most 16 hops; a loop ends empty). <paramref name="ttl"/> is the smallest TTL along the chain, 0 when empty.
    /// </summary>
    public static List<IPAddress> Addresses(DnsResponse response, string name, DnsRecordType type, out uint ttl)
    {
        ArgumentNullException.ThrowIfNull(response);
        var result = new List<IPAddress>();
        ttl = uint.MaxValue;
        var current = TrimDot(name);
        for (var hop = 0; hop <= MaxCNameHops; hop++)
        {
            string? next = null;
            foreach (var record in response.Answers)
            {
                if (!SameName(record.Name, current))
                {
                    continue;
                }

                if (record.Type == (ushort)type && record.Address is { } address)
                {
                    result.Add(address);
                    ttl = Math.Min(ttl, record.Ttl);
                }
                else if (record.Type == (ushort)DnsRecordType.CName && record.CanonicalName is { } target && next is null)
                {
                    next = target;
                    ttl = Math.Min(ttl, record.Ttl);
                }
            }

            if (result.Count > 0 || next is null)
            {
                break;
            }

            current = next;
        }

        if (result.Count == 0)
        {
            ttl = 0;
        }

        return result;
    }

    public static bool SameName(string left, string right) =>
        string.Equals(TrimDot(left), TrimDot(right), StringComparison.OrdinalIgnoreCase);

    private static string TrimDot(string name) =>
        name.Length > 1 && name[^1] == '.' ? name[..^1] : name;

    private static int EncodedNameLength(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = TrimDot(name);
        if (trimmed.Length == 0 || trimmed == ".")
        {
            throw new ArgumentException("The host name is empty.", nameof(name));
        }

        var labelLength = 0;
        foreach (var c in trimmed)
        {
            if (c == '.')
            {
                if (labelLength == 0)
                {
                    throw new ArgumentException("The host name has an empty label.", nameof(name));
                }

                labelLength = 0;
                continue;
            }

            if (c > 0x7F || c <= ' ')
            {
                throw new ArgumentException("The host name is not ASCII (pass the punycode form).", nameof(name));
            }

            if (++labelLength > MaxLabelLength)
            {
                throw new ArgumentException("A label of the host name is longer than 63 characters.", nameof(name));
            }
        }

        if (labelLength == 0)
        {
            throw new ArgumentException("The host name has an empty label.", nameof(name));
        }

        // Each label costs its length byte, the dots become length bytes, plus the root byte.
        var encoded = trimmed.Length + 2;
        return encoded <= MaxNameLength
            ? encoded
            : throw new ArgumentException("The host name is longer than 255 bytes on the wire.", nameof(name));
    }

    private static string ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        Span<char> buffer = stackalloc char[MaxNameLength];
        var written = 0;
        var wireLength = 1;
        var position = offset;
        var lowest = offset;
        var jumped = false;
        while (true)
        {
            Require(message, position, 1);
            var length = message[position];
            switch (length & 0xC0)
            {
                case 0xC0:
                    Require(message, position, 2);
                    var pointer = ((length & 0x3F) << 8) | message[position + 1];
                    if (pointer >= lowest)
                    {
                        throw new DnsFormatException("A compression pointer does not point backwards.");
                    }

                    if (!jumped)
                    {
                        offset = position + 2;
                        jumped = true;
                    }

                    lowest = pointer;
                    position = pointer;
                    continue;
                case 0x00:
                    break;
                default:
                    throw new DnsFormatException("A name uses a reserved label type.");
            }

            if (length == 0)
            {
                if (!jumped)
                {
                    offset = position + 1;
                }

                return written == 0 ? "." : new string(buffer[..written]);
            }

            Require(message, position + 1, length);
            wireLength += length + 1;
            if (wireLength > MaxNameLength)
            {
                throw new DnsFormatException("A name is longer than 255 bytes.");
            }

            if (written > 0)
            {
                buffer[written++] = '.';
            }

            foreach (var b in message.Slice(position + 1, length))
            {
                buffer[written++] = (char)b;
            }

            position += length + 1;
        }
    }

    private static void Require(ReadOnlySpan<byte> message, int position, int count)
    {
        if (position < 0 || count < 0 || position > message.Length - count)
        {
            throw new DnsFormatException("The DNS message ends inside a field.");
        }
    }
}
