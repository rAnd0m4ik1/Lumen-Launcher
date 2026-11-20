using System.Windows;
using CraftDeck.Controls;
using CraftDeck.Services;
using CraftDeck.Views;

namespace CraftDeck;

public partial class MainWindow : Window
{
    public static MainWindow? Main { get; private set; }

    private readonly InstancesView _instances = new();
    private readonly BrowseView _browse = new();
    private readonly AccountsView _accounts = new();
    private readonly SettingsView _settings = new();

    public MainWindow()
    {
        Main = this;
        InitializeComponent();
        VersionText.Text = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0";
        WindowCorners.Apply(this);
        MaximizeToWorkArea.Apply(this);
        PageHost.Content = _instances;
        RefreshAccount();
        Fx.Mode = BackgroundFX.ParseMode(AppServices.Settings.BackgroundFx);
        PreviewMouseMove += (_, e) => Fx.SetPointer(e.GetPosition(Fx), true);
        MouseLeave += (_, _) => Fx.SetPointer(new Point(ActualWidth / 2, ActualHeight * 0.42), false);
        Deactivated += (_, _) => Fx.SetPointer(new Point(ActualWidth / 2, ActualHeight * 0.42), false);
        StateChanged += (_, _) =>
        {
            MaxGlyph.Glyph = WindowState == WindowState.Maximized ? "CaptionRestore" : "CaptionMax";
            WinFrame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        };
        UpdateService.Changed += OnUpdateChanged;
        RunningGames.Changed += OnUpdateChanged;
        UpdateUi();
    }

    private void OnUpdateChanged() => Dispatcher.BeginInvoke(UpdateUi);

    private void UpdateUi()
    {
        var rel = UpdateService.Available;
        if (rel == null || UpdateService.Blocked)
        {
            NavUpdate.Visibility = Visibility.Collapsed;
            return;
        }
        NavUpdate.Visibility = Visibility.Visible;
        UpdateLabel.Text = UpdateService.Staged ? "Restart to apply"
            : UpdateService.Busy ? $"Updating {Math.Round(UpdateService.Fraction * 100)}%"
            : $"Update to {rel.Tag}";
        NavUpdate.IsEnabled = !UpdateService.Busy;
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Staged)
        {
            UpdateService.RestartToApply();
            Close();
            return;
        }
        if (UpdateService.Available == null) return;
        UpdateUi();
        try
        {
            await UpdateService.StageAsync();
            if (UpdateService.Staged)
            {
                UpdateService.RestartToApply();
                Close();
            }
        }
        catch (Exception ex)
        {
            App.Log("update stage: " + ex);
            SetIdle("Update failed");
        }
        UpdateUi();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public void SetBackgroundFx(FxMode mode) => Fx.Mode = mode;

    public void RefreshBackgroundFxColors() => Fx.RefreshColors();

    public void RefreshAccount()
    {
        var name = AppServices.Settings.Username;
        var ms = AppServices.Settings.AccountType == "Microsoft";
        AccountName.Text = name;
        AccountType.Text = ms ? "Microsoft" : "Offline";
        AccountDot.Fill = new System.Windows.Media.SolidColorBrush(
            ms ? System.Windows.Media.Color.FromRgb(0x1F, 0xDB, 0x62)
               : System.Windows.Media.Color.FromRgb(0x8A, 0x8F, 0x9E));
        AccountInitial.Text = string.IsNullOrEmpty(name) ? "?" : name[0].ToString().ToUpperInvariant();
    }

    public void SetPage(object page)
    {
        if (Equals(PageHost.Content, page)) return;
        PageHost.Content = page;
    }

    public void SetProgress(string text)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            StatusChip.Visibility = Visibility.Visible;
        });
    }

    public void SetIdle(string text = "Ready")
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            StatusChip.Visibility = text == "Ready" ? Visibility.Collapsed : Visibility.Visible;
        });
    }

    public void GoBrowse(string? instanceFilter = null)
    {
        NavBrowse.IsChecked = true;
        SetPage(_browse);
        _browse.Prepare(instanceFilter);
    }

    public void GoInstance(DetailView view)
    {
        NavInstances.IsChecked = true;
        SetPage(view);
    }

    public void GoInstances()
    {
        NavInstances.IsChecked = true;
        SetPage(_instances);
        _instances.Refresh();
    }

    public void ImportModpackFile(string path, byte[]? packIcon = null) => _instances.ImportModpack(path, packIcon);

    public void GoAccounts()
    {
        NavAccounts.IsChecked = true;
        SetPage(_accounts);
        _accounts.Refresh();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageHost == null) return;
        if (sender == NavInstances) GoInstances();
        else if (sender == NavBrowse) { SetPage(_browse); _browse.Prepare(null); }
        else if (sender == NavAccounts) GoAccounts();
        else if (sender == NavSettings) { SetPage(_settings); _settings.Refresh(); }
    }

    private void Account_Click(object sender, RoutedEventArgs e)
    {
        NavAccounts.IsChecked = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            var mr = files.FirstOrDefault(f => f.EndsWith(".mr", StringComparison.OrdinalIgnoreCase) ||
                                               f.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
                                               f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (mr != null) _instances.ImportModpack(mr);
        }
    }
}
