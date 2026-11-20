using System.IO;
using System.Text.RegularExpressions;
using CraftDeck.Models;
using CraftDeck.Models.CurseForge;
using CraftDeck.Models.Modrinth;

namespace CraftDeck.Services;

public record ContentProject(string Title, Hit? Modrinth, CFMod? CurseForge,
    List<ProjectVersion>? Versions, List<CFFile>? Files, string? CurrentId)
{
    public bool OnCurseForge => CurseForge != null;
}

public static class ContentSource
{
    private const int Candidates = 4;
    private const int Budget = 8;
    private const int Keep = 50;

    public static async Task<ContentProject?> LocateAsync(Instance inst, string filePath, string title,
        ContentType type, CancellationToken ct = default)
    {
        var file = Path.GetFileName(filePath);
        var ctx = Context(inst, type);
        return await OnModrinth(file, title, type, ctx, ct) ?? await OnCurseForge(file, title, type, ctx, ct);
    }

    public static async Task ApplyAsync(Instance inst, ContentProject p, string versionId,
        ContentType type, CancellationToken ct = default)
    {
        var ctx = Context(inst, type);
        if (p.Modrinth != null)
        {
            await ModrinthService.InstallAsync(inst, p.Modrinth, ctx.mc, ctx.loader, type, versionId, ct);
            return;
        }
        if (p.CurseForge != null && long.TryParse(versionId, out var fileId))
            await CurseForgeService.InstallAsync(inst, p.CurseForge, ctx.mc, ctx.loader, type, fileId, ct);
    }

    private record Ctx(string mc, string loader, string? format, bool needsLoader);

    private static Ctx Context(Instance inst, ContentType type) =>
        new(inst.Config.MCVersion,
            inst.Config.Loader == LoaderKind.Vanilla ? "" : inst.Config.Loader.ToString(),
            type == ContentType.Shader ? ShaderFormat.For(inst) : null,
            type == ContentType.Mod);

    private static async Task<ContentProject?> OnModrinth(string file, string title, ContentType type,
        Ctx ctx, CancellationToken ct)
    {
        var fetched = new Dictionary<string, List<ProjectVersion>>();
        var seen = new List<Hit>();
        foreach (var query in Queries(title, file))
        {
            List<Hit> hits;
            try
            {
                (hits, _) = await ModrinthService.SearchAsync(query, type, ctx.mc, ctx.loader, 8, 0, ctx.format);
            }
            catch { break; }
            seen.AddRange(hits);
            foreach (var h in hits.Take(Candidates))
            {
                ct.ThrowIfCancellationRequested();
                if (fetched.Count >= Budget) break;
                if (!fetched.TryGetValue(h.project_id, out var versions))
                {
                    try { versions = await ModrinthService.GetVersionsAsync(h.project_id); }
                    catch { continue; }
                    fetched[h.project_id] = versions;
                }
                if (versions.Any(v => v.files?.Any(f => SameName(f.filename, file)) == true))
                    return From(h.title ?? title, h, versions, file, ctx);
            }
        }
        foreach (var h in seen.DistinctBy(x => x.project_id).Take(Candidates))
        {
            if (!SameName(h.title ?? "", title)) continue;
            if (!fetched.TryGetValue(h.project_id, out var versions))
            {
                try { versions = await ModrinthService.GetVersionsAsync(h.project_id); }
                catch { continue; }
                fetched[h.project_id] = versions;
            }
            return From(h.title ?? title, h, versions, file, ctx);
        }
        return null;
    }

    private static ContentProject From(string title, Hit hit, List<ProjectVersion> versions, string file, Ctx ctx)
    {
        var fits = versions
            .Where(v => v.files?.Count > 0)
            .Where(v => McVersions.Matches(v.game_versions, ctx.mc))
            .Where(v => !ctx.needsLoader || ctx.loader.Length == 0 || FitsLoader(v.loaders, ctx.loader))
            .OrderByDescending(v => v.date_published)
            .ToList();
        var shaped = ModrinthService.PreferFormat(fits, ctx.format).Take(Keep).ToList();
        var current = shaped.FirstOrDefault(v => v.files!.Any(f => SameName(f.filename, file)));
        return new ContentProject(title, hit, null, shaped, null, current?.id);
    }

    private static async Task<ContentProject?> OnCurseForge(string file, string title, ContentType type,
        Ctx ctx, CancellationToken ct)
    {
        var fetched = new Dictionary<long, List<CFFile>>();
        var seen = new List<CFMod>();
        foreach (var query in Queries(title, file))
        {
            List<CFMod> mods;
            try
            {
                (mods, _) = await CurseForgeService.SearchAsync(query, type, ctx.mc, ctx.loader, 8, 1, ct);
            }
            catch { break; }
            seen.AddRange(mods);
            foreach (var m in mods.Take(Candidates))
            {
                ct.ThrowIfCancellationRequested();
                if (fetched.Count >= Budget) break;
                if (!fetched.TryGetValue(m.id, out var files))
                {
                    try { files = await CurseForgeService.GetFilesAsync(m.id, ct: ct, gameVersion: ctx.mc); }
                    catch { continue; }
                    fetched[m.id] = files;
                }
                if (files.Any(f => SameName(f.fileName, file))) return FromCf(m, files, file, ctx);
            }
        }
        foreach (var m in seen.DistinctBy(x => x.id).Take(Candidates))
        {
            if (!SameName(m.name ?? "", title)) continue;
            if (!fetched.TryGetValue(m.id, out var files))
            {
                try { files = await CurseForgeService.GetFilesAsync(m.id, ct: ct, gameVersion: ctx.mc); }
                catch { continue; }
                fetched[m.id] = files;
            }
            return FromCf(m, files, file, ctx);
        }
        return null;
    }

    private static ContentProject FromCf(CFMod mod, List<CFFile> files, string file, Ctx ctx)
    {
        var fits = files
            .Where(f => f.fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                        f.fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(f => McVersions.Matches(f.gameVersions, ctx.mc))
            .Where(f => !ctx.needsLoader || ctx.loader.Length == 0 ||
                        f.gameVersions?.Any(v => v.Equals(ctx.loader, StringComparison.OrdinalIgnoreCase)) == true)
            .OrderByDescending(f => DateTime.TryParse(f.fileDate, out var d) ? d : DateTime.MinValue)
            .ToList();
        var shaped = CurseForgeService.PreferFormat(fits, ctx.format).Take(Keep).ToList();
        var current = shaped.FirstOrDefault(f => SameName(f.fileName, file));
        return new ContentProject(mod.name ?? "", null, mod, null, shaped, current?.id.ToString());
    }

    private static IEnumerable<string> Queries(string title, string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var words = new[] { Phrase(stem), First(stem) };
        var named = SameName(title, file) || SameName(title, stem) ? words : new[] { title }.Concat(words);
        return named.Select(q => q.Trim()).Where(q => q.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] Noise =
        { "mc", "for", "the", "mod", "mods", "beta", "alpha", "rc", "release", "latest", "stable" };

    private static IEnumerable<string> Tokens(string stem) =>
        Regex.Split(stem, @"[^0-9A-Za-z]+|(?<=[a-z0-9])(?=[A-Z])")
            .Where(w => w.Length > 0);

    private static string Phrase(string stem) => string.Join(" ", Tokens(stem)
        .Where(w => !w.Any(char.IsDigit) && !Noise.Contains(w.ToLowerInvariant()))
        .Take(4));

    private static string First(string stem) => Tokens(stem)
        .FirstOrDefault(w => !w.Any(char.IsDigit) && !Noise.Contains(w.ToLowerInvariant()) && w.Length > 3) ?? "";

    private static bool FitsLoader(List<string>? loaders, string wanted) =>
        loaders?.Any(l => l.Equals(wanted, StringComparison.OrdinalIgnoreCase)) == true;

    private static bool SameName(string a, string b) =>
        string.Equals(Path.GetFileName(a).Trim(), Path.GetFileName(b).Trim(), StringComparison.OrdinalIgnoreCase);
}
