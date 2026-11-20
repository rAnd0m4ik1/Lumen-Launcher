using System.Net;
using System.Security.Cryptography;

namespace CraftDeck.Services;

public readonly record struct Invite(IPEndPoint Host, byte[] Key);

public static class NetCode
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int KeyBytes = 6;
    private const int PayloadBytes = 4 + 2 + KeyBytes;
    private const int CodeChars = 20;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    public static string Create(IPEndPoint host, byte[] key)
    {
        var raw = new byte[PayloadBytes];
        host.Address.GetAddressBytes().CopyTo(raw, 0);
        raw[4] = (byte)(host.Port >> 8);
        raw[5] = (byte)host.Port;
        key.AsSpan(0, KeyBytes).CopyTo(raw.AsSpan(6, KeyBytes));
        return Group(Encode(raw));
    }

    public static bool TryParse(string? text, out Invite invite)
    {
        invite = default;
        var cleaned = new string((text ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (cleaned.Length != CodeChars) return false;
        var raw = Decode(cleaned);
        if (raw == null) return false;

        var port = (raw[4] << 8) | raw[5];
        var ip = new IPAddress(raw.AsSpan(0, 4));
        if (port <= 0 || port > 65535 || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any)) return false;
        invite = new Invite(new IPEndPoint(ip, port), raw[6..]);
        return true;
    }

    private static string Encode(byte[] raw)
    {
        var chars = new char[CodeChars];
        var bits = 0;
        var value = 0;
        var index = 0;
        foreach (var b in raw)
        {
            value = value << 8 | b;
            bits += 8;
            while (bits >= 5)
            {
                chars[index++] = Alphabet[value >> (bits - 5) & 31];
                bits -= 5;
                value &= (1 << bits) - 1;
            }
        }
        if (bits > 0) chars[index++] = Alphabet[value << 5 - bits & 31];
        return new string(chars);
    }

    private static byte[]? Decode(string code)
    {
        var raw = new byte[PayloadBytes];
        var bits = 0;
        var value = 0;
        var filled = 0;
        foreach (var c in code)
        {
            var idx = Alphabet.IndexOf(c);
            if (idx < 0) return null;
            value = value << 5 | idx;
            bits += 5;
            while (bits >= 8)
            {
                raw[filled++] = (byte)(value >> (bits - 8));
                bits -= 8;
                value &= (1 << bits) - 1;
            }
        }
        return filled == PayloadBytes ? raw : null;
    }

    private static string Group(string code) => string.Join('-', code.Chunk(5).Select(g => new string(g)));
}
