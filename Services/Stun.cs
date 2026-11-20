using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace CraftDeck.Services;

public static class Stun
{
    private static readonly (string Host, int Port)[] Servers =
    [
        ("stun.l.google.com", 19302),
        ("stun1.l.google.com", 19302),
        ("stun.cloudflare.com", 3478),
    ];

    private const int Cookie = 0x2112A442;
    private const int BindingRequest = 0x0001;
    private const int BindingSuccess = 0x0101;
    private const int MappedAddress = 0x0001;
    private const int XorMappedAddress = 0x0020;

    public static async Task<IPEndPoint?> PublicEndpointAsync(UdpClient socket, CancellationToken ct = default)
    {
        foreach (var server in Servers)
        {
            var endpoint = await TryAsync(socket, server.Host, server.Port, ct);
            if (endpoint != null) return endpoint;
            if (ct.IsCancellationRequested) break;
        }
        return null;
    }

    private static async Task<IPEndPoint?> TryAsync(UdpClient socket, string host, int port, CancellationToken ct)
    {
        var transaction = RandomNumberGenerator.GetBytes(12);
        var request = new byte[20];
        Write16(request, 0, BindingRequest);
        Write32(request, 4, Cookie);
        transaction.CopyTo(request, 8);

        IPEndPoint server;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var v4 = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (v4 == null) return null;
            server = new IPEndPoint(v4, port);
        }
        catch
        {
            return null;
        }

        using var step = CancellationTokenSource.CreateLinkedTokenSource(ct);
        step.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await socket.SendAsync(request, server, step.Token);
            var result = await socket.ReceiveAsync(step.Token);
            var buffer = result.Buffer;
            if (buffer.Length < 20 || Read16(buffer, 0) != BindingSuccess) return null;
            if (!new ReadOnlySpan<byte>(buffer, 8, 12).SequenceEqual(transaction)) return null;
            return ParseAddress(buffer);
        }
        catch
        {
            return null;
        }
    }

    private static IPEndPoint? ParseAddress(byte[] buffer)
    {
        IPEndPoint? plain = null;
        var offset = 20;
        while (offset + 4 <= buffer.Length)
        {
            var type = Read16(buffer, offset);
            var length = Read16(buffer, offset + 2);
            var value = offset + 4;
            if (value + length > buffer.Length) break;
            if (type is XorMappedAddress or MappedAddress)
            {
                var endpoint = ReadAddress(buffer, value, type == XorMappedAddress);
                if (endpoint != null)
                {
                    if (type == XorMappedAddress) return endpoint;
                    plain ??= endpoint;
                }
            }
            offset = value + (length + 3) / 4 * 4;
        }
        return plain;
    }

    private static IPEndPoint? ReadAddress(byte[] buffer, int offset, bool xor)
    {
        if (offset + 8 > buffer.Length || buffer[offset + 1] != 1) return null;
        var rawPort = (buffer[offset + 2] << 8) | buffer[offset + 3];
        var port = xor ? rawPort ^ (Cookie >> 16) : rawPort;
        var ip = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            ip[i] = buffer[offset + 4 + i];
            if (xor) ip[i] ^= (byte)(Cookie >> (24 - 8 * i) & 0xFF);
        }
        if (port <= 0 || port > 65535) return null;
        return new IPEndPoint(new IPAddress(ip), port);
    }

    private static int Read16(byte[] buffer, int offset) => (buffer[offset] << 8) | buffer[offset + 1];

    private static void Write16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)value;
    }

    private static void Write32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
