using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CraftDeck.Models;
using CraftDeck.Models.CurseForge;
using CraftDeck.Models.Modrinth;
using CraftDeck.Services;
using SkiaSharp;

namespace CraftDeck.Views;

public class BrowseCard : INotifyPropertyChanged
{
    private static readonly string[] KnownLoaders =
        { "fabric", "quilt", "forge", "neoforge", "optifine", "vanilla", "bedrock", "sponge" };

    public Hit? Hit { get; init; }
    public CFMod? Cf { get; init; }
    public string? IconUrl { get; init; }
    public string? Slug { get; init; }
    public string Title { get; init; } = "";
    public string Desc { get; init; } = "";
    public string Author { get; init; } = "";
    public string Loader { get; init; } = "";
    public List<string> Cats { get; init; } = new();
    public string Downloads { get; init; } = "";
    public string Follows { get; init; } = "";
    public string Updated { get; init; } = "";
    public string Letter => Title.Length > 0 ? char.ToUpperInvariant(Title[0]).ToString() : "?";
    public Visibility LoaderVis => string.IsNullOrEmpty(Loader) ? Visibility.Collapsed : Visibility.Visible;

    private BitmapImage? _icon;
    public BitmapImage? Icon
    {
        get => _icon;
        set { _icon = value; Raise(nameof(Icon)); Raise(nameof(IconVis)); Raise(nameof(LetterVis)); }
    }
    public Visibility IconVis => Icon == null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LetterVis => Icon == null ? Visibility.Visible : Visibility.Collapsed;

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        set { _installed = value; Raise(nameof(Installed)); Raise(nameof(ActionLabel)); Raise(nameof(ActionTip)); }
    }
    public string ActionLabel => Installed ? "Installed" : "Install";
    public string ActionTip => Installed
        ? "Already in this instance — clicking installs the latest compatible file again"
        : "Install into the selected instance";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    public static BrowseCard From(Hit h)
    {
        var cats = h.display_categories ?? h.categories ?? new List<string>();
        var loaders = (h.categories ?? new List<string>())
            .Where(c => KnownLoaders.Contains(c.ToLowerInvariant())).ToList();
        var card = new BrowseCard
        {
            Hit = h,
            IconUrl = h.icon_url,
            Slug = h.slug,
            Title = h.title ?? "",
            Desc = h.description ?? "",
            Author = string.IsNullOrEmpty(h.author) ? "" : "by " + h.author,
            Loader = loaders.Count == 0 ? ""
                : Cap(loaders[0]) + (loaders.Count > 1 ? "  +" + (loaders.Count - 1) : ""),
            Cats = cats.Where(c => !KnownLoaders.Contains(c.ToLowerInvariant())).Select(Cap).ToList(),
            Downloads = Compact(h.downloads),
            Follows = Compact((long)h.follows),
            Updated = RelTime(h.date_modified ?? h.updated),
        };
        LoadIconAsync(card);
        return card;
    }

    public static BrowseCard From(CFMod m)
    {
        var best = CurseForgeService.PickFile(m, null, ContentType.Mod);
        var loaders = best != null ? CurseForgeService.LoadersOf(best) : "";
        var card = new BrowseCard
        {
            Cf = m,
            IconUrl = m.logo?.url,
            Title = m.name ?? "",
            Desc = m.summary ?? "",
            Author = m.authors?.FirstOrDefault()?.name is { } a ? "by " + a : "",
            Loader = loaders,
            Cats = best != null ? CurseForgeService.VersionsOf(best) : new(),
            Downloads = Compact(m.downloadCount),
            Follows = Compact(m.favoriteCount),
            Updated = RelTime(m.dateModified),
        };
        LoadIconAsync(card);
        return card;
    }

    private static readonly Dictionary<string, Task<byte[]?>> IconCache = new();

    public static Task<byte[]?> IconBytesAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<byte[]?>(null);
        if (IconCache.TryGetValue(url, out var cached)) return cached;
        var task = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.Client.GetByteArrayAsync(url);
                using var data = SKData.CreateCopy(bytes);
                using var img = SKImage.FromEncodedData(data);
                using var png = img?.Encode(SKEncodedImageFormat.Png, 100);
                return png?.ToArray();
            }
            catch { return null; }
        });
        IconCache[url] = task;
        return task;
    }

    private static void LoadIconAsync(BrowseCard card)
    {
        var task = IconBytesAsync(card.IconUrl);
        _ = task.ContinueWith(_ =>
        {
            byte[]? bytes = null;
            try { bytes = task.IsCompletedSuccessfully ? task.Result : null; } catch { }
            if (bytes == null) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = new MemoryStream(bytes);
                bi.EndInit();
                bi.Freeze();
                card.Icon = bi;
            });
        }, TaskScheduler.Default);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Compact(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
        : n >= 1_000 ? $"{n / 1_000.0:0.#}K"
        : n.ToString();

    private static string RelTime(string? iso)
    {
        if (!DateTime.TryParse(iso, out var t)) return "";
        var d = DateTime.UtcNow - t.ToUniversalTime();
        return d.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => $"{(int)d.TotalMinutes}m ago",
            < 24 * 60 => $"{(int)d.TotalHours}h ago",
            < 30 * 24 * 60 => $"{(int)d.TotalDays}d ago",
            < 365 * 24 * 60 => $"{(int)(d.TotalDays / 30)}mo ago",
            _ => $"{(int)(d.TotalDays / 365)}y ago",
        };
    }
}

public partial class BrowseView : UserControl
{
    private static readonly ContentType[] Types =
        { ContentType.Mod, ContentType.Shader, ContentType.ResourcePack, ContentType.DataPack, ContentType.Modpack };

    public BrowseView()
    {
        InitializeComponent();
        TypeBox.ItemsSource = Types.Select(TLabel);
        TypeBox.SelectedIndex = 0;
        SourceBox.SelectedIndex = 0;
    }

    private static string TLabel(ContentType t) => t switch
    {
        ContentType.Shader => "Shaders",
        ContentType.ResourcePack => "Resource packs",
        ContentType.DataPack => "Data packs",
        ContentType.Modpack => "Modpacks",
        _ => "Mods",
    };

    private bool _targetWired;

    public void Prepare(string? instanceId)
    {
        var list = AppServices.Instances.ListAll();
        TargetInstance.ItemsSource = list;
        if (instanceId != null)
            TargetInstance.SelectedItem = list.FirstOrDefault(i => i.Config.Id == instanceId);
        if (list.Count > 0 && TargetInstance.SelectedItem == null) TargetInstance.SelectedIndex = 0;
        if (!_targetWired)
        {
            _targetWired = true;
            TargetInstance.SelectionChanged += (_, _) => { DoSearch(); };
        }
        if (Results.ItemsSource == null) DoSearch();
    }

    private ContentType CurrentType => Types[Math.Max(0, TypeBox.SelectedIndex)];
    private bool OnCurseForge => SourceBox.SelectedIndex == 1;
    private string StateKey() =>
        $"{(TargetInstance.SelectedItem as Instance)?.Config.Id}|{(int)CurrentType}|{SourceBox.SelectedIndex}";

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) DoSearch();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) DoSearch();
    }

    private CancellationTokenSource? _debounce;

    private async void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var tok = _debounce.Token;
        try { await Task.Delay(350, tok); } catch (OperationCanceledException) { return; }
        DoSearch();
    }

    private async void DoSearch()
    {
        var inst = TargetInstance.SelectedItem as Instance;
        var query = SearchBox.Text.Trim();
        Busy.Visibility = Visibility.Visible;
        Hint.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        ResultBar.Visibility = Visibility.Collapsed;
        ResultsScroll.Visibility = Visibility.Collapsed;
        try
        {
            var type = CurrentType;
            List<BrowseCard> cards;
            if (OnCurseForge)
            {
                var cf = await CurseForgeService.SearchAsync(query, type,
                    type == ContentType.Modpack ? null : inst?.Config.MCVersion,
                    type == ContentType.Modpack ? null : inst?.Config.Loader.ToString());
                cards = cf.Select(BrowseCard.From).ToList();
            }
            else
            {
                var hits = await ModrinthService.SearchAsync(query, type,
                    type == ContentType.Modpack ? null : inst?.Config.MCVersion,
                    type == ContentType.Modpack ? null : inst?.Config.Loader.ToString());
                cards = hits.Select(BrowseCard.From).ToList();
            }
            var state = StateKey();
            if (inst != null && type != ContentType.Modpack)
            {
                var sets = await Task.Run(() => ContentMatch.ScanDir(ModrinthService.DirFor(inst, type)));
                if (state != StateKey()) return;
                foreach (var c in cards) c.Installed = ContentMatch.Any(c.Title, c.Slug, sets);
            }
            Results.ItemsSource = cards;
            ResultsScroll.Visibility = Visibility.Visible;
            Hint.Text = "Nothing found.";
            var n = cards.Count;
            if (n > 0)
            {
                Hint.Visibility = Visibility.Collapsed;
                ResultBar.Visibility = Visibility.Visible;
                var cf = OnCurseForge;
                var what = query.Length == 0 ? "popular" : "results";
                ResultCount.Text = $"{n} {what}";
                ResultType.Text = TLabel(type);
                ResultSrc.Text = cf ? "CurseForge" : "Modrinth";
                ResultSrcDot.Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(cf ? (byte)0xF1 : (byte)0x1F,
                                                      cf ? (byte)0x64 : (byte)0xDB,
                                                      cf ? (byte)0x2E : (byte)0x62));
            }
            else ResultBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrorText.Text = "Search failed: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrowseCard card }) return;
        var type = CurrentType;
        if (type == ContentType.Modpack)
        {
            MainWindow.Main!.SetProgress($"Downloading modpack {card.Title}…", 40);
            try
            {
                var iconTask = BrowseCard.IconBytesAsync(card.IconUrl);
                var file = card.Cf != null
                    ? await CurseForgeService.DownloadModpackAsync(card.Cf.id, card.Title,
                        new Progress<string>(s => MainWindow.Main!.SetProgress(s, 60)))
                    : await ModrinthService.DownloadModpackAsync(card.Hit!.project_id, card.Title,
                        new Progress<string>(s => MainWindow.Main!.SetProgress(s, 60)));
                var icon = await iconTask;
                MainWindow.Main!.SetIdle();
                MainWindow.Main!.ImportModpackFile(file, icon);
            }
            catch (Exception ex)
            {
                MainWindow.Main!.SetIdle();
                MessageBox.Show(Window.GetWindow(this), ex.Message, "Lumen",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            return;
        }
        if (TargetInstance.SelectedItem is not Instance inst)
        {
            MessageBox.Show(Window.GetWindow(this), "Create/select an instance first.", "Lumen");
            return;
        }
        MainWindow.Main!.SetProgress($"Installing {card.Title} into {inst.Config.Name}…", 40);
        try
        {
            var loader = inst.Config.Loader == LoaderKind.Vanilla ? "fabric" : inst.Config.Loader.ToString();
            if (card.Cf != null)
                await CurseForgeService.InstallAsync(inst, card.Cf, inst.Config.MCVersion, loader, type);
            else
                await ModrinthService.InstallAsync(inst, card.Hit!, inst.Config.MCVersion, loader, type);
            card.Installed = true;
            MainWindow.Main!.SetIdle();
        }
        catch (Exception ex)
        {
            MainWindow.Main!.SetIdle();
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Lumen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
