using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using CraftDeck.Models;
using CraftDeck.Models.Minecraft;

namespace CraftDeck.Services;

public record LaunchPlan(string JavaExe, List<string> JvmArgs, List<string> GameArgs, string WorkingDir);

public class LaunchService
{
    private readonly AppSettings _settings;
    private readonly MinecraftService _mc;

    public LaunchService(AppSettings settings, MinecraftService mc)
    {
        _settings = settings;
        _mc = mc;
    }

    public async Task<VersionDetails> EnsureInstalledAsync(Instance inst, IProgress<string>? status = null,
        CancellationToken ct = default)
    {
        VersionDetails? details = inst.Installed ? TryReadVersionJson(inst) : null;
        if (details == null)
        {
            status?.Report($"Fetching metadata for {inst.Config.MCVersion}…");
            var vanilla = await _mc.GetVersionDetailsAsync(inst.Config.MCVersion);
            if (inst.Config.Loader == LoaderKind.Vanilla)
            {
                details = vanilla;
                Directory.CreateDirectory(inst.Dir);
                File.WriteAllText(inst.VersionJsonPath, Json.Save(details));
            }
            else
            {
                status?.Report($"Installing {inst.Config.Loader} {inst.Config.LoaderVersion}…");
                details = await LoaderService.InstallAsync(inst, vanilla, _settings, status, ct);
            }
        }

        Directory.CreateDirectory(inst.GameDir);
        Directory.CreateDirectory(inst.ModsDir);
        Directory.CreateDirectory(inst.NativesDir);

        status?.Report("Downloading game files…");
        var pbar = new Progress<double>();
        await _mc.PrepareVanillaAsync(inst.Config.MCVersion, pbar, ct);

        status?.Report("Downloading loader libraries…");
        var items = new List<DownloadItem>();
        foreach (var lib in (details.libraries ?? new()).Where(l => l.Applies))
            items.AddRange(MinecraftService.LibraryDownloads(lib, _settings.LibrariesDir));
        await new Downloader(_settings.DownloadThreads).RunAsync(items, ct);

        status?.Report("Downloading assets…");
        await _mc.PrepareAssetsAsync(details, null, ct);
        return details;
    }

    private static VersionDetails? TryReadVersionJson(Instance inst)
    {
        try
        {
            var d = Json.Load<VersionDetails>(File.ReadAllText(inst.VersionJsonPath));
            return d?.mainClass != null && d.libraries != null ? d : null;
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException) { return null; }
    }

    public LaunchPlan BuildPlan(Instance inst, VersionDetails details, string? javaOverride = null)
    {
        var features = new Dictionary<string, bool>
        {
            ["has_custom_resolution"] = false,
            ["is_demo_user"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false,
            ["is_quick_play_singleplayer"] = false,
        };

        var libs = (details.libraries ?? new List<Library>()).Where(l => l.Applies).ToList();

        var cp = new List<string>();
        var nativeJars = new List<string>();
        foreach (var lib in libs)
        {
            var parts = lib.name!.Split(':');
            bool isNativeClassifier = parts.Length >= 4 && parts[3].StartsWith("natives-windows");
            bool legacyNative = lib.natives?.windows != null;
            if (isNativeClassifier && !legacyNative)
            {
                cp.Add(AbsPathFor(lib));
                nativeJars.Add(AbsPathFor(lib));
                continue;
            }
            if (legacyNative)
            {
                nativeJars.Add(AbsPathFor(lib, native: true));
                continue;
            }
            cp.Add(AbsPathFor(lib));
        }
        var mcJar = File.Exists(_mc.ClientJarPath(inst.Config.MCVersion))
            ? _mc.ClientJarPath(inst.Config.MCVersion)
            : Path.Combine(_settings.VersionsDir, inst.Config.MCVersion, inst.Config.MCVersion + ".jar");
        cp.Add(mcJar);
        var classpath = string.Join(Path.PathSeparator, cp);

        Directory.CreateDirectory(inst.NativesDir);
        foreach (var jar in nativeJars)
            if (File.Exists(jar)) ExtractZip(jar, inst.NativesDir);

        var token = _settings.AccountType == "Microsoft" ? _settings.AccessToken ?? "0" : "0";
        var uuid = _settings.AccountType == "Microsoft" && _settings.UUID != null
            ? _settings.UUID : OfflineUuid(_settings.Username);

        var subs = new Dictionary<string, string>
        {
            ["auth_player_name"] = _settings.Username,
            ["version_name"] = inst.Config.MCVersion,
            ["game_directory"] = inst.GameDir,
            ["assets_root"] = _settings.AssetsDir,
            ["assets_index_name"] = details.assets ?? "legacy",
            ["auth_uuid"] = uuid.Replace("-", ""),
            ["auth_access_token"] = token,
            ["auth_xuid"] = "0",
            ["clientid"] = "0",
            ["auth_session"] = token,
            ["user_type"] = _settings.AccountType == "Microsoft" ? "msa" : "legacy",
            ["version_type"] = details.type ?? "release",
            ["launcher_name"] = "Lumen",
            ["launcher_version"] = "1.0",
            ["natives_directory"] = inst.NativesDir,
            ["classpath"] = classpath,
            ["user_properties"] = "{}",
            ["resolution_width"] = "854",
            ["resolution_height"] = "480",
            ["game_assets"] = _settings.AssetsDir,
            ["secondary_launcher_version"] = "1.0",
        };

        var jvm = new List<string>();
        var game = new List<string>();
        bool hasX = false, hasClasspath = false, hasUsername = false;

        if (details.arguments.ValueKind == JsonValueKind.Object &&
            details.arguments.TryGetProperty("jvm", out var jvmEl))
        {
            foreach (var a in FlattenArgs(jvmEl, features))
            {
                var s = Sub(a, subs);
                if (s.Contains("-Xmx")) hasX = true;
                if (s == "-cp" || s == "-classpath" || s.StartsWith("-Djava.class.path")) hasClasspath = true;
                jvm.Add(s);
            }
        }
        if (details.arguments.ValueKind == JsonValueKind.Object &&
            details.arguments.TryGetProperty("game", out var gameEl))
        {
            foreach (var a in FlattenArgs(gameEl, features))
            {
                var s = Sub(a, subs);
                if (s == "--username") hasUsername = true;
                game.Add(s);
            }
        }
        else if (!string.IsNullOrEmpty(details.minecraftArguments))
        {
            foreach (var a in details.minecraftArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var s = Sub(a, subs);
                if (s == "--username") hasUsername = true;
                game.Add(s);
            }
        }

        var maxMb = inst.Config.MaxMemoryMb ?? _settings.MaxMemoryMb;

        var defaults = new List<string>();
        if (details.arguments.ValueKind == JsonValueKind.Object &&
            details.arguments.TryGetProperty("default-user-jvm", out var duj) &&
            duj.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in FlattenArgs(duj, features))
            {
                var s = Sub(a, subs);
                if (s.StartsWith("-Xms") || s.StartsWith("-Xmx")) continue;
                defaults.Add(s);
            }
        }
        if (!hasX)
            defaults.Insert(0, $"-Xmx{maxMb}M");
        if (!hasX && _settings.MinMemoryMb > 0)
            defaults.Insert(0, $"-Xms{_settings.MinMemoryMb}M");
        if (defaults.Count > 0)
            jvm.InsertRange(0, defaults);

        if (!hasClasspath)
            jvm.Add($"-Djava.class.path={classpath}");
        if (!hasUsername)
        {
            game.AddRange(new[] { "--username", _settings.Username, "--uuid", uuid.Replace("-", "") });
        }
        if (!string.IsNullOrEmpty(_settings.JVMArguments))
            jvm.AddRange(SplitArgs(_settings.JVMArguments));
        if (!string.IsNullOrEmpty(inst.Config.ExtraJvmArgs))
            jvm.AddRange(SplitArgs(inst.Config.ExtraJvmArgs));
        if (!string.IsNullOrEmpty(_settings.GameArguments))
            game.AddRange(SplitArgs(_settings.GameArguments));

        var minMajor = JavaLocator.RequiredMajor(details);
        var java = javaOverride ?? JavaLocator.Find(inst.Config.JavaPath ?? _settings.JavaPath, minMajor);
        if (java == null)
            throw new Exception($"Java {minMajor}+ is required for this version but was not found.");

        return new LaunchPlan(java, jvm, new List<string> { details.mainClass ?? "" }.Where(s => s != "").Concat(BuildMainArgs(details, subs)).Concat(game).ToList(), inst.GameDir);
    }

    private static IEnumerable<string> BuildMainArgs(VersionDetails d, Dictionary<string, string> subs)
    {
        yield break;
    }

    private string AbsPathFor(Library lib, bool native = false)
    {
        string Shared(string rel) =>
            Path.Combine(_settings.LibrariesDir, rel.Replace('/', Path.DirectorySeparatorChar));
        if (native)
        {
            var key = lib.natives?.windows?.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32");
            if (!string.IsNullOrEmpty(key))
            {
                var classifier = Shared(lib.MavenPathClassifier(key));
                if (File.Exists(classifier)) return classifier;
            }
        }
        return Shared(lib.MavenPath());
    }

    private static IEnumerable<string> FlattenArgs(JsonElement el, Dictionary<string, bool> features)
    {
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) { yield return item.GetString()!; continue; }
            if (item.ValueKind != JsonValueKind.Object) continue;
            bool allow = true;
            if (item.TryGetProperty("rules", out var rules))
            {
                allow = false;
                foreach (var rule in rules.EnumerateArray())
                {
                    var action = rule.TryGetProperty("action", out var a) ? a.GetString() : "";
                    bool match = true;
                    if (rule.TryGetProperty("os", out var os))
                    {
                        if (os.TryGetProperty("name", out var name) && name.GetString() != "windows") match = false;
                        if (os.TryGetProperty("arch", out var arch) && !ArchMatches(arch)) match = false;
                        if (os.TryGetProperty("versionRange", out var range) && !RangeAllows(range)) match = false;
                    }
                    if (rule.TryGetProperty("features", out var feats))
                    {
                        foreach (var f in feats.EnumerateObject())
                        {
                            if (!features.TryGetValue(f.Name, out var v) ||
                                v != (f.Value.ValueKind == JsonValueKind.True))
                                match = false;
                        }
                    }
                    if (match && action == "allow") allow = true;
                    if (match && action == "disallow") allow = false;
                }
            }
            if (!allow) continue;
            if (item.TryGetProperty("value", out var val))
            {
                if (val.ValueKind == JsonValueKind.String) yield return val.GetString()!;
                else foreach (var s in val.EnumerateArray()) if (s.ValueKind == JsonValueKind.String) yield return s.GetString()!;
            }
        }
    }

    private static bool RangeAllows(JsonElement range)
    {
        var current = Environment.OSVersion.Version;
        foreach (var bound in new[] { "min", "max" })
        {
            if (!range.TryGetProperty(bound, out var el) || el.ValueKind != JsonValueKind.String) continue;
            if (!Version.TryParse(el.GetString(), out var v)) continue;
            if (bound == "min" && current < v) return false;
            if (bound == "max" && current > v) return false;
        }
        return true;
    }

    private static bool ArchMatches(JsonElement arch) =>
        arch.ValueKind != JsonValueKind.String || arch.GetString() switch
        {
            "x86" => !Environment.Is64BitOperatingSystem,
            "x64" or "amd64" => Environment.Is64BitOperatingSystem,
            "arm64" or "aarch64" => RuntimeInformation.OSArchitecture == Architecture.Arm64,
            _ => true,
        };

    private static string Sub(string s, Dictionary<string, string> subs)
    {
        foreach (var (k, v) in subs)
            if (s.Contains("${" + k + "}")) s = s.Replace("${" + k + "}", v);
        return s;
    }

    public static List<string> SplitArgs(string line) =>
        System.Text.RegularExpressions.Regex.Matches(line, "\"[^\"]*\"|[^ ]+")
            .Select<Match, string>(m => m.Value.Trim('"')).ToList();

    private static void ExtractZip(string zipPath, string dest)
    {
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (IOException) { return; }
        using (zip)
        {
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name) || e.FullName.StartsWith("META-INF")) continue;
                var target = Path.Combine(dest, e.Name);
                if (!Path.GetFullPath(target).StartsWith(Path.GetFullPath(dest))) continue;
                if (File.Exists(target) && new FileInfo(target).Length == e.Length) continue;
                try { e.ExtractToFile(target, true); }
                catch (IOException) { }
            }
        }
    }

    public static string OfflineUuid(string username)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)(hash[6] & 0x0f | 0x40);
        hash[8] = (byte)(hash[8] & 0x3f | 0x80);
        return new Guid(hash).ToString();
    }

    public async Task<Process> LaunchAsync(Instance inst, LaunchPlan plan, GameLog log)
    {
        var allArgs = plan.JvmArgs.Concat(plan.GameArgs).ToList();
        var full = string.Join(" ", allArgs.Select(Quote));

        if (full.Length > 30_000 && JavaLocator.JavaMajorVersion(plan.JavaExe) >= 9)
            full = "@" + Quote(WriteArgFile(inst, allArgs));

        var psi = new ProcessStartInfo
        {
            FileName = plan.JavaExe,
            Arguments = full,
            WorkingDirectory = plan.WorkingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) log.Add(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) log.Add("[err] " + e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    private static string WriteArgFile(Instance inst, IEnumerable<string> args)
    {
        var path = Path.Combine(inst.Dir, "launch-args.txt");
        File.WriteAllText(path, string.Join("\n", args.Select(QuoteForArgFile)));
        return path;
    }

    private static string QuoteForArgFile(string a)
    {
        if (!a.Contains(' ') && !a.Contains('\t')) return a;
        return "\"" + a.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string Quote(string a) => a.Contains(' ') ? "\"" + a + "\"" : a;
}
