using System.Text;
using System.Text.Json;

namespace WhiteHoney;

/// <summary>Scan history: compares this scan with the previous one ("what changed since last time").</summary>
public static class History
{
    public sealed record Entry(string Name, long Delta, int Textures, int Meshes, long Tris, int Flagged);

    /// <summary>How VRAM is counted; scans made under another rule aren't compared.</summary>
    const int Counting = 1;

    public static List<string> SaveAndDiff(ScanResult r, string outDir)
    {
        var dir = Path.Combine(outDir, "history");
        Directory.CreateDirectory(dir);
        var previous = Directory.GetFiles(dir, "scan-*.json").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault();
        var current = r.Mods.Where(m => m.Error == null).Select(m => new Entry(m.Name, m.GpuDelta, m.Textures, m.Meshes, m.Tris0, m.FlagCount)).ToList();

        using (var fs = File.Create(Path.Combine(dir, $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.json")))
        using (var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject(); w.WriteNumber("counting", Counting); w.WriteNumber("total", r.TotalDelta); w.WriteStartArray("mods");
            foreach (var e in current)
            {
                w.WriteStartObject(); w.WriteString("name", e.Name); w.WriteNumber("delta", e.Delta); w.WriteNumber("textures", e.Textures);
                w.WriteNumber("meshes", e.Meshes); w.WriteNumber("tris", e.Tris); w.WriteNumber("flagged", e.Flagged); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        foreach (var old in Directory.GetFiles(dir, "scan-*.json").OrderByDescending(f => f, StringComparer.Ordinal).Skip(20)) File.Delete(old);
        if (previous == null) return new();

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(previous));
            if (!doc.RootElement.TryGetProperty("counting", out var c) || c.GetInt32() != Counting) return new();
            long prevTotal = doc.RootElement.GetProperty("total").GetInt64();
            var prev = doc.RootElement.GetProperty("mods").EnumerateArray()
                .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("delta").GetInt64());
            var lines = new List<string>();
            var when = File.GetLastWriteTime(previous);
            lines.Add($"Since last scan ({when:yyyy-MM-dd HH:mm}): total {Report.Size(prevTotal)} -> {Report.Size(r.TotalDelta)} ({Sign(r.TotalDelta - prevTotal)})");
            foreach (var e in current.Where(e => !prev.ContainsKey(e.Name)).OrderByDescending(e => e.Delta))
                lines.Add($"  + {e.Name}: {Report.Size(e.Delta)}");
            foreach (var name in prev.Keys.Where(n => current.All(c => c.Name != n)))
                lines.Add($"  - {name}: was {Report.Size(prev[name])}");
            foreach (var e in current.Where(e => prev.TryGetValue(e.Name, out var d) && Math.Abs(d - e.Delta) >= 1 << 20))
                lines.Add($"  ~ {e.Name}: {Report.Size(prev[e.Name])} -> {Report.Size(e.Delta)}");
            return lines;
        }
        catch { return new(); }
    }

    static string Sign(long d) => (d >= 0 ? "+" : "") + Report.Size(d);
}

/// <summary>Short Markdown summary to paste into a Nexus/Reddit/Discord support thread.</summary>
public static class Summary
{
    public static string Markdown(ScanResult r, BudgetPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"**White Honey v{Report.Version}** · {r.Mods.Count} mods · **{Report.Size(r.AlwaysDelta)}** extra in normal play · {Report.Size(r.WhenUsedDelta)} in new items (only when used)");
        if (r.Gpu != null) sb.AppendLine($"GPU: {r.Gpu.Name} ({Report.Size(r.Gpu.VramBytes)})" + (r.Settings != null ? $" · {r.Settings.Describe()}" : ""));
        sb.AppendLine($"Room for mods on top of the game: {Report.Size(plan.Budget)} ({r.BudgetWhy}) → {(plan.Current <= plan.Budget ? "fits" : $"{Report.Size(plan.Current - plan.Budget)} too much")}");
        if (r.BudgetFix != null) sb.AppendLine($"Budget fix active: `{Path.GetFileName(r.BudgetFix)}`" + (r.FixCheck is { Stale: true } f ? $" (out of date: {f.Describe()})" : ""));
        if (r.Mods.Where(m => m.ExcludedFromFix).Select(m => m.Name).ToList() is { Count: > 0 } kept) sb.AppendLine($"Kept as is (excluded from the fix): {string.Join(", ", kept)}");
        sb.AppendLine();
        sb.AppendLine("| Mod | Normal play | When used | Tex | Mesh | Flags |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        // One row per installed mod: a mod with several folders would otherwise show up several times under the same name.
        var rows = r.Mods.Where(m => m.Error == null).GroupBy(m => m.Package ?? m.Name)
            .Select(g => (Name: g.Count() > 1 ? $"{g.Key} ({g.Count()} folders)" : g.Key, Always: g.Sum(m => m.AlwaysDelta), WhenUsed: g.Sum(m => m.WhenUsedDelta),
                Total: g.Sum(m => m.GpuDelta), Tex: g.Sum(m => m.Textures), Mesh: g.Sum(m => m.Meshes), Flags: g.Sum(m => m.FlagCount)));
        foreach (var m in rows.OrderByDescending(m => m.Always).ThenByDescending(m => m.Total).Take(10))
            sb.AppendLine($"| {m.Name} | {Report.Size(m.Always)} | {Report.Size(m.WhenUsed)} | {m.Tex} | {m.Mesh} | {m.Flags} |");
        var broken = r.Mods.Where(m => m.Error != null).ToList();
        if (broken.Count > 0) sb.AppendLine().AppendLine("**Broken mods:** " + string.Join(", ", broken.Select(b => $"`{b.Name}`")));
        var missing = r.Mods.Where(m => m.Missing > 0).ToList();
        if (missing.Count > 0) sb.AppendLine().AppendLine("**Missing references (requirement not installed?):** " + string.Join(", ", missing.Select(m => $"`{m.Name}` ({m.Missing})")));
        return sb.ToString();
    }
}
