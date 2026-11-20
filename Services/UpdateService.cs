using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace CraftDeck.Services;

public record UpdateRelease(string Tag, string? ZipUrl, string? ZipSha, long ZipSize,
                            string? InstallerUrl, string? InstallerSha, long InstallerSize);

public static class UpdateService
{
    private const string Repo = "rAnd0m4ik1/Lumen-Launcher";

    public static event Action? Changed;

    public static string CurrentTag { get; } =
        (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0)).ToString(3);

    public static UpdateRelease? Available { get; private set; }
    public static bool Busy { get; private set; }
    public static bool Staged { get; private set; }
    public static double Fraction { get; private set; }
    public static string Note { get; private set; } = "";

    public static string CacheDir => Path.Combine(Path.GetTempPath(), "LumenUpdate");
    public static string StagedDir => Path.Combine(CacheDir, "staged");
    public static string HelperDir => Path.Combine(CacheDir, "helper");
    public static string PrevDir => Path.Combine(AppContext.BaseDirectory, "_prev");
    public static string PendingFile => Path.Combine(CacheDir, "pending.txt");
    public static string OkFile => Path.Combine(CacheDir, "applied.ok");

    public static bool Blocked => RunningGames.AnyRunning;

    public static bool IsInstalledMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\8E1C0D6B-7F4A-4E2B-9C3D-5A6B7C8D9E0F_is1");
            var where = key?.GetValue("InstallLocation") as string;
            return where != null && SameDir(where, AppContext.BaseDirectory);
        }
        catch { return false; }
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                      Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);

    public static async Task<UpdateRelease?> FetchAsync(CancellationToken ct = default)
    {
        var url = Environment.GetEnvironmentVariable("LUMEN_UPDATE_API") ??
                  $"https://api.github.com/repos/{Repo}/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await Http.Client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var tag = Read(doc.RootElement, "tag_name");
        if (!IsNewer(tag, CurrentTag)) return null;

        string? zip = null, zipSha = null, inst = null, instSha = null;
        long zipSize = 0, instSize = 0;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = Read(a, "name");
                var digest = Read(a, "digest");
                var sha = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest[7..].Trim() : "";
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var l) ? l : 0;
                var dl = Read(a, "browser_download_url");
                if (dl.Length == 0) continue;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                { zip = dl; zipSha = sha; zipSize = size; }
                else if (name.EndsWith("-installer.exe", StringComparison.OrdinalIgnoreCase))
                { inst = dl; instSha = sha; instSize = size; }
            }

        if (zip == null && inst == null) return null;
        return new UpdateRelease(tag, zip, zipSha, zipSize, inst, instSha, instSize);
    }

    private static string Read(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static bool IsNewer(string tag, string current)
    {
        var a = Parse(tag); var b = Parse(current);
        return a != null && b != null && a > b;
    }

    private static Version? Parse(string tag)
    {
        var text = tag.TrimStart('v', 'V');
        var end = 0;
        while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
        return Version.TryParse(text[..end], out var v) ? v : null;
    }

    public static async Task AutoCheckAsync()
    {
        if (AppServices.Settings.LastUpdateCheck > DateTime.UtcNow - TimeSpan.FromHours(12)) return;
        await CheckAsync(report: false);
    }

    public static async Task<UpdateRelease?> CheckAsync(bool report = true)
    {
        if (Busy) return null;
        var rel = await FetchAsync();
        AppServices.Settings.LastUpdateCheck = DateTime.UtcNow;
        try { AppServices.Settings.Save(); } catch { }

        var skipped = rel != null && string.Equals(AppServices.Settings.SkipUpdateTag, rel.Tag,
                                                   StringComparison.OrdinalIgnoreCase);
        Available = rel != null && (!skipped || report) ? rel : null;
        Staged = false;
        Fraction = 0;
        Note = rel == null ? "Up to date" : "";
        Raise();
        return rel;
    }

    public static async Task StageAsync(CancellationToken ct = default)
    {
        var rel = Available ?? throw new InvalidOperationException("no update available");
        var installer = IsInstalledMode() && rel.InstallerUrl != null;
        var url = installer ? rel.InstallerUrl : rel.ZipUrl;
        if (url == null) throw new IOException("The release has no matching download.");

        Busy = true; Fraction = 0; Note = "Downloading…"; Raise();
        try
        {
            Directory.CreateDirectory(CacheDir);
            var target = Path.Combine(CacheDir, Path.GetFileName(new Uri(url).LocalPath));
            await DownloadAsync(url, target, installer ? rel.InstallerSize : rel.ZipSize,
                                installer ? rel.InstallerSha : rel.ZipSha, ct);
            if (!installer) Extract(target);
            StageHelper();
            Staged = true;
            Note = "Ready";
        }
        finally { Busy = false; Raise(); }
    }

    private static async Task DownloadAsync(string url, string target, long size, string? sha256,
                                            CancellationToken ct)
    {
        var tmp = target + ".part";
        using var resp = await Http.Stream.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (size <= 0) size = resp.Content.Headers.ContentLength ?? 0;

        long done = 0;
        var buf = new byte[81920];
        using (var src = await resp.Content.ReadAsStreamAsync(ct))
        {
            await using (var dst = File.Create(tmp))
            {
                while (true)
                {
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(TimeSpan.FromSeconds(30));
                    int n;
                    try { n = await src.ReadAsync(buf, readCts.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new IOException($"Stalled while reading {url}");
                    }
                    if (n <= 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (size > 0) Fraction = Math.Min(0.95, (double)done / size);
                    if (done % 262144 < n) Raise();
                }
                await dst.FlushAsync(ct);
            }
        }

        if (size > 0 && new FileInfo(tmp).Length != size)
            throw new IOException($"size mismatch, got {new FileInfo(tmp).Length} of {size} bytes");
        if (!string.IsNullOrEmpty(sha256))
        {
            Note = "Verifying…"; Raise();
            using var sha = SHA256.Create();
            await using (var fs = File.OpenRead(tmp))
            {
                int n;
                while ((n = await fs.ReadAsync(buf, ct)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
            }
            sha.TransformFinalBlock(buf, 0, 0);
            var got = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (got != sha256!.ToLowerInvariant()) throw new IOException($"checksum mismatch, got {got}");
        }
        File.Move(tmp, target, true);
    }

    private static void StageHelper()
    {
        if (Directory.Exists(HelperDir)) Directory.Delete(HelperDir, true);
        Directory.CreateDirectory(HelperDir);
        foreach (var f in Directory.GetFiles(StagedDir))
        {
            var name = Path.GetFileName(f);
            var cur = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(cur)) File.Copy(cur, Path.Combine(HelperDir, name), true);
        }
    }

    private static void Extract(string zipPath)
    {
        if (Directory.Exists(StagedDir)) Directory.Delete(StagedDir, true);
        Directory.CreateDirectory(StagedDir);
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(x => x.FullName.Replace('\\', '/')).ToList();
        var prefix = CommonRoot(names);
        for (int i = 0; i < zip.Entries.Count; i++)
        {
            var entry = zip.Entries[i];
            var name = names[i];
            if (name.Length == 0 || name.EndsWith("/")) continue;
            if (name.Contains("..") || Path.IsPathRooted(name))
                throw new IOException($"unsafe entry {entry.FullName}");
            if (prefix.Length > 0 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                name = name[prefix.Length..];
            if (name.Length == 0 || name.EndsWith("/")) continue;
            var dest = Path.Combine(StagedDir, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }
    }

    private static string CommonRoot(IReadOnlyList<string> names)
    {
        var first = names.FirstOrDefault(x => x.Contains('/'));
        if (first == null) return "";
        var root = first[..(first.IndexOf('/') + 1)];
        return names.All(x => x.StartsWith(root, StringComparison.OrdinalIgnoreCase)) ? root : "";
    }

    public static void RestartToApply()
    {
        var rel = Available;
        if (rel == null || !Staged) return;
        var exe = Path.Combine(AppContext.BaseDirectory, "Lumen.exe");
        var helper = Path.Combine(HelperDir, "Lumen.exe");
        if (!File.Exists(helper)) helper = exe;
        var root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string args;
        if (IsInstalledMode() && rel.InstallerUrl != null)
        {
            var file = Path.Combine(CacheDir, Path.GetFileName(new Uri(rel.InstallerUrl).LocalPath));
            args = $"--installer {Environment.ProcessId} \"{file}\" \"{root}\" \"{rel.Tag}\"";
        }
        else args = $"--update {Environment.ProcessId} \"{StagedDir}\" \"{root}\" \"{rel.Tag}\"";
        Process.Start(new ProcessStartInfo(helper, args)
        {
            WorkingDirectory = HelperDir,
            UseShellExecute = false,
        });
        Raise();
    }

    private static void Raise() => Changed?.Invoke();
}
