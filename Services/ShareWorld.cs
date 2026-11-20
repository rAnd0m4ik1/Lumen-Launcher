using System.IO;
using System.Windows.Threading;
using CraftDeck.Models;

namespace CraftDeck.Services;

/// <summary>
/// Keeps one pack's LAN world announced to the peers of a P2P network,
/// so sharing survives the launcher showing another tab or page.
/// </summary>
public static class ShareWorld
{
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private static Instance? _inst;
    private static PeerNetwork? _net;
    private static readonly HashSet<int> _seen = new();
    private static List<int> _ports = new();
    private static int _port;
    private static string _name = "";

    public static Instance? Owner => _inst;
    public static List<int> Ports => _ports;
    public static int Port => _port;
    public static string WorldName => _name;
    public static event Action? Changed;

    public static void Start(Instance inst, PeerNetwork net)
    {
        _inst = inst;
        _net = net;
        _seen.Clear();
        Timer.Tick -= OnTick;
        Timer.Tick += OnTick;
        Timer.Stop();
        Timer.Start();
        OnTick(null, EventArgs.Empty);
    }

    public static void Stop()
    {
        var net = _net;
        _inst = null;
        _net = null;
        _port = 0;
        _name = "";
        _ports = new();
        Timer.Stop();
        net?.AnnounceWorld(0, "");
        Changed?.Invoke();
    }

    /// <summary>Overrides the auto-picked port while the game keeps listening on it.</summary>
    public static void Pick(int port)
    {
        if (_inst == null || port <= 0 || !_ports.Contains(port)) return;
        _port = port;
        Announce();
    }

    private static async void OnTick(object? sender, EventArgs e)
    {
        var inst = _inst;
        var net = _net;
        if (inst == null || net == null) return;
        try
        {
            var session = RunningGames.Of(inst.Config.Id);
            var pid = session != null && session.Running && !session.Proc.HasExited ? session.Proc.Id : 0;
            var scan = await Task.Run(() => (
                pid != 0 ? NetInfo.ListenPorts(pid) : new List<int>(),
                pid != 0 ? InstanceContent.LatestWorld(Path.Combine(inst.GameDir, "saves")) : ""));
            if (!ReferenceEquals(_inst, inst) || !ReferenceEquals(_net, net)) return;

            _ports = scan.Item1;
            var fresh = scan.Item1.FirstOrDefault(p => !_seen.Contains(p));
            _seen.UnionWith(scan.Item1);
            if (!(_port > 0 && scan.Item1.Contains(_port))) _port = fresh != 0 ? fresh : scan.Item1.FirstOrDefault();
            _name = scan.Item2;
            Announce();
        }
        catch
        {
            // A pack folder that vanished mid-tick is not worth a dialog; the next tick retries.
        }
        Changed?.Invoke();
    }

    private static void Announce()
    {
        var net = _net;
        if (net == null) return;
        net.AnnounceWorld(_port, _port > 0 ? _name : "");
    }
}
