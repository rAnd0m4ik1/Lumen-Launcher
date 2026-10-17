using System.IO;

namespace CraftDeck.Services;

public static class JavaLocator
{
    public static string? Find(string? preferred, int requiredMajor = 0)
    {
        if (!string.IsNullOrEmpty(preferred) && File.Exists(preferred) &&
            (requiredMajor <= 0 || JavaMajorVersion(preferred) >= requiredMajor))
            return preferred;

        var candidates = new List<string>();
        void AddIfJava(string dir)
        {
            foreach (var exe in new[] { "javaw.exe", "java.exe" })
            {
                var full = Path.Combine(dir, "bin", exe);
                if (File.Exists(full)) candidates.Add(full);
            }
        }
        void Scan(string root, int depth = 1)
        {
            if (!Directory.Exists(root)) return;
            AddIfJava(root);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                AddIfJava(dir);
                if (depth > 1) Scan(dir, depth - 1);
            }
        }

        try { Scan(AppServices.Settings.JavaDir, 2); } catch { }
        Scan(Path.Combine(AppContext.BaseDirectory, "java"), 2);

        var env = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(env)) AddIfJava(env);

        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Scan(Path.Combine(pf, "Eclipse Adoptium"));
        Scan(Path.Combine(pf, "Java"));
        Scan(Path.Combine(pf, "Zulu"));
        Scan(Path.Combine(pf, "Microsoft"));
        Scan(Path.Combine(pf, "BellSoft"));
        Scan(Path.Combine(pf, "Amazon Corretto"));

        var mc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        foreach (var runtime in new[] { "java-runtime-delta", "java-runtime-gamma", "java-runtime-beta" })
            AddIfJava(Path.Combine(mc, "runtime", runtime));

        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir.Trim(), "javaw.exe"))
            .FirstOrDefault(File.Exists);
        if (onPath != null) candidates.Insert(0, onPath);

        candidates = candidates.Distinct().ToList();
        if (candidates.Count == 0) return null;

        var withVersion = candidates
            .Select(c => (path: c, major: JavaMajorVersion(c)))
            .Where(t => t.major > 0)
            .ToList();
        if (withVersion.Count == 0) return candidates[0];

        if (requiredMajor > 0)
        {
            var fitting = withVersion.Where(t => t.major >= requiredMajor)
                .OrderBy(t => t.major).ThenByDescending(t => t.path.Contains(AppContext.BaseDirectory))
                .FirstOrDefault();
            return fitting.path;
        }
        return withVersion.OrderByDescending(t => t.major).First().path;
    }

    public static int JavaMajorVersion(string javaExe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            var m = System.Text.RegularExpressions.Regex.Match(err, @"""(\d+)(?:\.(\d+))?");
            if (!m.Success) return 0;
            var major = int.Parse(m.Groups[1].Value);
            if (major == 1 && m.Groups[2].Success) major = int.Parse(m.Groups[2].Value);
            return major;
        }
        catch { return 0; }
    }

    public static int RequiredMajor(Models.Minecraft.VersionDetails v)
    {
        var major = v.javaVersion?.majorVersion ?? 0;
        if (major >= 8) return major;
        return 8;
    }
}
