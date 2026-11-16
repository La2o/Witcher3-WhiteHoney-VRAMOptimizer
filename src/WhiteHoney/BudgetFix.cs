using System.Text.Json;

namespace WhiteHoney;

/// <summary>
/// Stage 2: writes the budget plan as ONE extra mod folder that loads before the mods it slims down.
/// The mods themselves are never modified; deleting the fix folder restores everything.
/// </summary>
public static class BudgetFix
{
    /// <param name="Path">The fix's mod folder.</param>
    public sealed record Outcome(string Path, int Textures, long SavedBytes, List<string> Skipped);

    /// <summary>
    /// A small JSON file in the fix folder: each texture it replaces and a fingerprint of the mod copy it was made
    /// from, so a later scan can tell when mods changed under it. The game ignores it.
    /// </summary>
    public const string ManifestName = "white_honey_fix.json";

    /// <summary>Name of the MO2 mod folder the fix is saved as when run through Mod Organizer 2.</summary>
    public const string Mo2FixModName = "White Honey Budget Fix";

    /// <summary>
    /// Whether a fix still matches the installed mods. Changed: the mod's copy of a texture is different now (the mod
    /// was updated, or another mod now wins), so the fix shows an old version. Removed: no installed mod has that
    /// texture any more, so the fix brings back a texture of a mod you removed.
    /// </summary>
    public sealed record FixCheck(int Textures, List<string> Changed, List<string> Removed, List<string> Excluded, bool HasManifest)
    {
        public bool Stale => Changed.Count > 0 || Removed.Count > 0 || Excluded.Count > 0;

        public string Describe() => string.Join(" and ", new[]
        {
            Changed.Count > 0 ? $"{Changed.Count:N0} of its {Textures:N0} textures come from mods that were updated since" : null,
            Removed.Count > 0 ? $"{Removed.Count:N0} come from mods you removed" : null,
            Excluded.Count > 0 ? $"{Excluded.Count:N0} come from mods you have since excluded" : null,
        }.OfType<string>());
    }

    static string CacheOf(string fixDir) => System.IO.Path.Combine(fixDir, "content", "texture.cache");

    public static FixCheck? Check(string fixDir, ScanResult r)
    {
        try
        {
            // The scan leaves the fix out, so these are the textures the game would load without it.
            var winners = new Dictionary<uint, (ModReport Mod, AssetInfo Asset)>();
            foreach (var m in r.Mods)
                foreach (var x in m.Assets.Where(x => x.Kind == AssetKind.Texture && x.Loads)) winners.TryAdd(x.TexKey, (m, x));
            var changed = new List<string>();
            var removed = new List<string>();
            var excluded = new List<string>();
            int textures = 0;
            bool hasManifest = true;
            // Mods of both cache versions give two fix folders, side by side.
            foreach (var dir in FixFoldersIn(System.IO.Path.GetDirectoryName(fixDir)!).Where(d => File.Exists(CacheOf(d))))
            {
                using var c = new TextureCache(CacheOf(dir));
                Dictionary<uint, string>? manifest = null;
                var mf = System.IO.Path.Combine(dir, "content", ManifestName);
                if (File.Exists(mf))
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                        manifest = doc.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
                            f => Convert.ToUInt32(f.GetProperty("key").GetString(), 16), f => f.GetProperty("stamp").GetString() ?? "");
                    }
                    catch { }
                hasManifest &= manifest != null;
                textures += c.Entries.Count;
                foreach (var e in c.Entries)
                {
                    if (!winners.TryGetValue(e.Hash, out var now)) removed.Add(e.Name);
                    else if (now.Mod.ExcludedFromFix) excluded.Add($"{e.Name} ({now.Mod.Name})");
                    else if (manifest != null && manifest.TryGetValue(e.Hash, out var stamp) && stamp.Length > 0 && stamp != now.Asset.Stamp)
                        changed.Add($"{e.Name} ({now.Mod.Name})");
                }
            }
            return new FixCheck(textures, changed, removed, excluded, hasManifest);
        }
        catch { return null; }
    }

    static void WriteManifest(string path, IEnumerable<(uint Key, string Stamp, string Name, string Mod, int Halvings)> files)
    {
        using var fs = File.Create(path);
        using var w = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("tool", Report.Version);
        w.WriteString("created", DateTime.UtcNow.ToString("o"));
        w.WriteStartArray("files");
        foreach (var f in files.OrderBy(f => f.Key))
        {
            w.WriteStartObject();
            w.WriteString("key", $"{f.Key:X8}"); w.WriteString("stamp", f.Stamp); w.WriteString("name", f.Name);
            w.WriteString("mod", f.Mod); w.WriteNumber("halvings", f.Halvings);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    static IEnumerable<string> FixFoldersIn(string modsDir) =>
        Directory.Exists(modsDir) ? Directory.GetDirectories(modsDir, $"*{Scanner.FixMarker}*") : Array.Empty<string>();

    /// <summary>The fix's mod folder, if one exists in the game's mods folder or in <paramref name="extraDirs"/>.</summary>
    public static string? FindExisting(string modRoot, IEnumerable<string>? extraDirs = null)
    {
        foreach (var dir in (extraDirs ?? Enumerable.Empty<string>()).Prepend(System.IO.Path.Combine(modRoot, "mods")))
            if (FixFoldersIn(dir).FirstOrDefault(d => File.Exists(CacheOf(d))) is { } f) return f;
        return null;
    }

    /// <summary>
    /// Every mods folder outside the game's own where a fix may be: the last saved one, the
    /// "White Honey Budget Fix" mod of each MO2 instance we know, and the Documents fallback.
    /// </summary>
    public static List<string> KnownFixDirs(string? savedFixDir, IEnumerable<string?> mo2Instances, string toolDir)
    {
        var dirs = new List<string>();
        void Add(string? d) { if (d != null && !dirs.Contains(d, StringComparer.OrdinalIgnoreCase)) dirs.Add(d); }
        static string FixMod(string modsDir) => System.IO.Path.Combine(modsDir, Mo2FixModName, "mods");
        Add(savedFixDir);
        foreach (var inst in mo2Instances.Append(ModManagers.Mo2InstanceAround(toolDir)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ModManagers.Mo2ModsDir(inst) is { } mods) Add(FixMod(mods));
            if (ModManagers.Mo2OverwriteDir(inst) is { } overwrite) Add(System.IO.Path.Combine(overwrite, "mods"));
        }
        Add(FixMod(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "White Honey")));
        return dirs;
    }

    /// <summary>Rough size of the fix: the mips that are kept, at the size they have on disk in the mod.</summary>
    public static long EstimateBytes(IEnumerable<PlanStep> steps) => steps.Sum(s =>
        (long)((s.Asset.GpuBytes >> (2 * s.Halvings)) * Math.Clamp(s.Asset.FullGpuBytes > 0 ? (double)s.Asset.DiskBytes / s.Asset.FullGpuBytes : 1, 0.05, 1.0)));

    /// <summary>"About 240 MB to write, under a minute": copying compressed mips is fast (~400 MB/s of fix data on an SSD).</summary>
    public static string DescribeWork(IReadOnlyCollection<PlanStep> steps)
    {
        long bytes = EstimateBytes(steps);
        double seconds = bytes / (400.0 * (1 << 20)) + steps.Count * 0.002;
        string time = seconds < 60 ? "under a minute" : $"roughly {Math.Ceiling(seconds / 60)} minute(s)";
        return $"About {Report.Size(bytes)} to write, {time}.";
    }

    /// <summary>Live state of a build, read by the UI thread without blocking the work.</summary>
    public sealed class BuildStatus
    {
        public int Done;
        public int Total;
        public volatile string Phase = "shrinking";
    }

    /// <summary>
    /// Writes the plan as &lt;mods&gt;\&lt;fix folder&gt;\content\texture.cache. Each texture's kept mips are copied
    /// as they are (no decoding), so this is quick and never loses more quality than the mod's own smaller mips.
    /// A texture stays in the cache version its mod used (6 for mods made for game 4.x and older, 7 for
    /// Remastered), so mods of both kinds give two fix folders.
    /// </summary>
    public static Outcome Build(ScanResult r, BudgetPlan plan, string modRoot, Action<string> log, BuildStatus? status = null,
        Action<string>? trace = null, string? targetModsDir = null, IEnumerable<string>? otherFixDirs = null, string? modsSettings = null)
    {
        var modsDir = targetModsDir ?? System.IO.Path.Combine(modRoot, "mods");
        Directory.CreateDirectory(modsDir);
        // Only one fix may exist: drop older ones wherever they are (game folder, MO2 mod).
        Remove(modRoot, quiet: true, extraDirs: (otherFixDirs ?? Enumerable.Empty<string>()).Append(modsDir), modsSettings: modsSettings);
        status ??= new BuildStatus();
        status.Total = plan.Steps.Count;

        // Load before every other mod so our smaller copies win, without renaming anyone's folders.
        var settings = Scanner.ReadModsSettings(modsSettings);
        var baseName = NameThatLoadsFirst(r.Mods.Where(m => m.Source != "dlc").Select(m => m.Name));
        var skipped = new List<string>();
        long saved = 0;
        int count = 0;
        var folders = new List<string>();
        var caches = new Dictionary<string, TextureCache>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var groups = plan.Steps.GroupBy(s => s.Asset.CacheVersion).OrderByDescending(g => g.Count()).ToList();
            for (int gi = 0; gi < groups.Count; gi++)
            {
                uint version = groups[gi].Key;
                var name = gi == 0 ? baseName : $"{baseName}_v{version}";
                var fixDir = System.IO.Path.Combine(modsDir, name);
                var content = System.IO.Path.Combine(fixDir, "content");
                trace?.Invoke($"build: {groups[gi].Count()} textures (cache version {version}), output {fixDir}");
                var made = new List<(uint, string, string, string, int)>();
                using var writer = new TextureCacheWriter(content, version);
                foreach (var step in groups[gi].OrderBy(s => s.Mod.File, StringComparer.OrdinalIgnoreCase))
                {
                    string label = $"{step.Mod.Name}: {step.Asset.DisplayName}";
                    try
                    {
                        // A mod can carry more than one texture.cache; find the one with this texture.
                        TextureCache? src = null; TexEntry? e = null;
                        foreach (var f in Scanner.ContentFiles(step.Mod.File, "texture.cache"))
                        {
                            if (!caches.TryGetValue(f, out var c)) caches[f] = c = new TextureCache(f);
                            e = c.Entries.FirstOrDefault(x => x.Hash == step.Asset.TexKey);
                            if (e != null) { src = c; break; }
                        }
                        if (src == null || e == null) { skipped.Add($"{label} (not found)"); trace?.Invoke($"skip {label}: not found"); continue; }
                        var res = TextureShrinker.Shrink(src, e, src.ReadRaw(e), step.DropMips, out var why);
                        if (res == null) { skipped.Add($"{label} ({why})"); trace?.Invoke($"skip {label}: {why}"); continue; }
                        writer.Add(res);
                        made.Add((e.Hash, e.Stamp, e.Name, step.Mod.Name, step.DropMips));
                        saved += step.Saves;
                        trace?.Invoke($"done  {label}: {e.Width}x{e.Height} -> {res.Entry.Width}x{res.Entry.Height}");
                    }
                    catch (Exception ex) { skipped.Add($"{label} ({ex.Message})"); trace?.Invoke($"error {label}: {ex}"); }
                    finally { Interlocked.Increment(ref status.Done); }
                }
                if (writer.Count == 0)
                {
                    writer.Dispose();
                    try { Directory.Delete(fixDir, recursive: true); } catch { }
                    continue;
                }
                count += writer.Count;
                status.Phase = "saving";
                trace?.Invoke($"saving {CacheOf(fixDir)}");
                writer.Write(CacheOf(fixDir));
                WriteManifest(System.IO.Path.Combine(content, ManifestName), made);
                folders.Add(fixDir);
                status.Phase = "shrinking";
            }
        }
        finally { foreach (var c in caches.Values) c.Dispose(); }

        if (folders.Count == 0)
        {
            log("Nothing to write: no plan steps could be applied.");
            return new Outcome(System.IO.Path.Combine(modsDir, baseName), 0, 0, skipped);
        }
        trace?.Invoke("saved");

        // A load order file with priorities overrides folder names: give the fix the best priority in it.
        if (modsSettings != null && settings.Count > 0)
            try
            {
                var bak = modsSettings + ".whitehoney.bak";
                if (!File.Exists(bak)) File.Copy(modsSettings, bak);
                int best = settings.Values.Where(v => v.Priority != int.MaxValue).Select(v => v.Priority).DefaultIfEmpty(1).Min();
                var text = File.ReadAllText(modsSettings);
                var nl = text.Contains("\r\n") ? "\r\n" : "\n";
                text = text.TrimEnd('\r', '\n');
                foreach (var f in folders) text += $"{nl}[{System.IO.Path.GetFileName(f)}]{nl}Enabled=1{nl}Priority={best}";
                File.WriteAllText(modsSettings, text + nl);
                log("Added the fix to mods.settings (backup: mods.settings.whitehoney.bak).");
            }
            catch (Exception ex) { log($"! Could not update mods.settings ({ex.Message}). If the fix doesn't take effect, give {baseName} the lowest Priority number there."); }
        return new Outcome(folders[0], count, saved, skipped);
    }

    /// <summary>
    /// A mod folder name that the game sorts before every name in <paramref name="others"/> (it compares without
    /// regard to case). The name must start with "mod"; after that, '0' is lower than any letter, digit run or '_'
    /// a mod is likely to use, so zeros are added until ours comes first.
    /// </summary>
    public static string NameThatLoadsFirst(IEnumerable<string> others)
    {
        string? min = null;
        foreach (var o in others)
            if (min == null || Scanner.CompareModNames(o, min) < 0) min = o;
        for (int zeros = 4; zeros <= 40; zeros++)
        {
            var name = "mod" + new string('0', zeros) + "_" + Scanner.FixMarker;
            if (min == null || Scanner.CompareModNames(name, min) < 0) return name;
        }
        return "mod" + new string('0', 40) + "_" + Scanner.FixMarker;
    }

    /// <summary>
    /// Deletes the fix folder from the game's mods folder and from <paramref name="extraDirs"/>
    /// (see <see cref="KnownFixDirs"/>), and its entry in mods.settings. Returns the folders removed.
    /// </summary>
    /// <param name="say">Where the lines about each folder go unless <paramref name="quiet"/>; the console by default.</param>
    public static List<string> Remove(string modRoot, bool quiet = false, IEnumerable<string>? extraDirs = null, string? modsSettings = null, Action<string>? say = null)
    {
        say ??= Console.WriteLine;
        var removed = new List<string>();
        foreach (var dir in (extraDirs ?? Enumerable.Empty<string>()).Prepend(System.IO.Path.Combine(modRoot, "mods")).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var f in FixFoldersIn(dir).ToList())
            {
                // Only ever delete a folder that holds nothing but our own files.
                var files = Directory.GetFiles(f, "*", SearchOption.AllDirectories);
                if (files.Any(x => !IsOurFile(x))) { if (!quiet) say($"Left alone (it has other files in it): {f}"); continue; }
                Directory.Delete(f, recursive: true);
                removed.Add(f);
                if (!quiet) say($"Removed {f}");
            }
        if (modsSettings != null && File.Exists(modsSettings))
            try
            {
                var lines = File.ReadAllLines(modsSettings).ToList();
                var kept = new List<string>();
                bool skip = false;
                foreach (var l in lines)
                {
                    var t = l.Trim();
                    if (t.StartsWith('[')) skip = t.Contains(Scanner.FixMarker, StringComparison.OrdinalIgnoreCase);
                    if (!skip) kept.Add(l);
                }
                if (kept.Count != lines.Count) File.WriteAllLines(modsSettings, kept);
            }
            catch { }
        return removed;
    }

    static bool IsOurFile(string path)
    {
        var n = System.IO.Path.GetFileName(path);
        return n.Equals("texture.cache", StringComparison.OrdinalIgnoreCase) || n.Equals(ManifestName, StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".whitehoney-tmp", StringComparison.OrdinalIgnoreCase);
    }
}
