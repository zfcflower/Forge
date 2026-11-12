using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Forge.Core.Net;

namespace Forge.Core.Tests.Net;

/// <summary>One answer record of a hand-built response: an address (A/AAAA) or a CNAME target.</summary>
internal sealed record TestRecord(string Name, DnsRecordType Type, uint Ttl, string Data);

/// <summary>A parsed incoming query (what the fake servers see).</summary>
internal sealed record TestQuery(ushort Id, string Name, DnsRecordType Type);

/// <summary>Builds DNS messages byte by byte, without the codec under test (names uncompressed unless asked).</summary>
internal static class DnsMessage
{
    public static byte[] Response(TestQuery query, int rcode = 0, bool truncated = false, params TestRecord[] answers) =>
        Response(query.Id, query.Name, query.Type, rcode, truncated, answers);

    public static byte[] Response(ushort id, string name, DnsRecordType type, int rcode = 0, bool truncated = false, params TestRecord[] answers)
    {
        var bytes = new List<byte>();
        AddUInt16(bytes, id);
        var flags = (ushort)(0x8000 | 0x0100 | 0x0080 | (truncated ? 0x0200 : 0) | (rcode & 0xF));
        AddUInt16(bytes, flags);
        AddUInt16(bytes, 1);
        AddUInt16(bytes, (ushort)answers.Length);
        AddUInt16(bytes, 0);
        AddUInt16(bytes, 0);
        AddName(bytes, name);
        AddUInt16(bytes, (ushort)type);
        AddUInt16(bytes, 1);
        foreach (var record in answers)
        {
            AddName(bytes, record.Name);
            AddUInt16(bytes, (ushort)record.Type);
            AddUInt16(bytes, 1);
            AddUInt32(bytes, record.Ttl);
            byte[] data = record.Type switch
            {
                DnsRecordType.A or DnsRecordType.Aaaa => IPAddress.Parse(record.Data).GetAddressBytes(),
                _ => NameBytes(record.Data),
            };
            AddUInt16(bytes, (ushort)data.Length);
            bytes.AddRange(data);
        }

        return [.. bytes];
    }

    public static TestQuery ParseQuery(ReadOnlySpan<byte> message)
    {
        var id = BinaryPrimitives.ReadUInt16BigEndian(message);
        var position = 12;
        var name = new StringBuilder();
        while (message[position] != 0)
        {
            var length = message[position++];
            if (name.Length > 0)
            {
                name.Append('.');
            }

            name.Append(Encoding.ASCII.GetString(message.Slice(position, length)));
            position += length;
        }

        position++;
        var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(message[position..]);
        return new TestQuery(id, name.ToString(), type);
    }

    public static byte[] NameBytes(string name)
    {
        var bytes = new List<byte>();
        AddName(bytes, name);
        return [.. bytes];
    }

    public static void AddName(List<byte> bytes, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
    }

    public static void AddUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    public static void AddUInt32(List<byte> bytes, uint value)
    {
        AddUInt16(bytes, (ushort)(value >> 16));
        AddUInt16(bytes, (ushort)value);
    }
}

/// <summary>
/// A DNS server on loopback answering both UDP and TCP on the same port. The handler gets the query and the transport
/// (true = TCP) and returns the reply, or null to stay silent.
/// </summary>
internal sealed class FakeDnsServer : IDisposable
{
    private readonly Socket udp;
    private readonly TcpListener tcp;
    private readonly CancellationTokenSource stop = new();
    private readonly Func<TestQuery, bool, byte[]?> handler;
    private int udpQueries;
    private int tcpQueries;

    public FakeDnsServer(Func<TestQuery, bool, byte[]?> handler)
    {
        this.handler = handler;
        // Bind TCP first on an ephemeral port, then UDP on the same number (retry on the rare clash).
        for (var attempt = 0; ; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                tcp = listener;
                udp = socket;
                EndPoint = new IPEndPoint(IPAddress.Loopback, port);
                break;
            }
            catch (SocketException) when (attempt < 20)
            {
                socket.Dispose();
                listener.Stop();
            }
        }

        _ = Task.Run(UdpLoopAsync);
        _ = Task.Run(TcpLoopAsync);
    }

    public IPEndPoint EndPoint { get; }

    public int UdpQueries => Volatile.Read(ref udpQueries);

    public int TcpQueries => Volatile.Read(ref tcpQueries);

    /// <summary>Extra datagrams sent before the real reply (stray/forged packets).</summary>
    public Func<TestQuery, IEnumerable<byte[]>>? Noise { get; init; }

    public void Dispose()
    {
        stop.Cancel();
        udp.Dispose();
        tcp.Stop();
        stop.Dispose();
    }

    private async Task UdpLoopAsync()
    {
        var buffer = new byte[4096];
        while (!stop.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (stop.IsCancellationRequested)
                {
                    return;
                }

                continue;
            }

            Interlocked.Increment(ref udpQueries);
            var query = DnsMessage.ParseQuery(buffer.AsSpan(0, received.ReceivedBytes));
            try
            {
                if (Noise is { } noise)
                {
                    foreach (var datagram in noise(query))
                    {
                        await udp.SendToAsync(datagram, SocketFlags.None, received.RemoteEndPoint);
                    }
                }

                if (handler(query, false) is { } reply)
                {
                    await udp.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint);
                }
            }
            catch (Exception exception) when (exception is ObjectDisposedException or SocketException)
            {
            }
        }
    }

    private async Task TcpLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await tcp.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        var prefix = new byte[2];
                        await stream.ReadExactlyAsync(prefix, stop.Token);
                        var message = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
                        await stream.ReadExactlyAsync(message, stop.Token);
                        Interlocked.Increment(ref tcpQueries);
                        if (handler(DnsMessage.ParseQuery(message), true) is { } reply)
                        {
                            var framed = new byte[reply.Length + 2];
                            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)reply.Length);
                            reply.CopyTo(framed, 2);
                            await stream.WriteAsync(framed, stop.Token);
                        }
                        else
                        {
                            await Task.Delay(Timeout.Infinite, stop.Token);
                        }
                    }
                    catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException or SocketException)
                    {
                    }
                }
            });
        }
    }
}

/// <summary>A resolver with a fixed table; unknown hosts fail like NXDOMAIN, and every call is recorded.</summary>
internal sealed class TableResolver(Dictionary<string, IPAddress[]> table) : IDnsResolver
{
    public List<string> Asked { get; } = [];

    public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        lock (Asked)
        {
            Asked.Add(host);
        }

        return table.TryGetValue(host, out var addresses)
            ? ValueTask.FromResult(addresses)
            : ValueTask.FromException<IPAddress[]>(new DnsResolutionException(host, SocketError.HostNotFound, $"no such host '{host}'"));
    }
}
