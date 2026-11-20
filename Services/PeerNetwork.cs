using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace CraftDeck.Services;

public enum PeerState { Connecting, Online, Down }

public readonly record struct PeerInfo(string Name, IPEndPoint Endpoint, PeerState State,
                                       int WorldPort, string WorldName);

/// <summary>
/// Serverless multiplayer: one UDP socket, an invite code that carries the network key,
/// and a reliable ordered stream per TCP connection between two machines.
/// </summary>
public sealed class PeerNetwork : IAsyncDisposable
{
    private const byte TypeHello = 0;
    private const byte TypeHelloAck = 1;
    private const byte TypeData = 2;
    private const byte TypeClose = 3;
    private const byte TypeOpen = 4;
    private const byte TypeAnnounce = 5;
    private const byte TypeIntro = 6;
    private const byte TypeAck = 7;
    private const uint AckMarker = 0x8000_0000;
    private const int Header = 11;
    private const int Tag = 16;
    private const int HelloSize = 13;
    public const int MaxPayload = 1024;
    private const int Window = 256;
    private static readonly TimeSpan DeadLink = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan HelloAgain = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AckThrottle = TimeSpan.FromMilliseconds(5);

    private readonly UdpClient _socket = new(AddressFamily.InterNetwork);
    private readonly AesGcm _open;
    private readonly byte[] _networkKey;
    private readonly byte[] _sessionKey;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(8);
    private readonly object _gate = new();
    private readonly Dictionary<IPEndPoint, Link> _links = new();
    private readonly Dictionary<(IPEndPoint, int), Pipe> _pipes = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextStream;

    public event Action? Changed;
    public IPEndPoint? PublicEndpoint { get; private set; }
    public string Code { get; private set; } = "";
    public bool IsHost { get; private set; }
    public List<PeerInfo> Peers { get; private set; } = new();
    public int AnnouncedPort { get; private set; }
    public string AnnouncedName { get; private set; } = "";

    private PeerNetwork(byte[] networkKey)
    {
        _networkKey = networkKey;
        _sessionKey = DeriveKey(networkKey);
        _open = new AesGcm(_sessionKey, Tag);
    }

    public static Task<PeerNetwork?> HostAsync(CancellationToken ct = default) => HostAsync(null, ct);

    /// <summary>knownAddress is the loopback/LAN test path: the code advertises that address with this socket's own port.</summary>
    internal static async Task<PeerNetwork?> HostAsync(IPEndPoint? knownAddress, CancellationToken ct = default)
    {
        var net = new PeerNetwork(NetCode.NewKey()) { IsHost = true };
        net._socket.Client.Bind(new IPEndPoint(knownAddress?.Address ?? IPAddress.Any, 0));
        if (knownAddress != null)
        {
            net.PublicEndpoint = new IPEndPoint(knownAddress.Address,
                ((IPEndPoint)net._socket.Client.LocalEndPoint!).Port);
        }
        else
        {
            net.PublicEndpoint = await Stun.PublicEndpointAsync(net._socket, ct);
        }
        if (net.PublicEndpoint == null)
        {
            await net.DisposeAsync();
            return null;
        }
        net.Code = NetCode.Create(net.PublicEndpoint, net._networkKey);
        net.Start();
        return net;
    }

    public static Task<PeerNetwork?> JoinAsync(Invite invite, CancellationToken ct = default) =>
        JoinAsync(invite, TimeSpan.FromSeconds(12), ct);

    internal static async Task<PeerNetwork?> JoinAsync(Invite invite, TimeSpan handshake,
                                                      CancellationToken ct = default)
    {
        var net = new PeerNetwork(invite.Key);
        net._socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var link = net.AddLink(invite.Host, "host");
        net.Start();
        var deadline = DateTime.UtcNow + handshake;
        try
        {
            while (link.State == PeerState.Connecting && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await net.SendHello(link, TypeHello, ct);
                await Task.Delay(250, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        if (link.State != PeerState.Online)
        {
            await net.DisposeAsync();
            return null;
        }
        return net;
    }

    private void Start()
    {
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(TickLoop);
    }

    public void AnnounceWorld(int port, string name)
    {
        if (port == AnnouncedPort && name == AnnouncedName) return;
        AnnouncedPort = port;
        AnnouncedName = name;
        var payload = BuildAnnounce(port, name);
        lock (_gate)
        {
            foreach (var link in _links.Values.Where(l => l.State == PeerState.Online))
                link.Enqueue(0, payload, TypeAnnounce);
        }
        Changed?.Invoke();
    }

    /// <summary>Listens on 127.0.0.1 and pipes that port into the peer's announced LAN world. Returns the local port.</summary>
    public int OpenLocalTunnel(PeerInfo peer)
    {
        if (peer.WorldPort <= 0) return -1;
        Link? link;
        lock (_gate) link = _links.GetValueOrDefault(peer.Endpoint);
        if (link == null || link.State != PeerState.Online) return -1;

        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }
        catch
        {
            listener.Dispose();
            return -1;
        }
        listener.Listen(8);
        var localPort = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var target = peer.WorldPort;
        _ = Task.Run(() => AcceptLoop(listener, link, target));
        return localPort;
    }

    private async Task AcceptLoop(Socket listener, Link link, int targetPort)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(_cts.Token);
                OpenRemote(client, link, targetPort);
            }
        }
        catch
        {
        }
        finally
        {
            listener.Dispose();
        }
    }

    private void OpenRemote(Socket client, Link link, int targetPort)
    {
        client.NoDelay = true;
        var streamId = NextStreamId(link);
        if (streamId <= 0)
        {
            client.Dispose();
            return;
        }
        var pipe = new Pipe(this, link, streamId, client);
        lock (_gate) _pipes[(link.Remote, streamId)] = pipe;
        link.Enqueue(streamId, PortBytes(targetPort), TypeOpen);
        pipe.Connected();
        pipe.Start();
    }

    private void ConnectLocal(IPEndPoint remote, int streamId, int port)
    {
        Link? link;
        lock (_gate)
        {
            if (_pipes.ContainsKey((remote, streamId))) return;
            link = _links.GetValueOrDefault(remote);
        }
        if (link == null || link.State != PeerState.Online) return;
        if (port <= 0 || port > 65535) return;

        var local = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var pipe = new Pipe(this, link, streamId, local);
        // Registered before the connect finishes: the peer already treats this stream as open and
        // keeps sending, and those bytes need somewhere to land while the world is being reached.
        lock (_gate) _pipes[(remote, streamId)] = pipe;
        pipe.Start();
        _ = pipe.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
    }

    public void DropPeer(PeerInfo peer)
    {
        lock (_gate)
        {
            if (!_links.TryGetValue(peer.Endpoint, out var link)) return;
            _links.Remove(peer.Endpoint);
            link.Kill();
        }
        RebuildPeers();
    }

    private Link AddLink(IPEndPoint remote, string name)
    {
        lock (_gate)
        {
            if (_links.TryGetValue(remote, out var existing)) return existing;
            var link = new Link(this, remote, name);
            _links[remote] = link;
            RebuildPeers();
            return link;
        }
    }

    private int NextStreamId(Link link)
    {
        lock (_gate)
        {
            for (var i = 0; i < 60000; i++)
            {
                var id = (ushort)Interlocked.Increment(ref _nextStream);
                if (id != 0 && !_pipes.ContainsKey((link.Remote, id))) return id;
            }
            return -1;
        }
    }

    private void ClosePipe(IPEndPoint remote, int streamId)
    {
        Pipe? pipe;
        lock (_gate)
        {
            if (!_pipes.TryGetValue((remote, streamId), out pipe)) return;
            _pipes.Remove((remote, streamId));
        }
        pipe.Close(false);
    }

    private void WriteToLocal(IPEndPoint remote, int streamId, byte[] body)
    {
        Pipe? pipe;
        lock (_gate) pipe = _pipes.GetValueOrDefault((remote, streamId));
        if (body.Length == 0 || pipe == null) return;
        if (!pipe.Deliver(body)) pipe.Close(true);
    }

    private void RebuildPeers()
    {
        List<PeerInfo> peers;
        lock (_gate)
        {
            peers = _links.Select(kv => new PeerInfo(kv.Value.Name, kv.Key, kv.Value.State,
                kv.Value.WorldPort, kv.Value.WorldName)).ToList();
        }
        Peers = peers;
        Changed?.Invoke();
    }

    internal void OnPeerWorld(Link link, int port, string name)
    {
        link.WorldPort = port;
        link.WorldName = name;
        RebuildPeers();
    }

    internal void OnLinkUp(Link link)
    {
        if (AnnouncedPort > 0) link.Enqueue(0, BuildAnnounce(AnnouncedPort, AnnouncedName), TypeAnnounce);
        if (!IsHost) return;
        byte[] intro;
        lock (_gate)
        {
            var others = _links.Values.Where(l => !ReferenceEquals(l, link) && l.State == PeerState.Online).ToList();
            intro = BuildIntro(others.Select(l => l.Remote));
            foreach (var other in others) other.Enqueue(0, intro, TypeIntro);
        }
        if (intro.Length > 1) link.Enqueue(0, intro, TypeIntro);
    }

    private void ApplyIntro(Link from, byte[] body)
    {
        foreach (var endpoint in ReadIntro(body))
        {
            if (endpoint.Equals(from.Remote)) continue;
            if (PublicEndpoint != null && endpoint.Equals(PublicEndpoint)) continue;
            AddLink(endpoint, "peer");
        }
    }

    internal void OnLinkDown(Link link)
    {
        lock (_gate)
        {
            _links.Remove(link.Remote);
            var stale = _pipes.Keys.Where(k => k.Item1.Equals(link.Remote)).ToList();
            foreach (var key in stale)
            {
                var pipe = _pipes[key];
                _pipes.Remove(key);
                pipe.Close(false);
            }
        }
        RebuildPeers();
    }

    internal bool IsClosed => _cts.IsCancellationRequested;
    internal CancellationToken Token => _cts.Token;

    private async Task SendHello(Link link, byte type, CancellationToken ct)
    {
        var packet = new byte[HelloSize];
        packet[0] = type;
        _salt.CopyTo(packet, 1);
        Hmac(_salt).AsSpan(0, 4).CopyTo(packet.AsSpan(9, 4));
        link.LastHello = DateTime.UtcNow;
        try { await _socket.SendAsync(packet, link.Remote); }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }

    private byte[] Hmac(byte[] salt) => HMACSHA256.HashData(_sessionKey, salt);

    private static byte[] DeriveKey(byte[] networkKey) =>
        SHA256.HashData(Encoding.ASCII.GetBytes("lumen-p2p-v1:")
            .Concat(networkKey).ToArray());

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset
                or SocketError.ConnectionAborted or SocketError.Interrupted)
            {
                // An ICMP answer for a peer that is simply not there yet; keep listening.
                continue;
            }
            catch (SocketException)
            {
                return;
            }
            Handle(result.Buffer, result.RemoteEndPoint);
        }
    }

    private List<Link> SnapshotLinks()
    {
        lock (_gate) return _links.Values.ToList();
    }

    private async Task TickLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(20, _cts.Token);
            }
            catch
            {
                return;
            }
            foreach (var link in SnapshotLinks()) link.Tick();
        }
    }

    private void Handle(byte[] buffer, IPEndPoint remote)
    {
        if (buffer.Length < 1) return;
        if (buffer[0] is TypeHello or TypeHelloAck)
        {
            if (buffer.Length != HelloSize) return;
            var salt = buffer.AsSpan(1, 8).ToArray();
            if (!Hmac(salt).AsSpan(0, 4).SequenceEqual(buffer.AsSpan(9, 4))) return;
            Link link;
            lock (_gate)
            {
                link = FindOrCreateLink(remote);
            }
            link.PeerSalt = salt;
            var cameUp = link.State != PeerState.Online;
            link.State = PeerState.Online;
            link.Seen = DateTime.UtcNow;
            if (buffer[0] == TypeHello) _ = SendHello(link, TypeHelloAck, _cts.Token);
            if (cameUp)
            {
                RebuildPeers();
                OnLinkUp(link);
            }
            return;
        }

        if (buffer.Length < Header + Tag) return;
        Link target;
        lock (_gate)
        {
            target = _links.GetValueOrDefault(remote)!;
            if (target?.PeerSalt == null) return;
        }

        var stream = (buffer[1] << 8) | buffer[2];
        var seq = Read32(buffer, 3);
        var ack = Read32(buffer, 7);
        var nonce = new byte[12];
        target.PeerSalt!.CopyTo(nonce, 0);
        Write32(nonce, 8, seq);
        var plain = new byte[buffer.Length - Header - Tag];
        try
        {
            _open.Decrypt(nonce, buffer.AsSpan(Header, buffer.Length - Header - Tag),
                buffer.AsSpan(buffer.Length - Tag, Tag), plain, buffer.AsSpan(0, Header));
        }
        catch (CryptographicException)
        {
            return;
        }
        target.Receive(buffer[0], stream, seq, ack, plain);
    }

    private Link FindOrCreateLink(IPEndPoint remote) =>
        _links.GetValueOrDefault(remote) ?? AddLink(remote, "peer");

    private static byte[] PortBytes(int port) => [(byte)(port >> 8), (byte)port];

    private static uint Read32(byte[] b, int o) =>
        (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

    private static void Write32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static void Write16(byte[] b, int o, int v)
    {
        b[o] = (byte)(v >> 8);
        b[o + 1] = (byte)v;
    }

    private static byte[] BuildAnnounce(int port, string name)
    {
        var label = Encoding.UTF8.GetBytes(name);
        var body = new byte[4 + label.Length];
        Write16(body, 0, port);
        Write16(body, 2, label.Length);
        label.CopyTo(body, 4);
        return body;
    }

    internal static (int port, string name) ReadAnnounce(ReadOnlySpan<byte> plain)
    {
        if (plain.Length < 4) return (0, "");
        var port = plain[0] << 8 | plain[1];
        var len = plain[2] << 8 | plain[3];
        if (len < 0 || plain.Length < 4 + len) return (0, "");
        return (port, Encoding.UTF8.GetString(plain.Slice(4, len)));
    }

    internal static int ReadPort(ReadOnlySpan<byte> plain) => plain.Length < 2 ? 0 : plain[0] << 8 | plain[1];

    private static byte[] BuildIntro(IEnumerable<IPEndPoint> peers)
    {
        var list = peers.Where(p => p.AddressFamily == AddressFamily.InterNetwork).Take(16).ToList();
        var body = new byte[1 + list.Count * 6];
        body[0] = (byte)list.Count;
        for (var i = 0; i < list.Count; i++)
        {
            list[i].Address.GetAddressBytes().CopyTo(body, 1 + i * 6);
            Write16(body, 5 + i * 6, list[i].Port);
        }
        return body;
    }

    private static List<IPEndPoint> ReadIntro(ReadOnlySpan<byte> body)
    {
        var peers = new List<IPEndPoint>();
        if (body.Length < 1) return peers;
        var count = Math.Min(body[0], (body.Length - 1) / 6);
        for (var i = 0; i < count; i++)
        {
            var port = (ushort)(body[5 + i * 6] << 8 | body[6 + i * 6]);
            if (port == 0) continue;
            peers.Add(new IPEndPoint(new IPAddress(body.Slice(1 + i * 6, 4)), port));
        }
        return peers;
    }

    public ValueTask DisposeAsync()
    {
        if (!_cts.IsCancellationRequested) _cts.Cancel();
        Pipe[] pipes;
        Link[] links;
        lock (_gate)
        {
            pipes = _pipes.Values.ToArray();
            links = _links.Values.ToArray();
        }
        foreach (var pipe in pipes) pipe.Close(false);
        foreach (var link in links) link.Kill();
        try { _socket.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }

    internal sealed class Link
    {
        private readonly PeerNetwork _net;
        private readonly Dictionary<uint, Pending> _waiting = new();
        private readonly SortedDictionary<uint, (byte Type, int Stream, byte[] Body)> _gaps = new();
        private readonly object _lock = new();
        private readonly AesGcm _aes;
        private uint _sendSeq;
        private uint _ackSeq;
        private uint _recvSeq;
        private uint _clearedUpTo;
        private bool _ackPending;
        private DateTime _lastAckSent = DateTime.MinValue;
        private DateTime _lastSent = DateTime.UtcNow;

        public IPEndPoint Remote { get; }
        public string Name { get; internal set; }
        public PeerState State { get; internal set; } = PeerState.Connecting;
        public byte[]? PeerSalt { get; internal set; }
        public DateTime Seen { get; internal set; } = DateTime.UtcNow;
        public DateTime LastHello { get; internal set; } = DateTime.MinValue;
        public int WorldPort { get; internal set; }
        public string WorldName { get; internal set; } = "";

        public Link(PeerNetwork net, IPEndPoint remote, string name)
        {
            _net = net;
            Remote = remote;
            Name = name;
            _aes = new AesGcm(net.SessionKey, Tag);
        }

        public void Enqueue(int streamId, byte[] body, byte type)
        {
            lock (_lock)
            {
                if (PeerSalt == null || State == PeerState.Down) return;
                if (body.Length <= MaxPayload)
                {
                    SendPacket(type, streamId, body);
                    return;
                }
                for (var offset = 0; offset < body.Length; offset += MaxPayload)
                    SendPacket(type, streamId, body.AsSpan(offset, Math.Min(MaxPayload, body.Length - offset)));
            }
        }

        public bool TrySendData(int streamId, ReadOnlySpan<byte> payload)
        {
            lock (_lock)
            {
                if (PeerSalt == null || State != PeerState.Online) return false;
                if (_waiting.Count >= Window) return false;
                SendPacket(TypeData, streamId, payload);
                return true;
            }
        }

        /// <summary>Caller holds _lock: nothing here may take the PeerNetwork lock.</summary>
        private void SendPacket(byte type, int streamId, ReadOnlySpan<byte> body)
        {
            if (PeerSalt == null) return;
            var packet = new byte[Header + body.Length + Tag];
            packet[0] = type;
            Write16(packet, 1, streamId);
            var seq = body.Length == 0 ? ++_ackSeq | AckMarker : ++_sendSeq;
            Write32(packet, 3, seq);
            Write32(packet, 7, _recvSeq);
            var nonce = new byte[12];
            _net.LocalSalt.CopyTo(nonce, 0);
            Write32(nonce, 8, seq);
            body.CopyTo(packet.AsSpan(Header));
            _aes.Encrypt(nonce, body, packet.AsSpan(Header, body.Length),
                packet.AsSpan(Header + body.Length, Tag), packet.AsSpan(0, Header));
            _lastSent = DateTime.UtcNow;
            // Bare acks and keep-alives carry no body and use a separate number space, so losing one
            // never leaves a hole in ordered delivery and they never need retransmit.
            if (body.Length > 0) _waiting[seq] = new Pending(packet, DateTime.UtcNow + Backoff(0), 0);
            _ = _net.SendRaw(packet, Remote);
        }

        public void Receive(byte type, int streamId, uint seq, uint ack, byte[] body)
        {
            // A bare ack/keep-alive carries no ordered payload: consume the acknowledgement and stop,
            // so the two sides never settle into confirming each other's confirmations.
            if ((seq & AckMarker) != 0)
            {
                lock (_lock)
                {
                    Seen = DateTime.UtcNow;
                    if (State == PeerState.Connecting) State = PeerState.Online;
                    if (ack > _clearedUpTo)
                    {
                        _clearedUpTo = ack;
                        ClearAcked(ack);
                    }
                }
                return;
            }
            List<(byte Type, int Stream, byte[] Body)>? ready = null;
            lock (_lock)
            {
                Seen = DateTime.UtcNow;
                if (State == PeerState.Connecting) State = PeerState.Online;
                if (ack > _clearedUpTo)
                {
                    _clearedUpTo = ack;
                    ClearAcked(ack);
                }
                if (seq > _recvSeq && seq <= _recvSeq + (uint)Window)
                {
                    _gaps[seq] = (type, streamId, body);
                    while (_gaps.TryGetValue(_recvSeq + 1, out var next))
                    {
                        _gaps.Remove(_recvSeq + 1);
                        _recvSeq++;
                        // A bare ack or keep-alive is not worth confirming, otherwise the two sides
                        // would go on acknowledging each other's acknowledgements forever.
                        if (next.Type != TypeData || next.Body.Length > 0) _ackPending = true;
                        (ready ??= new List<(byte, int, byte[])>()).Add(next);
                    }
                }
                else if (seq <= _recvSeq && (type != TypeData || body.Length > 0))
                {
                    // A retransmission of something already delivered means our ack for it got lost.
                    // Without re-acknowledging here the sender would retry until the window fills up
                    // and the link would look alive while moving nothing.
                    _ackPending = true;
                }
            }
            if (ready != null)
                foreach (var item in ready) Deliver(item.Type, item.Stream, item.Body);
            FlushAck();
        }

        public void FlushAck()
        {
            lock (_lock)
            {
                if (!_ackPending || PeerSalt == null || State != PeerState.Online) return;
                var now = DateTime.UtcNow;
                if (now - _lastAckSent < AckThrottle) return;
                _ackPending = false;
                _lastAckSent = now;
                SendPacket(TypeAck, 0, ReadOnlySpan<byte>.Empty);
            }
        }

        private void Deliver(byte type, int streamId, byte[] body)
        {
            switch (type)
            {
                case TypeOpen:
                    _net.ConnectLocal(Remote, streamId, ReadPort(body));
                    break;
                case TypeAnnounce:
                    var (port, name) = ReadAnnounce(body);
                    _net.OnPeerWorld(this, port, name);
                    break;
                case TypeData:
                    _net.WriteToLocal(Remote, streamId, body);
                    break;
                case TypeClose:
                    _net.ClosePipe(Remote, streamId);
                    break;
                case TypeIntro:
                    _net.ApplyIntro(this, body);
                    break;
            }
        }

        private void ClearAcked(uint ack)
        {
            if (_waiting.Count == 0) return;
            List<uint>? ready = null;
            foreach (var seq in _waiting.Keys)
                if (seq <= ack) (ready ??= new List<uint>()).Add(seq);
            if (ready == null) return;
            foreach (var seq in ready) _waiting.Remove(seq);
        }

        public void Tick()
        {
            var now = DateTime.UtcNow;
            bool hello = false;
            bool dead = false;
            List<byte[]>? resend = null;
            lock (_lock)
            {
                if (State == PeerState.Down) return;
                if (now - Seen > DeadLink)
                {
                    State = PeerState.Down;
                    dead = true;
                }
                else if (PeerSalt == null)
                {
                    hello = now - LastHello > HelloAgain;
                }
                else
                {
                    if (State == PeerState.Connecting && now - LastHello > HelloAgain) hello = true;
                    if (_ackPending && now - _lastAckSent >= AckThrottle)
                    {
                        _ackPending = false;
                        _lastAckSent = now;
                        SendPacket(TypeAck, 0, ReadOnlySpan<byte>.Empty);
                    }
                    if (now - _lastSent > KeepAlive) SendPacket(TypeAck, 0, ReadOnlySpan<byte>.Empty);
                    if (State == PeerState.Online)
                    {
                        foreach (var entry in _waiting.ToList())
                        {
                            if (entry.Value.Deadline > now) continue;
                            if (entry.Value.Tries >= 10)
                            {
                                State = PeerState.Down;
                                dead = true;
                                break;
                            }
                            _waiting[entry.Key] = new Pending(entry.Value.Packet, now + Backoff(entry.Value.Tries + 1),
                                entry.Value.Tries + 1);
                            (resend ??= new List<byte[]>()).Add(entry.Value.Packet);
                        }
                    }
                }
            }
            if (dead)
            {
                _net.OnLinkDown(this);
                return;
            }
            if (hello) _ = _net.SendHello(this, TypeHello, CancellationToken.None);
            if (resend == null) return;
            foreach (var packet in resend) _ = _net.SendRaw(packet, Remote);
        }

        public void Kill()
        {
            lock (_lock) State = PeerState.Down;
        }

        private static TimeSpan Backoff(int tries) =>
            TimeSpan.FromMilliseconds(Math.Min(2000, 200 * (1 << Math.Min(tries, 4))));

        private sealed record Pending(byte[] Packet, DateTime Deadline, int Tries);
    }

    internal sealed class Pipe
    {
        private const int MaxBuffered = 8 << 20;

        private readonly PeerNetwork _net;
        private readonly Link _link;
        private readonly int _streamId;
        private readonly Socket _local;
        private readonly Channel<byte[]> _toLocal;
        private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private NetworkStream? _stream;
        private int _buffered;
        private int _connectFailed;
        private int _closed;

        public Pipe(PeerNetwork net, Link link, int streamId, Socket local)
        {
            _net = net;
            _link = link;
            _streamId = streamId;
            _local = local;
            _toLocal = Channel.CreateUnbounded<byte[]>();
        }

        /// <summary>Runs the loopback side of the tunnel; the pump waits for this before touching the socket.</summary>
        public async Task ConnectAsync(IPEndPoint target)
        {
            try
            {
                using var step = CancellationTokenSource.CreateLinkedTokenSource(_net.Token);
                step.CancelAfter(TimeSpan.FromSeconds(5));
                await _local.ConnectAsync(target, step.Token);
            }
            catch
            {
                Volatile.Write(ref _connectFailed, 1);
            }
            _connected.TrySetResult();
        }

        public void Connected() => _connected.TrySetResult();

        public void Start()
        {
            _ = Task.Run(PumpLocalToLink);
            _ = Task.Run(PumpLinkToLocal);
        }

        public bool Deliver(byte[] body)
        {
            if (Volatile.Read(ref _closed) != 0) return false;
            // Whatever the peer sends is parked here until the loopback socket is ready, so a world
            // that is slow to accept cannot be blamed for the bytes already on the wire.
            if (Interlocked.Add(ref _buffered, body.Length) > MaxBuffered)
            {
                Interlocked.Add(ref _buffered, -body.Length);
                return false;
            }
            if (!_toLocal.Writer.TryWrite(body))
            {
                Interlocked.Add(ref _buffered, -body.Length);
                return false;
            }
            return true;
        }

        private NetworkStream Stream => _stream ??= new NetworkStream(_local, ownsSocket: false);

        private bool Ready => _connected.Task.IsCompleted && Volatile.Read(ref _connectFailed) == 0
                              && Volatile.Read(ref _closed) == 0 && !_net.IsClosed;

        private async Task PumpLocalToLink()
        {
            var buffer = new byte[MaxPayload];
            try
            {
                await _connected.Task;
                if (!Ready) return;
                while (true)
                {
                    var read = await Stream.ReadAsync(buffer, _net.Token);
                    if (read <= 0) break;
                    var sent = false;
                    while (!sent)
                    {
                        if (_link.State != PeerState.Online) return;
                        sent = _link.TrySendData(_streamId, buffer.AsSpan(0, read));
                        if (!sent) await Task.Delay(2, _net.Token);
                    }
                }
            }
            catch
            {
            }
            Close(true);
        }

        private async Task PumpLinkToLocal()
        {
            try
            {
                await _connected.Task;
                await foreach (var chunk in _toLocal.Reader.ReadAllAsync(_net.Token))
                {
                    if (!Ready) return;
                    await Stream.WriteAsync(chunk, _net.Token);
                    Interlocked.Add(ref _buffered, -chunk.Length);
                }
            }
            catch
            {
            }
            Close(true);
        }

        public void Close(bool notifyRemote)
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _toLocal.Writer.TryComplete();
            if (notifyRemote) _link.Enqueue(_streamId, new byte[] { 0 }, TypeClose);
            _net.ClosePipe(_link.Remote, _streamId);
            try { _local.Shutdown(SocketShutdown.Both); } catch { }
            _local.Dispose();
        }
    }

    internal byte[] SessionKey => _sessionKey;
    internal byte[] LocalSalt => _salt;

    private async Task SendRaw(byte[] packet, IPEndPoint to)
    {
        // A failed send is never fatal: unconfirmed packets are retransmitted by the link timer.
        try { await _socket.SendAsync(packet, to); } catch { }
    }
}
