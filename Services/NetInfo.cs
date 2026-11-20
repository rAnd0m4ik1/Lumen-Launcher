using System.Net;
using System.Runtime.InteropServices;

namespace CraftDeck.Services;

public static class NetInfo
{
    private const int AfIp = 2;
    private const int TableOwnerPidAll = 5;
    private const int StateListen = 2;
    private const int RowSize = 24;

    public const int DefaultPort = 25565;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sorted,
        int addressFamily, int tableClass, int reserved);

    public static List<int> ListenPorts(int pid)
    {
        var ports = new List<int>();
        if (pid <= 0) return ports;
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfIp, TableOwnerPidAll, 0);
        if (size <= 0) return ports;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, true, AfIp, TableOwnerPidAll, 0) != 0) return ports;
            var rows = Marshal.ReadInt32(buffer);
            for (var i = 0; i < rows; i++)
            {
                var row = buffer + 4 + i * RowSize;
                if (Marshal.ReadInt32(row) != StateListen) continue;
                if (Marshal.ReadInt32(row + 20) != pid) continue;
                var port = (ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(row + 8));
                if (port > 0 && !ports.Contains(port)) ports.Add(port);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        ports.Sort();
        return ports;
    }

    public static bool TryTarget(string? text, out string host, out int port)
    {
        host = "";
        port = DefaultPort;
        var s = (text ?? "").Trim().TrimEnd('/');
        if (s.StartsWith("minecraft:")) s = s["minecraft:".Length..];
        if (s.Length == 0) return false;

        var lastColon = s.LastIndexOf(':');
        if (lastColon > 0 && s.IndexOf(':') == lastColon &&
            int.TryParse(s[(lastColon + 1)..], out var parsed) && parsed is > 0 and <= 65535)
        {
            host = s[..lastColon];
            port = parsed;
        }
        else
        {
            host = s;
        }
        if (host.Length == 0 || Uri.CheckHostName(host) == UriHostNameType.Unknown) return false;
        return true;
    }
}
