using System.Windows;
using System.Windows.Controls;
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
        WindowCorners.Apply(this);
        PageHost.Content = _instances;
        RefreshAccount();
        Fx.Mode = BackgroundFX.ParseMode(AppServices.Settings.BackgroundFx);
        PreviewMouseMove += (_, e) => Fx.SetPointer(e.GetPosition(Fx), true);
        MouseLeave += (_, _) => Fx.SetPointer(new Point(ActualWidth / 2, ActualHeight * 0.42), false);
        Deactivated += (_, _) => Fx.SetPointer(new Point(ActualWidth / 2, ActualHeight * 0.42), false);
        StateChanged += (_, _) =>
        {
            MaxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            WinFrame.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        };
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
        if (page is not System.Windows.UIElement el) return;
        el.Opacity = 0;
        var move = new System.Windows.Media.TranslateTransform(0, 12);
        el.RenderTransform = move;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, System.TimeSpan.FromMilliseconds(180));
        var slide = new System.Windows.Media.Animation.DoubleAnimation(12, 0, System.TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        fade.Completed += (_, _) => el.RenderTransform = null;
        el.BeginAnimation(System.Windows.UIElement.OpacityProperty, fade);
        move.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }

    public void SetProgress(string text, double percent)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            StatusProgress.Visibility = Visibility.Visible;
            StatusProgress.Value = Math.Clamp(percent, 0, 100);
        });
    }

    public void SetIdle(string text = "Ready")
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = text;
            StatusProgress.Visibility = Visibility.Hidden;
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
        else if (sender == NavAccounts) { SetPage(_accounts); _accounts.Refresh(); }
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
