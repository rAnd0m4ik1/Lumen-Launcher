using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CraftDeck.Dialogs;
using CraftDeck.Models;
using CraftDeck.Services;

namespace CraftDeck.Views;

public record InstanceCard(Instance Inst, string Name, string Glyph, ImageSource? IconImage,
                           string LoaderBadge, string Sub, string Meta)
{
    public bool Running { get; init; }
    public Visibility IconVis => IconImage != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVis => IconImage == null && Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StarVis => IconImage == null && Glyph.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ActionLabel => Running ? "Stop" : "Play";
    public Visibility PlayGlyphVis => Running ? Visibility.Collapsed : Visibility.Visible;
    public Visibility StopGlyphVis => Running ? Visibility.Visible : Visibility.Collapsed;
}

public partial class InstancesView : UserControl
{
    public InstancesView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RunningGames.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => RunningGames.Changed -= Refresh;
    }

    public void Refresh()
    {
        var list = AppServices.Instances.ListAll().Select(ToCard).ToList();
        Cards.ItemsSource = list;
        EmptyHint.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private InstanceCard ToCard(Instance i)
    {
        var img = InstanceIcons.Load(i);
        var glyph = img == null ? InstanceIcons.CustomGlyph(i) : null;
        return new InstanceCard(
            i,
            i.Config.Name,
            glyph ?? "",
            img,
            i.Config.Loader == LoaderKind.Vanilla ? "vanilla" : i.Config.Loader.ToString().ToLower(),
            $"Minecraft {i.Config.MCVersion}" + (i.Config.Loader != LoaderKind.Vanilla ? $" · {i.Config.Loader} {i.Config.LoaderVersion}" : ""),
            i.Config.LastPlayed == default ? "Never played" :
                "Played " + i.Config.LastPlayed.ToString("yyyy-MM-dd HH:mm") +
                (i.Config.PlaytimeSeconds > 60 ? $" · {(int)(i.Config.PlaytimeSeconds / 3600)}h played" : ""))
        { Running = RunningGames.IsRunning(i.Config.Id) };
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NewInstanceDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true) Refresh();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Modrinth modpack (*.mr;*.mrpack)|*.mr;*.mrpack|CurseForge modpack (*.zip)|*.zip|All files (*.*)|*.*",
            Title = "Import a modpack (.mr, .mrpack or a CurseForge .zip) — or just drag it into this window",
        };
        if (ofd.ShowDialog() == true) ImportModpack(ofd.FileName);
    }

    public async void ImportModpack(string file, byte[]? packIcon = null)
    {
        var dlg = new NewInstanceDialog(preferredName: Path.GetFileNameWithoutExtension(file))
        {
            Owner = Window.GetWindow(this),
            InstallOnCreate = false,
        };
        if (dlg.ShowDialog() != true || dlg.CreatedInstance == null) return;
        var inst = dlg.CreatedInstance;
        try
        {
            MainWindow.Main!.SetProgress($"Importing modpack {Path.GetFileName(file)}…", 10);
            await ModrinthService.InstallModpackAsync(inst, file,
                new Progress<string>(s => MainWindow.Main!.SetProgress(s, 50)));
            inst.Config.ModrinthPackId = null;
            if (packIcon != null) InstanceIcons.Store(inst, packIcon);
            AppServices.Instances.Save(inst);
            MainWindow.Main!.SetIdle();
            Refresh();
            if (inst.Config.Loader != LoaderKind.Vanilla || inst.Config.MCVersion != "")
                dlg.InstallNow(inst);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Import failed: " + ex.Message, "Lumen",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            MainWindow.Main!.SetIdle();
        }
    }

    private void Card_Open(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject d) return;
        for (DependencyObject? o = d; o != null; o = VisualTreeHelper.GetParent(o))
            if (o is System.Windows.Controls.Primitives.ButtonBase) return;
        var dc = FindCard(d);
        if (dc != null) MainWindow.Main!.GoInstance(new DetailView(dc.Inst));
    }

    internal static InstanceCard? FindCard(DependencyObject d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.DataContext is InstanceCard card) return card;
            d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) as DependencyObject;
        }
        return null;
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Instance inst }) return;
        if (RunningGames.IsRunning(inst.Config.Id)) { RunningGames.Stop(inst.Config.Id); return; }
        _ = PlayFlow.PlayAsync(inst, this);
    }

    private void AddContent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Instance inst })
            MainWindow.Main!.GoBrowse(inst.Config.Id);
    }

    private Instance? _popupInstance;

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: Instance inst }) return;
        _popupInstance = inst;
        MorePopup.PlacementTarget = sender as UIElement;
        MorePopup.IsOpen = true;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => WithInst(i => Process.Start("explorer.exe", i.GameDir));
    private void OpenMods_Click(object sender, RoutedEventArgs e) => WithInst(i => Process.Start("explorer.exe", i.ModsDir));
    private void Settings_Click(object sender, RoutedEventArgs e) => WithInst(i =>
    {
        if (new InstanceSettingsDialog(i) { Owner = Window.GetWindow(this) }.ShowDialog() != true) return;
        AppServices.Instances.Save(i);
        Refresh();
    });
    private void Duplicate_Click(object sender, RoutedEventArgs e) => WithInst(i =>
    {
        AppServices.Instances.Duplicate(i);
        Refresh();
    });
    private void Delete_Click(object sender, RoutedEventArgs e) => WithInst(i =>
    {
        if (!ConfirmDialog.Ask(Window.GetWindow(this), "Delete modpack",
                $"Delete “{i.Config.Name}” with all its files?", $"Modpack folder: {i.Dir}", "Delete")) return;
        AppServices.Instances.Delete(i);
        Refresh();
    });

    private void WithInst(Action<Instance> a)
    {
        MorePopup.IsOpen = false;
        if (_popupInstance != null) a(_popupInstance);
    }
}
