using System.Diagnostics;
using System.IO;
using System.Threading;

namespace CraftDeck.Services;

public static class Updater
{
    private static string _root = "";
    private static readonly string Cache = UpdateService.CacheDir;

    public static bool RunIfRequested(string[] args)
    {
        if (args.Length == 0) return false;
        var mode = args[0];
        if (mode != "--update" && mode != "--installer") return false;
        try
        {
            if (args.Length < 5) throw new ArgumentException("too few arguments: " + string.Join(' ', args));
            if (!int.TryParse(args[1], out var pid)) throw new ArgumentException("bad pid: " + args[1]);
            _root = Path.GetFullPath(Clean(args[3]));
            var tag = Clean(args[4]);
            if (mode == "--installer") RunInstaller(Clean(args[2]), pid, tag);
            else Apply(Clean(args[2]), pid, tag);
        }
        catch (Exception ex) { Log("updater: " + ex); }
        return true;
    }

    private static string Clean(string value) => value.Trim('"').TrimEnd('\\');

    public static void MarkApplied()
    {
        try
        {
            var pend = Path.Combine(Cache, "pending.txt");
            if (!File.Exists(pend)) return;
            if (File.ReadAllText(pend).Trim() != UpdateService.CurrentTag) return;
            File.WriteAllText(Path.Combine(Cache, "applied.ok"), DateTime.Now.ToString("O"));
        }
        catch { }
    }

    private static void Apply(string staged, int pid, string tag)
    {
        if (!Directory.Exists(staged) || Directory.GetFiles(staged).Length == 0)
        {
            Log("nothing staged");
            Relaunch();
            return;
        }
        if (!WaitOld(pid))
        {
            Log("previous process still holds the files, update skipped");
            Relaunch();
            return;
        }

        var prev = Path.Combine(_root, "_prev");
        DeleteDir(prev);
        Directory.CreateDirectory(prev);
        var files = Directory.GetFiles(staged);
        foreach (var f in files)
        {
            var name = Path.GetFileName(f);
            var cur = Path.Combine(_root, name);
            if (File.Exists(cur) && !MoveQuietly(cur, Path.Combine(prev, name)))
                Log("could not back up " + name);
        }
        foreach (var f in files)
            File.Copy(f, Path.Combine(_root, Path.GetFileName(f)), true);

        File.WriteAllText(Path.Combine(Cache, "pending.txt"), tag);
        TryDelete(Path.Combine(Cache, "applied.ok"));
        Relaunch();

        if (WaitFor(Path.Combine(Cache, "applied.ok"), TimeSpan.FromSeconds(40)))
        {
            DeleteDir(prev);
            foreach (var old in Directory.GetFiles(_root, "*.old")) TryDelete(old);
            Log($"updated to {tag}");
        }
        else
        {
            Log($"version {tag} did not start, rolling back");
            foreach (var f in Directory.GetFiles(prev))
            {
                var name = Path.GetFileName(f);
                var cur = Path.Combine(_root, name);
                if (File.Exists(cur)) MoveQuietly(cur, cur + ".bad");
                try { File.Move(f, cur); } catch (Exception ex) { Log("restore " + name + ": " + ex.Message); }
            }
            Relaunch();
        }
        TryDelete(Path.Combine(Cache, "pending.txt"));
    }

    private static void RunInstaller(string file, int pid, string tag)
    {
        if (!File.Exists(file))
        {
            Log("installer missing");
            Relaunch();
            return;
        }
        if (!WaitOld(pid))
        {
            Log("previous process still holds the files, update skipped");
            Relaunch();
            return;
        }
        var p = Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-")
        {
            WorkingDirectory = Path.GetDirectoryName(file)!,
            UseShellExecute = false,
        });
        if (p == null) { Relaunch(); return; }
        if (!p.WaitForExit(300000))
        {
            try { p.Kill(true); } catch { }
            Log("installer timed out");
        }
        else Log($"installer for {tag} exited with {p.ExitCode}");
        Relaunch();
    }

    private static bool WaitOld(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(30000)) return false;
            Thread.Sleep(400);
            return true;
        }
        catch (ArgumentException) { return true; }
        catch (Exception ex) { Log("wait: " + ex.Message); return false; }
    }

    private static void Relaunch()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(_root, "Lumen.exe"))
            {
                WorkingDirectory = _root,
                UseShellExecute = false,
            });
        }
        catch (Exception ex) { Log("relaunch: " + ex.Message); }
    }

    private static bool MoveQuietly(string from, string to)
    {
        for (int i = 0; i < 5; i++)
        {
            try { File.Move(from, to, true); return true; }
            catch (IOException) { Thread.Sleep(400); }
            catch (Exception ex) { Log("move " + Path.GetFileName(from) + ": " + ex.Message); return false; }
        }
        return false;
    }

    private static bool WaitFor(string path, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (File.Exists(path)) return true;
            Thread.Sleep(200);
        }
        return File.Exists(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void DeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static void Log(string text)
    {
        var line = $"{DateTime.Now:O} {text}{Environment.NewLine}";
        foreach (var file in new[] { Path.Combine(Cache, "update.log"),
                                     _root.Length > 0 ? Path.Combine(_root, "lumen-error.log") : "" })
        {
            if (file.Length == 0) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, line);
            }
            catch { }
        }
    }
}
