using System.Windows;
using System.Windows.Controls;
using CraftDeck.Controls;
using CraftDeck.Models;
using CraftDeck.Services;

namespace CraftDeck.Dialogs;

public partial class NewInstanceDialog : Window
{
    public Instance? CreatedInstance { get; private set; }

    public bool InstallOnCreate { get; set; } = true;

    private List<string> _mcVersions = new();
    private bool _loading;

    public NewInstanceDialog(string? preferredName = null)
    {
        InitializeComponent();
        WindowCorners.Apply(this);
        LoaderBox.ItemsSource = new[] { "Vanilla", "Fabric", "Quilt", "Forge", "NeoForge" };
        LoaderBox.SelectedIndex = 1;
        LoaderBox.SelectionChanged += async (_, _) => await LoadLoaderVersionsAsync();
        if (preferredName != null) NameBox.Text = preferredName;
        else NameBox.Text = "New pack";
        Loaded += LoadVersionsAsync;
    }

    private async void LoadVersionsAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            _loading = true;
            var manifest = await AppServices.Minecraft.GetManifestAsync();
            ApplyVersions(manifest);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not load version list: " + ex.Message +
                                  "\nCheck your internet connection.", "Lumen");
        }
        finally { _loading = false; }
    }

    private void ApplyVersions(Models.Minecraft.VersionManifest manifest)
    {
        _mcVersions = manifest.versions
            .Where(v => Snapshots.IsChecked == true || v.type == "release")
            .Select(v => v.id).ToList();
        MCBox.ItemsSource = _mcVersions;
        var idx = _mcVersions.IndexOf("1.20.1");
        MCBox.SelectedIndex = idx >= 0 ? idx : 0;
        _ = LoadLoaderVersionsAsync();
    }

    private void Snapshots_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading && AppServices.Minecraft != null && IsLoaded) ReloadManifest();
    }

    private async void ReloadManifest()
    {
        try { ApplyVersions(await AppServices.Minecraft.GetManifestAsync()); } catch { }
    }

    private async Task LoadLoaderVersionsAsync()
    {
        if (MCBox.SelectedItem is not string mc) return;
        var loader = (string)LoaderBox.SelectedItem;
        try
        {
            List<string> versions = loader switch
            {
                "Vanilla" => new(),
                "Fabric" => await LoaderService.GetFabricLoadersAsync(),
                "Quilt" => await LoaderService.GetQuiltLoadersAsync(),
                "Forge" => await LoaderService.GetForgeVersionsAsync(mc),
                "NeoForge" => await LoaderService.GetNeoForgeVersionsAsync(mc),
                _ => new(),
            };
            LoaderVerBox.ItemsSource = versions;
            LoaderVerBox.Visibility = versions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (versions.Count > 0) LoaderVerBox.SelectedIndex = 0;
            LoaderHint.Text = versions.Count == 0 && loader != "Vanilla"
                ? $"{loader} has no release for Minecraft {mc}."
                : "";
            LoaderHint.Visibility = string.IsNullOrEmpty(LoaderHint.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            LoaderVerBox.ItemsSource = new List<string>();
            MessageBox.Show(this, "Could not load " + loader + " versions: " + ex.Message, "Lumen");
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (MCBox.SelectedItem is not string mc) return;
        var loaderName = (string)LoaderBox.SelectedItem;
        var loaderVer = LoaderVerBox.SelectedItem as string ?? "";
        if (loaderName != "Vanilla" && string.IsNullOrEmpty(loaderVer))
        {
            MessageBox.Show(this, "Pick a loader version.", "Lumen");
            return;
        }

        var cfg = new InstanceConfig
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? mc : NameBox.Text.Trim(),
            Icon = "",
            MCVersion = mc,
            Loader = loaderName switch
            {
                "Fabric" => LoaderKind.Fabric,
                "Quilt" => LoaderKind.Quilt,
                "Forge" => LoaderKind.Forge,
                "NeoForge" => LoaderKind.NeoForge,
                _ => LoaderKind.Vanilla,
            },
            LoaderVersion = loaderVer,
        };

        CreatedInstance = AppServices.Instances.Create(cfg);
        DialogResult = true;
        Close();
        if (InstallOnCreate) InstallNow(CreatedInstance);
    }

    public async void InstallNow(Instance inst)
    {
        var main = MainWindow.Main!;
        try
        {
            await AppServices.Launch.EnsureInstalledAsync(inst,
                new Progress<string>(s => main.SetProgress(s, 5)), CancellationToken.None);
            main.SetIdle();
        }
        catch (Exception ex)
        {
            main.SetIdle();
            MessageBox.Show(main, "Install failed: " + ex.Message, "Lumen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
