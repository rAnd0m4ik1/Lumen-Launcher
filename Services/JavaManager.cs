using System.IO;
using System.IO.Compression;
using System.Net.Http;
using CraftDeck.Models;

namespace CraftDeck.Services;

public static class JavaManager
{
    public static async Task<string> EnsureJavaAsync(int requiredMajor, AppSettings settings,
        IProgress<string>? status = null, CancellationToken ct = default)
    {
        var existing = JavaLocator.Find(null, requiredMajor);
        if (existing != null && JavaLocator.JavaMajorVersion(existing) >= requiredMajor) return existing;

        var feature = FeatureFor(requiredMajor);

        var javaRoot = settings.JavaDir;
        Directory.CreateDirectory(javaRoot);
        var target = Path.Combine(javaRoot, $"temurin-{feature}");
        var marker = Path.Combine(target, ".download-ok");
        if (!File.Exists(marker))
        {
            status?.Report($"No suitable Java found — downloading Java {feature} (Temurin)…");
            var zip = Path.Combine(javaRoot, $"temurin-{feature}.zip");
            var url = $"https://api.adoptium.net/v3/binary/latest/{feature}/ga/windows/x64/jdk/hotspot/normal/eclipse";
            using (var resp = await Http.Stream.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                using var dst = File.Create(zip);
                await src.CopyToAsync(dst, 81920, ct);
                await dst.FlushAsync(ct);
            }
            status?.Report("Extracting Java…");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.CreateDirectory(target);
            using (var archive = ZipFile.OpenRead(zip))
                archive.ExtractToDirectory(target);
            File.Delete(zip);
            File.WriteAllText(marker, DateTime.Now.ToString("O"));
        }

        var exe = FindExe(target) ?? throw new Exception($"Java {feature} download failed or is incomplete.");
        status?.Report($"Java {feature} installed into {target}");
        return exe;
    }

    private static int FeatureFor(int requiredMajor) => requiredMajor switch
    {
        <= 8 => 8,
        <= 11 => 11,
        <= 17 => 17,
        <= 21 => 21,
        _ => requiredMajor,
    };

    private static string? FindExe(string root)
    {
        foreach (var exeName in new[] { "javaw.exe", "java.exe" })
        {
            var direct = Path.Combine(root, "bin", exeName);
            if (File.Exists(direct)) return direct;
            foreach (var sub in Directory.EnumerateDirectories(root))
            {
                var nested = Path.Combine(sub, "bin", exeName);
                if (File.Exists(nested)) return nested;
            }
        }
        return null;
    }
}
