using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using CraftDeck.Dialogs;
using CraftDeck.Models;
using CraftDeck.Services;

namespace CraftDeck.Views;

public partial class DetailView : UserControl
{
    private const int LogTab = 4;

    private readonly Instance _inst;
    private readonly StringBuilder _pending = new();
    private readonly DispatcherTimer _logTimer;
    private GameSession? _bound;
    private DateTime? _exitedAt;
    private bool _wasRunning;
    private bool _stickToBottom = true;

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
        LogBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnLogScroll), true);
        Loaded += (_, _) =>
        {
            RunningGames.Changed += OnGamesChanged;
            _wasRunning = RunningGames.IsRunning(inst.Config.Id);
            UpdatePlayButton();
            ShowTab();
        };
        Unloaded += (_, _) =>
        {
            RunningGames.Changed -= OnGamesChanged;
            UnbindLog();
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
        if (running && !_wasRunning && IsLoaded) Tabs.SelectedIndex = LogTab;
        _wasRunning = running;
        if (Tabs.SelectedIndex == LogTab) BindLog();
    }

    private void ShowTab()
    {
        if (!IsLoaded) return;
        if (Tabs.SelectedIndex == LogTab) BindLog();
        else { UnbindLog(); _ = RefreshTab(); }
    }

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
        if (scan == null) return;

        var (list, empty, busy) = Tabs.SelectedIndex switch
        {
            0 => (ModsList, ModsEmpty, ModsBusy),
            1 => (ShadersList, ShadersEmpty, ShadersBusy),
            2 => (RpsList, RpsEmpty, RpsBusy),
            _ => (WorldsList, WorldsEmpty, WorldsBusy),
        };

        var tab = Tabs.SelectedIndex;
        Directory.CreateDirectory(DirForTab());
        busy.Visibility = Visibility.Visible;
        var items = await Task.Run(scan);
        if (Tabs.SelectedIndex != tab) { busy.Visibility = Visibility.Collapsed; return; }
        list.ItemsSource = items;
        empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _ = RefreshTab();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowTab();

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
