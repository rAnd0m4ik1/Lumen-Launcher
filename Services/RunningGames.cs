using System.Diagnostics;
using System.Windows;
using CraftDeck.Models;

namespace CraftDeck.Services;

public sealed class GameLog
{
    private const int Cap = 20_000;
    private readonly List<string> _lines = new();
    public event Action<string>? LineAppended;

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
            if (_lines.Count > Cap) _lines.RemoveRange(0, _lines.Count - Cap);
        }
        LineAppended?.Invoke(line);
    }

    public string[] Snapshot() { lock (_lines) return _lines.ToArray(); }
}

public sealed class GameSession
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private bool _finished;

    public Instance Inst { get; init; } = null!;
    public Process Proc { get; init; } = null!;
    public GameLog Log { get; init; } = new();
    public bool Running { get; private set; } = true;
    public bool StopRequested { get; private set; }
    public int ExitCode { get; private set; }
    public TimeSpan Elapsed => _watch.Elapsed;

    public void Stop()
    {
        if (!Running) return;
        StopRequested = true;
        try { if (!Proc.HasExited) Proc.Kill(true); } catch { }
    }

    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        Running = false;
        try { ExitCode = Proc.ExitCode; } catch { }
        Inst.Config.PlaytimeSeconds += _watch.Elapsed.Seconds;
        Inst.Config.LastPlayed = DateTime.Now;
        AppServices.Instances.Save(Inst);
    }
}

public static class RunningGames
{
    private static readonly Dictionary<string, GameSession> Sessions = new();

    public static event Action? Changed;

    public static bool IsRunning(string id) => Sessions.TryGetValue(id, out var s) && s.Running;
    public static GameSession? Of(string id) => Sessions.TryGetValue(id, out var s) ? s : null;

    public static GameSession Track(Instance inst, Process proc, GameLog log)
    {
        var session = new GameSession { Inst = inst, Proc = proc, Log = log };
        Sessions[inst.Config.Id] = session;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => OnUi(() =>
        {
            session.Finish();
            MainWindow.Main?.SetIdle();
            Changed?.Invoke();
        });
        OnUi(() =>
        {
            MainWindow.Main?.SetIdle($"{inst.Config.Name} is running");
            Changed?.Invoke();
        });
        return session;
    }

    public static void Stop(string id)
    {
        if (!IsRunning(id)) return;
        Of(id)?.Stop();
        Changed?.Invoke();
    }

    private static void OnUi(Action a)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) a();
        else dispatcher.BeginInvoke(a);
    }
}
