using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;

namespace CraftDeck.Services;

public record DownloadItem(string Url, string FilePath, long ExpectedSize = 0, Action<string>? OnDone = null,
                           string? Sha1 = null);

public class DownloadProgress
{
    public int Total;
    public int Completed;
    public long BytesDone;
    public long BytesTotal;
    public string CurrentFile = "";
}

public class Downloader
{
    private readonly int _maxParallel;
    public event Action<DownloadProgress>? ProgressChanged;

    public Downloader(int maxParallel = 8) => _maxParallel = maxParallel;

    public async Task RunAsync(IReadOnlyList<DownloadItem> items, CancellationToken ct = default)
    {
        var unique = items.DistinctBy(it => it.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        var pending = new List<DownloadItem>();
        foreach (var it in unique)
        {
            if (NeedsDownload(it.FilePath, it.ExpectedSize)) pending.Add(it);
            else Interlocked.Increment(ref _prog.Completed);
        }
        _prog.Total = unique.Count;
        _prog.BytesTotal = pending.Sum(it => Math.Max(0, it.ExpectedSize));
        if (pending.Count == 0) { Report(); return; }

        using var sem = new SemaphoreSlim(_maxParallel);
        var failures = new ConcurrentBag<string>();
        var tasks = pending.Select(async it =>
        {
            await sem.WaitAsync(ct);
            try
            {
                try
                {
                    await DownloadOne(it, ct);
                    it.OnDone?.Invoke(it.FilePath);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(it.FilePath)} — {ex.Message}");
                }
                Interlocked.Increment(ref _prog.Completed);
                Report();
            }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
        if (failures.Count > 0)
        {
            var detail = string.Join("; ", failures.Take(3));
            throw new IOException($"{failures.Count} file(s) could not be downloaded: {detail}" +
                                  (failures.Count > 3 ? " …" : ""));
        }
    }

    private readonly DownloadProgress _prog = new();

    private static bool NeedsDownload(string path, long expectedSize)
    {
        if (!File.Exists(path)) return true;
        if (expectedSize > 0)
        {
            try { return new FileInfo(path).Length != expectedSize; }
            catch { return true; }
        }
        return new FileInfo(path).Length == 0;
    }

    private static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);

    private async Task DownloadOne(DownloadItem it, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(it.FilePath)!);
        var tmp = it.FilePath + ".part";
        var lastError = (Exception?)null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await Http.Stream.GetAsync(it.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                long done;
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                {
                    _prog.CurrentFile = Path.GetFileName(it.FilePath);
                    Report();
                    using var dst = File.Create(tmp);
                    var buf = new byte[81920];
                    done = 0; int n;
                    while (true)
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readCts.CancelAfter(ReadIdleTimeout);
                        try { n = await src.ReadAsync(buf, readCts.Token); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                        {
                            throw new IOException($"Stalled while reading {it.Url}");
                        }
                        if (n <= 0) break;
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        if (done % 262144 < n) { Interlocked.Add(ref _prog.BytesDone, 262144); Report(); }
                    }
                    await dst.FlushAsync(ct);
                }
                Interlocked.Add(ref _prog.BytesDone, done % 262144);
                if (it.ExpectedSize > 0 && new FileInfo(tmp).Length != it.ExpectedSize)
                    throw new IOException($"size mismatch, got {new FileInfo(tmp).Length} of {it.ExpectedSize} bytes");
                if (it.Sha1 is { Length: > 0 } && Sha1Of(tmp) != it.Sha1.ToLowerInvariant())
                    throw new IOException("checksum mismatch");
                File.Move(tmp, it.FilePath, true);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                lastError = ex;
                TryDelete(tmp);
                if (attempt < 2) await Task.Delay(1000 * (attempt + 1), ct);
            }
        }
        TryDelete(tmp);
        throw lastError ?? new IOException($"Could not download {it.Url}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string Sha1Of(string path)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private void Report() => ProgressChanged?.Invoke(_prog);
}
