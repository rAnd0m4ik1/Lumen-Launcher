using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CraftDeck.Dialogs;
using CraftDeck.Models;
using CraftDeck.Services;

namespace CraftDeck.Views;

public partial class DetailView : UserControl
{
    private const int LogTab = 4;
    private const int NetworkTab = 6;

    private readonly Instance _inst;
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _logTimer;
    private readonly DispatcherTimer _netTimer;
    private List<int> _ports = new();
    private string _rowsSig = "";
    private bool _netAction;
    private static PeerNetwork? _room;
    private static string _roomCode = "";
    private static string _roomNote = "";
    private static readonly Dictionary<(string, int), int> _tunnels = new();
    private GameSession? _bound;
    private DateTime? _exitedAt;
    private bool _wasRunning;
    private bool _stickToBottom = true;
    private List<ContentItem> _tabItems = new();
    private int _itemsTab = -1;
    private readonly Dictionary<string, ContentProject> _sources = new(StringComparer.OrdinalIgnoreCase);
    private ContentProject? _menu;
    private CancellationTokenSource? _menuLoad;

    public DetailView(Instance inst)
    {
        _inst = inst;
        InitializeComponent();
        Title.Text = inst.Config.Name;
        SubInfo.Text = "Minecraft " + inst.Config.MCVersion +
                       (inst.Config.Loader != LoaderKind.Vanilla ? $" · {inst.Config.Loader} {inst.Config.LoaderVersion}" : " · vanilla");
        DirInfo.Text = inst.Dir;
        ShowIcon();
        _logTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _logTimer.Tick += (_, _) => FlushLog();
        _netTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _netTimer.Tick += (_, _) => RefreshNet();
        LogBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnLogScroll), true);
        Loaded += (_, _) =>
        {
            RunningGames.Changed += OnGamesChanged;
            ShareWorld.Changed += RefreshNet;
            _wasRunning = RunningGames.IsRunning(inst.Config.Id);
            UpdatePlayButton();
            ShowTab();
        };
        Unloaded += (_, _) =>
        {
            RunningGames.Changed -= OnGamesChanged;
            ShareWorld.Changed -= RefreshNet;
            _netTimer.Stop();
            UnbindLog();
            _menuLoad?.Cancel();
        };
    }

    private void UpdatePlayButton()
    {
        var running = RunningGames.IsRunning(_inst.Config.Id);
        PlayGlyph.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopBox.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PlayText.Text = running ? "Stop" : "Play";
        AutomationProperties.SetName(PlayBtn, running ? "Stop" : "Play");
    }

    private void OnGamesChanged()
    {
        UpdatePlayButton();
        var running = RunningGames.IsRunning(_inst.Config.Id);
        if (running && !_wasRunning && IsLoaded && Tabs.SelectedIndex != NetworkTab) Tabs.SelectedIndex = LogTab;
        _wasRunning = running;
        if (Tabs.SelectedIndex == LogTab) BindLog();
        if (Tabs.SelectedIndex == NetworkTab) RefreshNet();
    }

    private void ShowTab()
    {
        if (!IsLoaded) return;
        UpdateFilterBox();
        if (Tabs.SelectedIndex == LogTab)
        {
            _netTimer.Stop();
            BindLog();
            return;
        }
        UnbindLog();
        if (Tabs.SelectedIndex == NetworkTab)
        {
            _netTimer.Start();
            RefreshNet();
        }
        else _netTimer.Stop();
        _ = RefreshTab();
    }

    private void UpdateFilterBox()
    {
        var content = Tabs.SelectedIndex is >= 0 and < LogTab;
        FilterBox.Visibility = content ? Visibility.Visible : Visibility.Collapsed;
        FilterBox.Tag = Tabs.SelectedIndex switch
        {
            0 => "Search mods…",
            1 => "Search shaders…",
            2 => "Search resource packs…",
            3 => "Search worlds…",
            _ => "Search…",
        };
        if (!content && FilterBox.Text.Length > 0) FilterBox.Text = "";
    }

    private ItemsControl? ListForTab() => Tabs.SelectedIndex switch
    {
        0 => ModsList,
        1 => ShadersList,
        2 => RpsList,
        3 => WorldsList,
        _ => null,
    };

    private void ApplyFilter()
    {
        if (ListForTab() is not { } list || _itemsTab != Tabs.SelectedIndex) return;
        var q = FilterBox.Text.Trim();
        list.ItemsSource = q.Length == 0
            ? _tabItems
            : _tabItems.Where(i => Matches(i, q)).ToList();
    }

    private static bool Matches(ContentItem item, string q) =>
        item.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        item.Desc.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        item.Meta.Contains(q, StringComparison.OrdinalIgnoreCase);

    private void Filter_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void BindLog()
    {
        UnbindLog();
        _stickToBottom = true;
        var session = RunningGames.Of(_inst.Config.Id);
        if (session == null)
        {
            LogState.Text = "";
            var saved = new[] { Path.Combine(_inst.GameDir, "logs", "latest.log"), Path.Combine(_inst.Dir, "latest.log") }
                .FirstOrDefault(File.Exists);
            if (saved == null) return;
            try
            {
                LogBox.Text = string.Join("\n", File.ReadLines(saved).TakeLast(3000)) + "\n";
                LogState.Text = "Last saved game log";
            }
            catch { }
            return;
        }

        _bound = session;
        LogBox.Text = string.Join("\n", session.Log.Snapshot()) + "\n";
        session.Log.LineAppended += OnLogLine;
        _exitedAt = session.Running ? null : DateTime.Now;
        _logTimer.Start();
        DescribeState();
    }

    private void UnbindLog()
    {
        _logTimer.Stop();
        if (_bound != null) _bound.Log.LineAppended -= OnLogLine;
        _bound = null;
        _exitedAt = null;
        lock (_pending) _pending.Clear();
    }

    private void OnLogLine(string line)
    {
        lock (_pending) _pending.AppendLine(line);
    }

    private void OnLogScroll(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
            _stickToBottom = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 24;
        else if (_stickToBottom) LogBox.ScrollToEnd();
    }

    private void FlushLog()
    {
        string chunk;
        lock (_pending)
        {
            chunk = _pending.ToString();
            _pending.Clear();
        }
        if (chunk.Length > 0) LogBox.AppendText(chunk);
        if (_bound == null) { _logTimer.Stop(); return; }
        if (_bound.Running) { DescribeState(); return; }
        _exitedAt ??= DateTime.Now;
        if (chunk.Length > 0 || DateTime.Now - _exitedAt < TimeSpan.FromSeconds(1)) return;
        _logTimer.Stop();
        _bound.Log.LineAppended -= OnLogLine;
        DescribeState();
        _bound = null;
    }

    private void DescribeState()
    {
        if (_bound == null) return;
        LogState.Text = _bound.Running ? $"Running · {_bound.Elapsed:hh\\:mm\\:ss}"
            : _bound.StopRequested ? "Stopped"
            : _bound.ExitCode == 0 ? "Closed"
            : $"Closed · exit code 0x{_bound.ExitCode:X8}";
    }

    private void ShowIcon()
    {
        var img = InstanceIcons.Load(_inst);
        var glyph = img == null ? InstanceIcons.CustomGlyph(_inst) : null;
        IconImage.Source = img;
        IconImage.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        IconGlyph.Text = glyph ?? "";
        IconGlyph.Visibility = glyph != null ? Visibility.Visible : Visibility.Collapsed;
        IconStar.Visibility = img == null && glyph == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshTab()
    {
        var scan = Tabs.SelectedIndex switch
        {
            0 => (Func<List<ContentItem>>)(() => InstanceContent.Mods(_inst.ModsDir)),
            1 => () => InstanceContent.Shaders(_inst.ShaderPacksDir),
            2 => () => InstanceContent.ResourcePacks(_inst.ResourcePacksDir),
            3 => () => InstanceContent.Worlds(Path.Combine(_inst.GameDir, "saves")),
            _ => null,
        };
        if (scan == null || ListForTab() is not { } list) return;

        var busy = Tabs.SelectedIndex switch
        {
            0 => ModsBusy,
            1 => ShadersBusy,
            2 => RpsBusy,
            _ => WorldsBusy,
        };

        var tab = Tabs.SelectedIndex;
        Directory.CreateDirectory(DirForTab());
        busy.Visibility = Visibility.Visible;
        var items = await Task.Run(scan);
        if (Tabs.SelectedIndex != tab) { busy.Visibility = Visibility.Collapsed; return; }
        _tabItems = items;
        _itemsTab = tab;
        list.ItemsSource = items;
        ApplyFilter();
        busy.Visibility = Visibility.Collapsed;
    }

    private string DirForTab() => Tabs.SelectedIndex switch
    {
        0 => _inst.ModsDir,
        1 => _inst.ShaderPacksDir,
        2 => _inst.ResourcePacksDir,
        3 => Path.Combine(_inst.GameDir, "saves"),
        _ => _inst.GameDir,
    };

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ContentItem item }) return;
        var owner = Window.GetWindow(this);
        var isDir = Directory.Exists(item.Path);
        if (isDir && !ConfirmDialog.Ask(owner, "Delete",
                $"Delete “{item.Title}” with everything inside?", item.Path, "Delete")) return;
        try
        {
            if (isDir) Directory.Delete(item.Path, true);
            else File.Delete(item.Path);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Lumen");
        }
        _sources.Remove(item.Path);
        _ = RefreshTab();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        VersionPopup.IsOpen = false;
        ShowTab();
    }

    private static ContentType TypeForTab(int tab) => tab switch
    {
        1 => ContentType.Shader,
        2 => ContentType.ResourcePack,
        _ => ContentType.Mod,
    };

    private string LoaderName() =>
        _inst.Config.Loader == LoaderKind.Vanilla ? "" : _inst.Config.Loader.ToString();

    private async void Versions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ContentItem item }) return;
        _menu = null;
        _menuLoad?.Cancel();
        var cts = _menuLoad = new CancellationTokenSource();
        VersionTitle.Text = item.Title;
        VersionPopup.PlacementTarget = sender as UIElement;
        VersionPopup.IsOpen = true;
        if (_sources.TryGetValue(item.Path, out var known))
        {
            ShowVersions(known);
            return;
        }
        VersionList.ItemsSource = null;
        VersionBusy.Text = "Loading…";
        VersionBusy.Visibility = Visibility.Visible;
        try
        {
            var found = await ContentSource.LocateAsync(_inst, item.Path, item.Title,
                TypeForTab(Tabs.SelectedIndex), cts.Token);
            if (cts.IsCancellationRequested) return;
            if (found == null)
            {
                VersionBusy.Text = "This file is not on Modrinth or CurseForge.";
                return;
            }
            _sources[item.Path] = found;
            ShowVersions(found);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            VersionBusy.Text = "Could not load versions: " + ex.Message;
        }
    }

    private void ShowVersions(ContentProject p)
    {
        _menu = p;
        var rows = Rows(p);
        if (rows.Count == 0)
        {
            VersionList.ItemsSource = null;
            VersionBusy.Text = $"No files for Minecraft {_inst.Config.MCVersion}.";
            VersionBusy.Visibility = Visibility.Visible;
            return;
        }
        VersionBusy.Visibility = Visibility.Collapsed;
        VersionList.ItemsSource = rows.Select(r => r.Pick(r.Id == p.CurrentId)).ToList();
    }

    private List<VersionRow> Rows(ContentProject p) =>
        p.Versions != null
            ? p.Versions.Select(VersionRow.From).ToList()
            : p.Files!.Select(f => VersionRow.From(f, _inst.Config.MCVersion, LoaderName())).ToList();

    private async void VersionRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VersionRow row } || _menu is not { } p) return;
        VersionPopup.IsOpen = false;
        MainWindow.Main!.SetProgress($"Installing {p.Title} · {row.Label}…");
        try
        {
            await ContentSource.ApplyAsync(_inst, p, row.Id, TypeForTab(Tabs.SelectedIndex));
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Lumen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            MainWindow.Main!.SetIdle();
        }
        _sources.Clear();
        await RefreshTab();
    }

    private sealed record WorldRow(string Title, string Sub, Visibility Join, PeerInfo Peer);

    private void RefreshNet()
    {
        if (_netAction) return;
        var session = RunningGames.Of(_inst.Config.Id);
        Render(session is { Running: true } && !session.Proc.HasExited, _room);
    }

    private void ApplyPorts(List<int> ports)
    {
        if (ports.SequenceEqual(_ports)) return;
        _ports = ports;

        var items = ports.Select(p => p.ToString()).ToList();
        var keep = PortPick.SelectedItem as string;
        PortPick.ItemsSource = items;
        var wanted = ShareWorld.Port.ToString();
        if (items.Contains(wanted)) PortPick.SelectedItem = wanted;
        else if (keep != null && items.Contains(keep)) PortPick.SelectedItem = keep;
        else PortPick.SelectedIndex = items.Count > 0 ? 0 : -1;
    }

    private void Render(bool running, PeerNetwork? room)
    {
        if (room == null)
        {
            NetState.Text = _roomNote != "" ? _roomNote
                : "Create a game and send the code to a friend, or paste the code they sent you.";
            NetStart.Visibility = Visibility.Visible;
            NetLive.Visibility = Visibility.Collapsed;
            CreateBtn.IsEnabled = true;
            JoinBtn.IsEnabled = true;
        }
        else
        {
            NetState.Text = room.IsHost ? "Your game is live. Send this code to a friend:"
                : "You are in your friend's game. The same code invites others:";
            NetStart.Visibility = Visibility.Collapsed;
            NetLive.Visibility = Visibility.Visible;
            CodeText.Text = _roomCode;
        }

        var ports = ShareWorld.Ports;
        ApplyPorts(ports);
        var sharing = ReferenceEquals(ShareWorld.Owner, _inst);
        var shared = ShareWorld.WorldName;
        ShareName.Text = shared != "" && ShareWorld.Port > 0
            ? $"Sharing “{shared}” · port {ShareWorld.Port}"
            : $"Sharing “{shared}”";
        ShareName.Visibility = sharing && shared != "" ? Visibility.Visible : Visibility.Collapsed;
        ShareBtn.Content = sharing ? "Stop sharing" : "Share this world";
        ShareBtn.IsEnabled = room != null;
        PortPick.Visibility = sharing && ports.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ShareState.Text = room == null ? "Start a game first, then your world can be seen inside it."
            : sharing && running && ShareWorld.Port > 0 ? "Friends can join this world straight from their launcher."
            : sharing && running ? "In game press Esc → Open to LAN, the port shows up here."
            : sharing ? "Start this pack, enter a world, then press Esc → Open to LAN."
            : ShareWorld.Owner != null ? "Another pack is sharing its world. Stop it there to share this one."
            : "Not shared: your friends see only their own worlds.";

        var rows = room?.Peers.Select(RowFor).ToList() ?? new List<WorldRow>();
        var ready = rows.Count(r => r.Join == Visibility.Visible);
        var here = room?.Peers.Count(p => p.State == PeerState.Online) ?? 0;
        RoomState.Text = room == null ? "You are not in a game yet."
            : here == 0 ? "Nobody else is here yet."
            : ready == 0 ? $"{here} friend{(here == 1 ? "" : "s")} in, no world shared yet."
            : $"{ready} world{(ready == 1 ? "" : "s")} ready to join.";

        var sig = string.Join("|", rows.Select(r => $"{r.Title}|{r.Sub}|{r.Join}"));
        if (sig != _rowsSig)
        {
            _rowsSig = sig;
            WorldList.ItemsSource = rows;
        }
    }

    private static WorldRow RowFor(PeerInfo p)
    {
        var where = p.Endpoint.Address.ToString();
        if (p.State != PeerState.Online)
            return new WorldRow(p.State == PeerState.Connecting ? "Connecting…" : "Lost touch",
                where, Visibility.Collapsed, p);
        if (p.WorldPort <= 0)
            return new WorldRow("In the game", where + " · shares no world", Visibility.Collapsed, p);
        return new WorldRow(p.WorldName != "" ? p.WorldName : "Friend's world",
            where + " · port " + p.WorldPort, Visibility.Visible, p);
    }

    private void PortPick_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(ShareWorld.Owner, _inst)) return;
        if (PortPick.SelectedItem is string s && int.TryParse(s, out var port)) ShareWorld.Pick(port);
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (CodeText.Text.Length == 0) return;
        try { Clipboard.SetText(CodeText.Text); } catch { return; }
        MainWindow.Main!.SetIdle("Code copied");
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_room != null || _netAction) return;
        _netAction = true;
        _roomNote = "";
        CreateBtn.IsEnabled = false;
        JoinBtn.IsEnabled = false;
        NetState.Text = "Finding your address…";
        try
        {
            var net = await PeerNetwork.HostAsync();
            if (net == null)
                _roomNote = "Could not find your address on the internet. Check the connection and try again.";
            else
            {
                _room = net;
                _roomCode = net.Code;
                _tunnels.Clear();
                _rowsSig = "";
            }
        }
        finally
        {
            _netAction = false;
            RefreshNet();
        }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_netAction || _room != null) return;
        if (!NetCode.TryParse(CodeBox.Text, out var invite))
        {
            _roomNote = "That code is not right — 20 letters in groups of five, the way your friend sent it.";
            RefreshNet();
            return;
        }

        _netAction = true;
        _roomNote = "";
        CreateBtn.IsEnabled = false;
        JoinBtn.IsEnabled = false;
        NetState.Text = "Reaching your friend…";
        try
        {
            var net = await PeerNetwork.JoinAsync(invite);
            if (net == null)
                _roomNote = "Nobody answered that code. Check it with your friend — their Lumen has to stay open.";
            else
            {
                _room = net;
                _roomCode = NetCode.Create(invite.Host, invite.Key);
                _tunnels.Clear();
                _rowsSig = "";
            }
        }
        finally
        {
            _netAction = false;
            RefreshNet();
        }
    }

    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        var room = _room;
        if (room == null) return;
        _room = null;
        _roomCode = "";
        _roomNote = "";
        _tunnels.Clear();
        _rowsSig = "";
        ShareWorld.Stop();
        RefreshNet();
        await room.DisposeAsync();
    }

    private void Share_Click(object sender, RoutedEventArgs e)
    {
        var room = _room;
        if (room == null) return;
        if (ReferenceEquals(ShareWorld.Owner, _inst)) ShareWorld.Stop();
        else ShareWorld.Start(_inst, room);
        RefreshNet();
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var room = _room;
        if (sender is not Button { Tag: WorldRow row } || room == null) return;
        if (RunningGames.IsRunning(_inst.Config.Id))
        {
            RoomState.Text = "Stop this pack first, then connect.";
            return;
        }

        var key = (row.Peer.Endpoint.ToString(), row.Peer.WorldPort);
        if (!_tunnels.TryGetValue(key, out var local))
        {
            local = room.OpenLocalTunnel(row.Peer);
            if (local <= 0)
            {
                RoomState.Text = "That world is not reachable right now.";
                return;
            }
            _tunnels[key] = local;
        }
        _ = PlayFlow.PlayAsync(_inst, this, PlayBtn, $"127.0.0.1:{local}");
    }

    private void CodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Join_Click(sender, new RoutedEventArgs());
    }

    private void Back_Click(object sender, RoutedEventArgs e) => MainWindow.Main!.GoInstances();
    private void Add_Click(object sender, RoutedEventArgs e) => MainWindow.Main!.GoBrowse(_inst.Config.Id);

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (RunningGames.IsRunning(_inst.Config.Id)) RunningGames.Stop(_inst.Config.Id);
        else _ = PlayFlow.PlayAsync(_inst, this, PlayBtn);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (new InstanceSettingsDialog(_inst) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        AppServices.Instances.Save(_inst);
        Title.Text = _inst.Config.Name;
        ShowIcon();
        MainWindow.Main!.SetIdle();
    }

    private void OpenGame_Click(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", _inst.GameDir);
    private void OpenSaves_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Path.Combine(_inst.GameDir, "saves"));
        Process.Start("explorer.exe", Path.Combine(_inst.GameDir, "saves"));
    }
    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_inst.ConfigDir);
        Process.Start("explorer.exe", _inst.ConfigDir);
    }
}
