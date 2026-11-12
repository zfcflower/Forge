using System.Net;
using Forge.Core.Net;
using Xunit;

namespace Forge.Core.Tests.Net;

public sealed class DnsWireTests
{
    [Fact]
    public void Encodes_a_recursive_single_question_query()
    {
        var bytes = DnsWire.EncodeQuery(0xBEEF, "api.example.com.", DnsRecordType.Aaaa);

        byte[] expected =
        [
            0xBE, 0xEF, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            3, (byte)'a', (byte)'p', (byte)'i',
            7, (byte)'e', (byte)'x', (byte)'a', (byte)'m', (byte)'p', (byte)'l', (byte)'e',
            3, (byte)'c', (byte)'o', (byte)'m',
            0,
            0x00, 0x1C, 0x00, 0x01,
        ];
        Assert.Equal(expected, bytes);
        Assert.Equal(expected.Length, DnsWire.QueryLength("api.example.com"));
        Assert.Equal(new TestQuery(0xBEEF, "api.example.com", DnsRecordType.Aaaa), DnsMessage.ParseQuery(bytes));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("a..b")]
    [InlineData("bücher.example")]
    [InlineData(".leading")]
    public void Rejects_invalid_names(string name) =>
        Assert.ThrowsAny<ArgumentException>(() => DnsWire.EncodeQuery(1, name, DnsRecordType.A));

    [Fact]
    public void Rejects_over_long_labels_and_names()
    {
        Assert.ThrowsAny<ArgumentException>(() => DnsWire.EncodeQuery(1, new string('a', 64) + ".com", DnsRecordType.A));
        DnsWire.EncodeQuery(1, new string('a', 63) + ".com", DnsRecordType.A);

        var tooLong = string.Join('.', Enumerable.Repeat(new string('a', 63), 4));
        Assert.ThrowsAny<ArgumentException>(() => DnsWire.EncodeQuery(1, tooLong, DnsRecordType.A));
    }

    [Fact]
    public void WriteQuery_refuses_a_short_destination()
    {
        var buffer = new byte[DnsWire.QueryLength("a.b") - 1];
        Assert.Throws<ArgumentException>(() => DnsWire.WriteQuery(buffer, 1, "a.b", DnsRecordType.A));
    }

    [Fact]
    public void Decodes_a_and_aaaa_answers()
    {
        var a = DnsWire.Decode(DnsMessage.Response(7, "h.test", DnsRecordType.A, answers:
        [
            new TestRecord("h.test", DnsRecordType.A, 300, "192.0.2.1"),
            new TestRecord("h.test", DnsRecordType.A, 60, "192.0.2.2"),
        ]));
        Assert.True(a.IsResponse);
        Assert.False(a.Truncated);
        Assert.Equal(7, a.Id);
        Assert.Equal("h.test", a.QuestionName);
        Assert.Equal((ushort)DnsRecordType.A, a.QuestionType);
        Assert.Equal([IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2")], DnsWire.Addresses(a, "H.TEST.", DnsRecordType.A, out var ttl));
        Assert.Equal(60u, ttl);

        var aaaa = DnsWire.Decode(DnsMessage.Response(8, "h.test", DnsRecordType.Aaaa, answers:
            [new TestRecord("h.test", DnsRecordType.Aaaa, 30, "2001:db8::1")]));
        Assert.Equal([IPAddress.Parse("2001:db8::1")], DnsWire.Addresses(aaaa, "h.test", DnsRecordType.Aaaa, out ttl));
        Assert.Equal(30u, ttl);
        // The other family's records are not mistaken for this one.
        Assert.Empty(DnsWire.Addresses(aaaa, "h.test", DnsRecordType.A, out ttl));
        Assert.Equal(0u, ttl);
    }

    [Fact]
    public void Follows_a_cname_chain_and_keeps_the_smallest_ttl()
    {
        var response = DnsWire.Decode(DnsMessage.Response(1, "www.test", DnsRecordType.A, answers:
        [
            new TestRecord("www.test", DnsRecordType.CName, 500, "edge.test"),
            new TestRecord("edge.test", DnsRecordType.CName, 40, "node.cdn.test"),
            new TestRecord("node.cdn.test", DnsRecordType.A, 120, "198.51.100.7"),
            new TestRecord("unrelated.test", DnsRecordType.A, 1, "198.51.100.8"),
        ]));

        Assert.Equal("edge.test", response.Answers[0].CanonicalName);
        Assert.Equal([IPAddress.Parse("198.51.100.7")], DnsWire.Addresses(response, "www.test", DnsRecordType.A, out var ttl));
        Assert.Equal(40u, ttl);
    }

    [Fact]
    public void A_cname_loop_ends_empty()
    {
        var response = DnsWire.Decode(DnsMessage.Response(1, "a.test", DnsRecordType.A, answers:
        [
            new TestRecord("a.test", DnsRecordType.CName, 5, "b.test"),
            new TestRecord("b.test", DnsRecordType.CName, 5, "a.test"),
        ]));
        Assert.Empty(DnsWire.Addresses(response, "a.test", DnsRecordType.A, out var ttl));
        Assert.Equal(0u, ttl);
    }

    [Fact]
    public void Decodes_compressed_names()
    {
        // Question "example.com" at offset 12; the answer's owner is a pointer to it, and the CNAME target is
        // "www" followed by a pointer to "example.com" (offset 12).
        var bytes = new List<byte>();
        DnsMessage.AddUInt16(bytes, 9);
        DnsMessage.AddUInt16(bytes, 0x8180);
        DnsMessage.AddUInt16(bytes, 1);
        DnsMessage.AddUInt16(bytes, 2);
        DnsMessage.AddUInt16(bytes, 0);
        DnsMessage.AddUInt16(bytes, 0);
        DnsMessage.AddName(bytes, "example.com");
        DnsMessage.AddUInt16(bytes, 1);
        DnsMessage.AddUInt16(bytes, 1);

        // CNAME example.com -> www.example.com
        bytes.AddRange([0xC0, 12]);
        DnsMessage.AddUInt16(bytes, 5);
        DnsMessage.AddUInt16(bytes, 1);
        DnsMessage.AddUInt32(bytes, 100);
        DnsMessage.AddUInt16(bytes, 6);
        var targetOffset = bytes.Count;
        bytes.AddRange([3, (byte)'w', (byte)'w', (byte)'w', 0xC0, 12]);

        // A www.example.com (pointer to the CNAME target) -> 203.0.113.5
        bytes.AddRange([0xC0, (byte)targetOffset]);
        DnsMessage.AddUInt16(bytes, 1);
        DnsMessage.AddUInt16(bytes, 1);
        DnsMessage.AddUInt32(bytes, 50);
        DnsMessage.AddUInt16(bytes, 4);
        bytes.AddRange([203, 0, 113, 5]);

        var response = DnsWire.Decode(bytes.ToArray());
        Assert.Equal("example.com", response.Answers[0].Name);
        Assert.Equal("www.example.com", response.Answers[0].CanonicalName);
        Assert.Equal("www.example.com", response.Answers[1].Name);
        Assert.Equal([IPAddress.Parse("203.0.113.5")], DnsWire.Addresses(response, "example.com", DnsRecordType.A, out var ttl));
        Assert.Equal(50u, ttl);
    }

    [Fact]
    public void Response_codes_and_truncation_are_reported()
    {
        var truncated = DnsWire.Decode(DnsMessage.Response(3, "x.test", DnsRecordType.A, rcode: 0, truncated: true));
        Assert.True(truncated.Truncated);

        var nx = DnsWire.Decode(DnsMessage.Response(3, "x.test", DnsRecordType.A, rcode: DnsWire.ResponseCodeNameError));
        Assert.Equal(DnsWire.ResponseCodeNameError, nx.ResponseCode);
        Assert.Empty(nx.Answers);
    }

    [Fact]
    public void IsAnswerTo_requires_the_same_id_and_question()
    {
        var response = DnsWire.Decode(DnsMessage.Response(42, "Q.Test", DnsRecordType.A));
        Assert.True(DnsWire.IsAnswerTo(response, 42, "q.test.", DnsRecordType.A));
        Assert.False(DnsWire.IsAnswerTo(response, 43, "q.test", DnsRecordType.A));
        Assert.False(DnsWire.IsAnswerTo(response, 42, "other.test", DnsRecordType.A));
        Assert.False(DnsWire.IsAnswerTo(response, 42, "q.test", DnsRecordType.Aaaa));

        // A query echoed back (QR bit clear) is not an answer.
        var echoed = DnsWire.Decode(DnsWire.EncodeQuery(42, "q.test", DnsRecordType.A));
        Assert.False(echoed.IsResponse);
        Assert.False(DnsWire.IsAnswerTo(echoed, 42, "q.test", DnsRecordType.A));
    }

    [Fact]
    public void Every_truncation_of_a_valid_message_is_a_format_error()
    {
        var full = DnsMessage.Response(1, "www.test", DnsRecordType.A, answers:
        [
            new TestRecord("www.test", DnsRecordType.CName, 5, "edge.test"),
            new TestRecord("edge.test", DnsRecordType.A, 5, "192.0.2.9"),
        ]);
        DnsWire.Decode(full);
        for (var length = 0; length < full.Length; length++)
        {
            var cut = full.AsSpan(0, length).ToArray();
            Assert.Throws<DnsFormatException>(() => DnsWire.Decode(cut));
        }
    }

    [Fact]
    public void Wrong_address_lengths_are_format_errors()
    {
        var bytes = DnsMessage.Response(1, "h.test", DnsRecordType.A, answers: [new TestRecord("h.test", DnsRecordType.A, 5, "192.0.2.1")]).ToList();
        // Rewrite the A record's RDLENGTH to 5 and append a byte, so the record is self-consistent but the wrong size.
        bytes[^6] = 0;
        bytes[^5] = 5;
        bytes.Add(0);
        Assert.Throws<DnsFormatException>(() => DnsWire.Decode(bytes.ToArray()));

        var six = DnsMessage.Response(1, "h.test", DnsRecordType.Aaaa, answers: [new TestRecord("h.test", DnsRecordType.A, 5, "192.0.2.1")]).ToList();
        six[^14] = 0;
        six[^13] = 28; // Type AAAA, but only 4 bytes of data.
        Assert.Throws<DnsFormatException>(() => DnsWire.Decode(six.ToArray()));
    }

    [Fact]
    public void A_pointer_loop_is_a_format_error()
    {
        // The question name is a pointer to itself.
        byte[] self = [0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12, 0, 1, 0, 1];
        Assert.Throws<DnsFormatException>(() => DnsWire.Decode(self));

        // Two pointers chasing each other: label "a" then a pointer forward to a pointer back to the start.
        byte[] pair = [0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 1, (byte)'a', 0xC0, 16, 0xC0, 12, 0, 1, 0, 1];
        Assert.Throws<DnsFormatException>(() => DnsWire.Decode(pair));
    }

    [Fact]
    public void Malformed_labels_and_pointers_are_format_errors()
    {
        // A label length with the reserved 01/10 prefix.
        byte[] reserved = [0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0x41, (byte)'a', 0, 0, 1, 0, 1];
        Assert.Throws<DnsFormatException>(() => DnsWire.Decode(reserved));

        // A pointer past the end of the message.
        byte[] pastEnd = [0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 200, 0, 1, 0, 1];
        Assert.Throws<DnsFormatException>(() => DnsWire.Decode(pastEnd));

        // A name longer than 255 bytes built from 63-byte labels.
        var bytes = new List<byte> { 0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0 };
        for (var i = 0; i < 5; i++)
        {
            bytes.Add(63);
            bytes.AddRange(Enumerable.Repeat((byte)'a', 63));
        }

        bytes.AddRange([0, 0, 1, 0, 1]);
        Assert.Throws<DnsFormatException>(() => DnsWire.Decode(bytes.ToArray()));

        // A forged answer count with no records behind it.
        byte[] forged = [0, 1, 0x81, 0x80, 0, 0, 0xFF, 0xFF, 0, 0, 0, 0];
        Assert.Throws<DnsFormatException>(() => DnsWire.Decode(forged));

        Assert.Throws<DnsFormatException>(() => DnsWire.Decode([]));
    }
}
