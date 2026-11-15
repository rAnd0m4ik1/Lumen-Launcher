using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private bool _installing;
    public bool Installing
    {
        get => _installing;
        set { _installing = value; Raise(nameof(Installing)); Raise(nameof(ActionLabel)); }
    }
    public string ActionLabel => Installing ? "Installing" : Installed ? "Installed" : "Install";
    public string ActionTip => Pinned != null
        ? $"Install “{Pinned.Label}” into the selected instance"
        : Installed
            ? "Already in this instance — clicking installs the latest compatible file again"
            : "Install into the selected instance";

    private VersionRow? _pinned;
    public VersionRow? Pinned
    {
        get => _pinned;
        set
        {
            _pinned = value;
            Raise(nameof(Pinned)); Raise(nameof(PinnedLabel)); Raise(nameof(PinnedVis)); Raise(nameof(ActionTip));
        }
    }
    public string PinnedLabel => Pinned?.Label ?? "";
    public Visibility PinnedVis => Pinned == null ? Visibility.Collapsed : Visibility.Visible;
    public List<VersionRow>? Files { get; set; }
    public string? FilesKey { get; set; }

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

    public static BrowseCard From(CFMod m, string mc, string loader, CFFile best)
    {
        var ls = CurseForgeService.LoadersOf(best, loader).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var card = new BrowseCard
        {
            Cf = m,
            IconUrl = m.logo?.url,
            Title = m.name ?? "",
            Desc = m.summary ?? "",
            Author = m.authors?.FirstOrDefault()?.name is { } a ? "by " + a : "",
            Loader = ls.Length == 0 ? "" : ls[0] + (ls.Length > 1 ? "  +" + (ls.Length - 1) : ""),
            Cats = CurseForgeService.VersionsOf(best, mc),
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

    internal static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Compact(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M"
        : n >= 1_000 ? $"{n / 1_000.0:0.#}K"
        : n.ToString();

    internal static string RelTime(string? iso)
    {
        if (!DateTime.TryParse(iso, out var t)) return "";
        return RelTime(t);
    }

    internal static string RelTime(DateTime t)
    {
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

public sealed class VersionRow
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Meta { get; init; } = "";
    public bool Picked { get; init; }
    public Visibility CheckVis => Picked ? Visibility.Visible : Visibility.Collapsed;

    public VersionRow Pick(bool picked) => new() { Id = Id, Label = Label, Meta = Meta, Picked = picked };

    public static VersionRow From(ProjectVersion v) => new()
    {
        Id = v.id,
        Label = DropMc(string.IsNullOrWhiteSpace(v.version_number) ? v.name : v.version_number, v.game_versions),
        Meta = Parts(Take(v.game_versions, 3), CapAll(v.loaders, 2), BrowseCard.RelTime(v.date_published)),
    };

    public static VersionRow From(CFFile f, string? mc = null, string? loader = null)
    {
        var mcs = CurseForgeService.VersionsOf(f, mc);
        return new()
        {
            Id = f.id.ToString(),
            Label = DropMc(StripExt(string.IsNullOrWhiteSpace(f.displayName) ? f.fileName : f.displayName), mcs),
            Meta = Parts(string.Join(", ", mcs),
                         Take(CurseForgeService.LoadersOf(f, loader).Split(' '), 2),
                         BrowseCard.RelTime(f.fileDate)),
        };
    }

    private static string DropMc(string label, List<string>? mcs)
    {
        if (label.Length == 0 || mcs == null) return label;
        var s = label;
        foreach (var mc in mcs.Where(m => m.Length > 3).OrderByDescending(m => m.Length))
            s = Regex.Replace(s, @"(?<![\d.])" + Regex.Escape(mc) + @"(?![\d.])", " ");
        s = Regex.Replace(s, @"[\(\[\{]\s*[\)\]\}]", " ");
        s = Regex.Replace(s, @"(^|[-+_ ])\s*mc\s*(?=[-+_,])", "$1", RegexOptions.IgnoreCase);
        for (var pass = 0; pass < 3; pass++)
        {
            var t = Regex.Replace(s, @"([-+_])\s*(?:[-+_]\s*)*", "$1");
            t = Regex.Replace(t, @"\s+(?=[)\]}])", "");
            t = Regex.Replace(t, @"([([{])\s+", "$1");
            t = Regex.Replace(t, @"[-_+]mc$", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"([-_+]|\s+)(for|minecraft)$", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\s{2,}", " ").Trim();
            t = t.Trim(' ', '+', '-', '_', '.', ',');
            if (t == s) break;
            s = t;
        }
        return s.Length >= 2 ? s : label;
    }

    private static string StripExt(string name) =>
        name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name) : name;

    private static string Take(IEnumerable<string>? items, int max) =>
        string.Join(", ", (items ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Take(max));

    private static string CapAll(List<string>? items, int max) =>
        Take((items ?? new List<string>()).ConvertAll(BrowseCard.Cap), max);

    private static string Parts(params string[] parts) =>
        string.Join("  ·  ", parts.Where(p => p.Length > 0));
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
        LoaderPick.ItemsSource = Loaders;
        SyncSearchHint();
        VerPick.SelectionChanged += (_, _) => { if (!_syncing) SearchFresh(); };
        LoaderPick.SelectionChanged += (_, _) => { if (!_syncing) SearchFresh(); };
        BuildEdgeFade();
    }

    private static readonly string[] Loaders = { "Vanilla", "Fabric", "Quilt", "Forge", "NeoForge" };

    private List<string> _mcVersions = new();
    private bool _syncing;
    private const int PageSize = 20;
    private int _page = 1;

    private void SearchFresh()
    {
        _page = 1;
        DoSearch();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _page--;
        DoSearch();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        _page++;
        DoSearch();
    }

    private const double EdgeFade = 12;
    private readonly GradientStop _topFade = new() { Color = Colors.Black, Offset = 0 };
    private readonly GradientStop _botFade = new() { Color = Colors.Black, Offset = 1 };

    private void BuildEdgeFade()
    {
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(_topFade);
        mask.GradientStops.Add(_botFade);
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        ResultsScroll.OpacityMask = mask;
    }

    private void Results_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var h = ResultsScroll.ViewportHeight;
        if (h <= EdgeFade * 2)
        {
            _topFade.Offset = 0;
            _botFade.Offset = 1;
            return;
        }
        var f = EdgeFade / h;
        _topFade.Offset = ResultsScroll.VerticalOffset > 1 ? f : 0;
        _botFade.Offset = ResultsScroll.VerticalOffset < ResultsScroll.ExtentHeight - h - 1 ? 1 - f : 1;
    }

    private void SyncPicks()
    {
        if (TargetInstance.SelectedItem is not Instance inst) return;
        _syncing = true;
        var mc = inst.Config.MCVersion;
        if (!string.IsNullOrEmpty(mc))
        {
            var list = _mcVersions;
            if (!list.Contains(mc)) list = list.Prepend(mc).ToList();
            VerPick.ItemsSource = list;
            VerPick.SelectedItem = mc;
        }
        LoaderPick.SelectedItem = inst.Config.Loader.ToString();
        _syncing = false;
    }

    private void ApplyVersionList()
    {
        var keep = VerPick.SelectedItem as string
                   ?? (TargetInstance.SelectedItem as Instance)?.Config.MCVersion;
        var list = _mcVersions;
        if (!string.IsNullOrEmpty(keep) && !list.Contains(keep)) list = list.Prepend(keep).ToList();
        _syncing = true;
        VerPick.ItemsSource = list;
        if (!string.IsNullOrEmpty(keep)) VerPick.SelectedItem = keep;
        _syncing = false;
    }

    private async Task LoadVersionListAsync()
    {
        if (_mcVersions.Count > 0) return;
        try
        {
            var manifest = await AppServices.Minecraft.GetManifestAsync();
            _mcVersions = manifest.versions.Where(v => v.type == "release").Select(v => v.id).ToList();
            ApplyVersionList();
        }
        catch { }
    }

    private string FilterMc() =>
        VerPick.SelectedItem as string ?? (TargetInstance.SelectedItem as Instance)?.Config.MCVersion ?? "";

    private string FilterLoader() =>
        LoaderPick.SelectedItem as string ?? (TargetInstance.SelectedItem as Instance)?.Config.Loader.ToString() ?? "Fabric";

    private static string TLabel(ContentType t) => t switch
    {
        ContentType.Shader => "Shaders",
        ContentType.ResourcePack => "Resource packs",
        ContentType.DataPack => "Data packs",
        ContentType.Modpack => "Modpacks",
        _ => "Mods",
    };

    private void SyncSearchHint() => SearchBox.Tag = "Search " + TLabel(CurrentType).ToLowerInvariant() + "…";

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
            TargetInstance.SelectionChanged += (_, _) => { SyncPicks(); SearchFresh(); };
        }
        SyncPicks();
        _ = LoadVersionListAsync();
        if (Results.ItemsSource == null) SearchFresh();
    }

    private ContentType CurrentType => Types[Math.Max(0, TypeBox.SelectedIndex)];
    private bool OnCurseForge => SourceBox.SelectedIndex == 1;
    private string StateKey() =>
        $"{(TargetInstance.SelectedItem as Instance)?.Config.Id}|{(int)CurrentType}|{SourceBox.SelectedIndex}" +
        $"|{VerPick.SelectedItem}|{LoaderPick.SelectedItem}|{_page}";

    private void Type_Changed(object sender, SelectionChangedEventArgs e)
    {
        SyncSearchHint();
        if (IsLoaded) SearchFresh();
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SearchFresh();
    }

    private CancellationTokenSource? _debounce;

    private async void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        var tok = _debounce.Token;
        try { await Task.Delay(350, tok); } catch (OperationCanceledException) { return; }
        SearchFresh();
    }

    private async void DoSearch()
    {
        var inst = TargetInstance.SelectedItem as Instance;
        var query = SearchBox.Text.Trim();
        var started = StateKey();
        Busy.Visibility = Visibility.Visible;
        Hint.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        ResultBar.Visibility = Visibility.Collapsed;
        Pager.Visibility = Visibility.Collapsed;
        ResultsScroll.Visibility = Visibility.Collapsed;
        try
        {
            var type = CurrentType;
            var pack = type == ContentType.Modpack;
            await LoadVersionListAsync();
            var mc = pack ? null : FilterMc();
            var loader = pack ? null : FilterLoader();
            List<BrowseCard> cards;
            bool more;
            if (OnCurseForge)
            {
                var (mods, raw) = await CurseForgeService.SearchAsync(query, type, mc, loader, PageSize, _page);
                var picked = await Task.WhenAll(mods.Select(async m =>
                {
                    var best = CurseForgeService.PickFile(m, loader, type, mc);
                    if (best == null && !pack)
                        best = CurseForgeService.PickFrom(await CurseForgeService.GetFilesAsync(m.id, gameVersion: mc),
                            loader, type, mc);
                    return (m, best);
                }));
                cards = picked.Where(t => t.best != null)
                    .Select(t => BrowseCard.From(t.m, mc ?? "", loader ?? "", t.best!)).ToList();
                more = raw == PageSize;
            }
            else
            {
                var (hits, _) = await ModrinthService.SearchAsync(query, type, mc, loader,
                    PageSize, (_page - 1) * PageSize);
                cards = hits.Select(BrowseCard.From).ToList();
                more = cards.Count == PageSize;
            }
            if (started != StateKey()) return;
            var state = StateKey();
            if (inst != null && type != ContentType.Modpack)
            {
                var sets = await Task.Run(() => ContentMatch.ScanDir(ModrinthService.DirFor(inst, type)));
                if (state != StateKey()) return;
                foreach (var c in cards) c.Installed = ContentMatch.Any(c.Title, c.Slug, sets);
            }
            Results.ItemsSource = cards;
            ResultsScroll.Visibility = Visibility.Visible;
            ResultsScroll.ScrollToTop();
            Hint.Text = "Nothing found.";
            var n = cards.Count;
            if (n > 0)
            {
                Hint.Visibility = Visibility.Collapsed;
                ResultBar.Visibility = Visibility.Visible;
                ResultType.Text = TLabel(type);
                var filtered = inst != null && !pack;
                VerPick.Visibility = LoaderPick.Visibility = filtered ? Visibility.Visible : Visibility.Collapsed;
            }
            else ResultBar.Visibility = Visibility.Collapsed;
            Pager.Visibility = _page > 1 || more ? Visibility.Visible : Visibility.Collapsed;
            PageLabel.Text = $"Page {_page}";
            PrevBtn.IsEnabled = _page > 1;
            NextBtn.IsEnabled = more;
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
        await InstallAsync(card);
    }

    private async Task InstallAsync(BrowseCard card)
    {
        if (card.Installing) return;
        var type = CurrentType;
        if (type == ContentType.Modpack)
        {
            card.Installing = true;
            MainWindow.Main!.SetProgress($"Downloading modpack {card.Title}…");
            try
            {
                var iconTask = BrowseCard.IconBytesAsync(card.IconUrl);
                var file = card.Cf != null
                    ? await CurseForgeService.DownloadModpackAsync(card.Cf.id, card.Title,
                        new Progress<string>(s => MainWindow.Main!.SetProgress(s)), CfFileId(card))
                    : await ModrinthService.DownloadModpackAsync(card.Hit!.project_id, card.Title,
                        new Progress<string>(s => MainWindow.Main!.SetProgress(s)), card.Pinned?.Id);
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
            finally
            {
                card.Installing = false;
            }
            return;
        }
        if (TargetInstance.SelectedItem is not Instance inst)
        {
            MessageBox.Show(Window.GetWindow(this), "Create/select an instance first.", "Lumen");
            return;
        }
        card.Installing = true;
        MainWindow.Main!.SetProgress($"Installing {card.Title} into {inst.Config.Name}…");
        try
        {
            var mc = FilterMc();
            var pickedLoader = FilterLoader();
            var loader = pickedLoader == "Vanilla" ? "fabric" : pickedLoader;
            if (card.Cf != null)
                await CurseForgeService.InstallAsync(inst, card.Cf, mc, loader, type, CfFileId(card));
            else
                await ModrinthService.InstallAsync(inst, card.Hit!, mc, loader, type, card.Pinned?.Id);
            card.Installed = true;
            MainWindow.Main!.SetIdle();
        }
        catch (Exception ex)
        {
            MainWindow.Main!.SetIdle();
            MessageBox.Show(Window.GetWindow(this), ex.Message, "Lumen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            card.Installing = false;
        }
    }

    private static long CfFileId(BrowseCard card) =>
        card.Pinned != null && long.TryParse(card.Pinned.Id, out var id) ? id : 0;

    private BrowseCard? _menuCard;

    private string ComboKey() => $"{FilterMc()}|{FilterLoader()}|{(int)CurrentType}";

    private async void Versions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BrowseCard card }) return;
        _menuCard = card;
        VersionPopup.PlacementTarget = sender as UIElement;
        VersionPopup.IsOpen = true;
        var key = ComboKey();
        if (card.Files != null && card.FilesKey == key)
        {
            ShowRows(card);
            return;
        }
        VersionList.ItemsSource = null;
        VersionBusy.Text = "Loading…";
        VersionBusy.Visibility = Visibility.Visible;
        try
        {
            card.Files = await LoadVersionsAsync(card);
            card.FilesKey = key;
        }
        catch (Exception ex)
        {
            VersionBusy.Text = "Could not load versions: " + ex.Message;
            return;
        }
        if (!ReferenceEquals(_menuCard, card) || !VersionPopup.IsOpen) return;
        ShowRows(card);
    }

    private void ShowRows(BrowseCard card)
    {
        var files = card.Files ?? new();
        if (files.Count == 0)
        {
            VersionList.ItemsSource = null;
            VersionBusy.Text = CurrentType == ContentType.Modpack
                ? "No files found."
                : $"No files for {FilterMc()} ({FilterLoader()}).";
            VersionBusy.Visibility = Visibility.Visible;
            return;
        }
        VersionBusy.Visibility = Visibility.Collapsed;
        VersionList.ItemsSource = new List<VersionRow>
        {
            new() { Id = "", Label = "Latest (auto)", Meta = files[0].Label, Picked = card.Pinned == null },
        }.Concat(files.Select(f => f.Pick(card.Pinned?.Id == f.Id))).ToList();
    }

    private async Task<List<VersionRow>> LoadVersionsAsync(BrowseCard card)
    {
        var pack = CurrentType == ContentType.Modpack;
        var mc = pack ? "" : FilterMc();
        var picked = FilterLoader();
        var loader = pack || picked == "Vanilla" ? "" : picked;
        bool Fits(List<string>? values, string wanted) => wanted.Length == 0 || McVersions.Matches(values, wanted);
        bool needsLoader = CurrentType == ContentType.Mod;

        if (card.Cf != null)
        {
            var files = await CurseForgeService.GetFilesAsync(card.Cf.id, gameVersion: mc);
            return files
                .Where(f => !pack || f.fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .Where(f => Fits(f.gameVersions, mc) &&
                            (!needsLoader || Fits(f.gameVersions, loader)))
                .Select(f => VersionRow.From(f, mc, loader)).ToList();
        }
        var versions = await ModrinthService.GetVersionsAsync(card.Hit!.project_id);
        bool IsPack(ProjectVersion v) => v.files!.Any(f =>
            f.filename.EndsWith(".mr", StringComparison.OrdinalIgnoreCase) ||
            f.filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase));
        return versions
            .Where(v => v.files?.Count > 0)
            .Where(v => !pack || IsPack(v))
            .Where(v => Fits(v.game_versions, mc) && (!needsLoader || Fits(v.loaders, loader)))
            .OrderByDescending(v => v.date_published)
            .Select(VersionRow.From).ToList();
    }

    private async void VersionRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VersionRow row } || _menuCard == null) return;
        var card = _menuCard;
        card.Pinned = row.Id.Length == 0 ? null : card.Files?.FirstOrDefault(f => f.Id == row.Id);
        VersionPopup.IsOpen = false;
        await InstallAsync(card);
    }
}
