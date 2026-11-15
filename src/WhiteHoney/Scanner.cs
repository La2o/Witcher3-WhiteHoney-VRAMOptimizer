using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace WhiteHoney;

public enum AssetKind { Texture, Mesh, Other }

public sealed class AssetInfo
{
    /// <summary>What the game resolves conflicts by: "tex|&lt;cache key&gt;" for a texture, "file|&lt;depot path&gt;" for a bundle file.</summary>
    public string Key = "";
    public string Path = "";
    public string Class = "";
    public AssetKind Kind = AssetKind.Other;
    public long DiskBytes;
    /// <summary>
    /// Bytes the renderer uploads to the GPU when the asset is shown at its best: a mesh's vertex + index buffers, or
    /// all mips of a texture that the player's Texture Quality setting lets the game load.
    /// </summary>
    public long GpuBytes;
    /// <summary>A texture's size with every mip in the file, whatever the settings.</summary>
    public long FullGpuBytes;
    /// <summary>Largest mips the game never loads at the player's Texture Quality (a 4K texture on High shows at 2K: 1).</summary>
    public int SkipMips;
    /// <summary>Other buffer data (animations and so on): system RAM, not VRAM.</summary>
    public long DataBytes;
    public int Width, Height, Mips, Slices;
    /// <summary>Mips the budget fix could drop (each stored in a block of its own).</summary>
    public int StreamMips;
    /// <summary>Version of the texture.cache this texture is in.</summary>
    public uint CacheVersion;
    public string? Compression;
    public uint TexKey;
    /// <summary>Cheap fingerprint of the mod's copy, kept in the budget fix so a later scan can tell when the mod changed.</summary>
    public string Stamp = "";
    public long Tris0, Verts0;
    public int LodCount;
    public bool OverridesBase;
    public bool IdenticalToVanilla;
    public long BaseGpuBytes;
    public int BaseWidth, BaseHeight;
    public long BaseTris0;
    public string? ShadowedBy;
    /// <summary>Hard references (CR2W imports) this file adds that the vanilla version didn't have.</summary>
    public List<string> NewImports = new();
    public List<string> MissingImports = new();
    /// <summary>Depot paths of every file this one points to.</summary>
    public List<string> Refs = new();
    /// <summary>
    /// Loaded during normal play: replaces a game file, or is pulled in by one (e.g. a new texture used by a
    /// replaced mesh). Everything else (new armor, weapons, DLC-style content) only costs VRAM when it is used.
    /// </summary>
    public bool AlwaysLoaded;
    public List<string> Flags = new();

    /// <summary>VRAM this asset adds compared to running without the mod.</summary>
    public long DeltaGpu => ShadowedBy != null ? 0 : OverridesBase ? GpuBytes - BaseGpuBytes : GpuBytes;
    public string DisplayName => Path;
    public int MaxDim => Math.Max(Width, Height);
    /// <summary>Size the game shows the texture at with the player's settings.</summary>
    public int ShownDim => MaxDim >> SkipMips;
    public bool Loads => ShadowedBy == null;
}

public sealed class ModReport
{
    public string Name = "";
    /// <summary>The mod's folder.</summary>
    public string File = "";
    public string Source = "";
    public int Priority;
    public long DiskSize;
    public string? Error;
    /// <summary>Installed mod this folder belongs to (from Vortex/MO2), if known.</summary>
    public string? Package;
    public int? NexusId;
    public string? Manager;
    public List<AssetInfo> Assets = new();

    public long GpuDelta => Assets.Sum(a => a.DeltaGpu);
    public long AlwaysDelta => Assets.Where(a => a.AlwaysLoaded).Sum(a => a.DeltaGpu);
    public long WhenUsedDelta => Assets.Where(a => !a.AlwaysLoaded).Sum(a => a.DeltaGpu);
    public long TextureDelta => Assets.Where(a => a.Kind == AssetKind.Texture).Sum(a => a.DeltaGpu);
    public long MeshDelta => Assets.Where(a => a.Kind == AssetKind.Mesh).Sum(a => a.DeltaGpu);
    public int Textures => Assets.Count(a => a.Kind == AssetKind.Texture);
    public int Meshes => Assets.Count(a => a.Kind == AssetKind.Mesh);
    public long Tris0 => Assets.Where(a => a.Loads).Sum(a => a.Tris0);
    public int Shadowed => Assets.Count(a => !a.Loads);
    public long ShadowedDisk => Assets.Where(a => !a.Loads).Sum(a => a.DiskBytes);
    public int Overrides => Assets.Count(a => a.OverridesBase);
    public int Missing => Assets.Where(a => a.Loads).Sum(a => a.MissingImports.Count);
    public int FlagCount => Assets.Count(a => a.Flags.Count > 0 && a.Loads);
    /// <summary>Data that loads into system RAM, not VRAM (e.g. animations).</summary>
    public long DataBytes => Assets.Where(a => a.Loads).Sum(a => a.DataBytes);
}

public sealed class ScanResult
{
    public string GameDir = "";
    public List<ModReport> Mods = new();
    public int BaseFileCount, BaseTextureCount;
    public GpuInfo? Gpu;
    public GameSettings? Settings;
    public string? BudgetFix;
    public List<string> ManagerNotes = new();
    public string BudgetWhy = "";
    public List<string> HistoryDiff = new();
    public TimeSpan Elapsed;
    public long TotalDelta => Mods.Sum(m => m.GpuDelta);
    public long AlwaysDelta => Mods.Sum(m => m.AlwaysDelta);
    public long WhenUsedDelta => Mods.Sum(m => m.WhenUsedDelta);
    public long DataBytes => Mods.Sum(m => m.DataBytes);
    /// <summary>Whether an existing budget fix still matches the installed mods (null when there is no fix).</summary>
    public BudgetFix.FixCheck? FixCheck;
}

public sealed class ScanOptions
{
    public required string GameDir;
    /// <summary>Folder laid out like the game root (mods\, dlc\) to read mods from. Defaults to GameDir.</summary>
    public string? ModRoot;
    public string? Mo2Instance;
    /// <summary>The game's mods.settings (load order priorities). Defaults to the one in Documents.</summary>
    public string? ModsSettings;
    /// <summary>The game's dx12user.settings / user.settings. Defaults to the one in Documents.</summary>
    public string? SettingsFile;
    public bool CompareVanilla = true;
    public Action<string>? Log;
    /// <summary>Called as mods finish: (done, total).</summary>
    public Action<int, int>? Progress;
}

public sealed class Scanner
{
    /// <summary>Part of the budget fix's folder name; the scan leaves that folder out so the plan is always made on the real mods.</summary>
    public const string FixMarker = "WhiteHoneyFix";

    /// <summary>Folders in dlc\ that belong to the game itself, not to mods.</summary>
    static readonly Regex OfficialDlc = new(@"^(dlc\d{1,2}|bob|ep1|ep2)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex PathRx = new(@"[A-Za-z0-9_\-\\/\. ]{3,}\.(xbm|w2mesh|w2mi|w2mg|w2ent|w2p|w2rig|w2anims|w2cube|texarray|redcloth|redfur|redapex|srt|w2fnt|w2beh|env|w2scene|w2l|w2w)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    readonly ScanOptions _o;
    readonly ConcurrentDictionary<string, byte> _referenced = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Bundle> _baseBundles = new();
    readonly Dictionary<string, (int Bundle, int Entry)> _baseFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly List<TextureCache> _baseCaches = new();
    readonly Dictionary<uint, (int Cache, int Entry)> _baseTex = new();
    GameSettings? _settings;

    public Scanner(ScanOptions o) => _o = o;
    void Log(string s) => _o.Log?.Invoke(s);

    public static string DefaultModsSettings =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "The Witcher 3", "mods.settings");

    /// <summary>[modName] sections of mods.settings: Enabled and Priority (lower number loads first).</summary>
    public static Dictionary<string, (bool Enabled, int Priority)> ReadModsSettings(string? path)
    {
        var d = new Dictionary<string, (bool, int)>(StringComparer.OrdinalIgnoreCase);
        if (path == null || !File.Exists(path)) return d;
        try
        {
            string? section = null; bool enabled = true; int priority = int.MaxValue;
            void Flush() { if (section != null) d[section] = (enabled, priority); }
            foreach (var raw in File.ReadAllLines(path))
            {
                var l = raw.Trim();
                if (l.StartsWith('[') && l.EndsWith(']')) { Flush(); section = l[1..^1]; enabled = true; priority = int.MaxValue; continue; }
                int eq = l.IndexOf('=');
                if (eq <= 0) continue;
                var k = l[..eq].Trim(); var v = l[(eq + 1)..].Trim();
                if (k.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) enabled = v != "0";
                else if (k.Equals("Priority", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out var p)) priority = p;
            }
            Flush();
        }
        catch { }
        return d;
    }

    /// <summary>The game compares mod folder names without regard to case: digits first, then "_", then letters.</summary>
    public static int CompareModNames(string a, string b) => string.CompareOrdinal(a.ToLowerInvariant(), b.ToLowerInvariant());

    /// <summary>
    /// Mod folders in the order the game loads them: mods\mod* by mods.settings priority (lower first; mods without one
    /// come after), then by name. Earlier mods win file conflicts. dlc\ folders that aren't the game's own come last;
    /// they add new content rather than replace files. The budget fix's own folder is left out.
    /// </summary>
    public static List<ModSource> DiscoverLoadOrder(string modRoot, IReadOnlyList<ModSource>? extra = null, string? modsSettings = null)
    {
        var settings = ReadModsSettings(modsSettings ?? DefaultModsSettings);
        var mods = new List<ModSource>();
        var dlcs = new List<ModSource>();
        void AddFrom(string dir, string kind, List<ModSource> into)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = System.IO.Path.GetFileName(d);
                if (kind == "mods" && !name.StartsWith("mod", StringComparison.OrdinalIgnoreCase)) continue; // the game ignores other folders
                if (kind == "dlc" && OfficialDlc.IsMatch(name)) continue;
                into.Add(new ModSource(d, kind));
            }
        }
        AddFrom(System.IO.Path.Combine(modRoot, "mods"), "mods", mods);
        AddFrom(System.IO.Path.Combine(modRoot, "dlc"), "dlc", dlcs);
        // Mod-manager folders that aren't deployed into the game folder (MO2 virtual file system).
        foreach (var x in extra ?? Array.Empty<ModSource>())
        {
            var into = x.Kind == "dlc" ? dlcs : mods;
            if (x.Kind == "mods" && x.Manager == "MO2" && !x.Name.StartsWith("mod", StringComparison.OrdinalIgnoreCase)) continue;
            if (x.Kind == "dlc" && OfficialDlc.IsMatch(x.Name)) continue;
            if (!into.Any(f => string.Equals(f.Name, x.Name, StringComparison.OrdinalIgnoreCase))) into.Add(x);
        }
        bool Enabled(ModSource m) => !settings.TryGetValue(m.Name, out var s) || s.Enabled;
        int Priority(ModSource m) => settings.TryGetValue(m.Name, out var s) ? s.Priority : int.MaxValue;
        var result = mods.Where(m => !m.Name.Contains(FixMarker, StringComparison.OrdinalIgnoreCase) && Enabled(m))
            .OrderBy(Priority).ThenBy(m => m.Name, Comparer<string>.Create(CompareModNames)).ToList();
        result.AddRange(dlcs.OrderBy(m => m.Name, Comparer<string>.Create(CompareModNames)));
        return result;
    }

    /// <summary>
    /// Mods subscribed to on the Steam Workshop: they live in the Steam library's workshop folder
    /// (steamapps/workshop/content/292030/&lt;item&gt;), not in the game's mods folder.
    /// </summary>
    public static List<ModSource> WorkshopMods(string gameDir)
    {
        var list = new List<ModSource>();
        try
        {
            var steamapps = new DirectoryInfo(gameDir).Parent?.Parent;
            if (steamapps == null || !steamapps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return list;
            var root = System.IO.Path.Combine(steamapps.FullName, "workshop", "content", "292030");
            if (!Directory.Exists(root)) return list;
            foreach (var item in Directory.GetDirectories(root))
            {
                // An item holds one or more mod folders (each with a content folder), possibly under mods\ or dlc\.
                var found = new List<string>();
                void Walk(string dir, int depth)
                {
                    if (Directory.Exists(System.IO.Path.Combine(dir, "content"))) { found.Add(dir); return; }
                    if (depth < 3) foreach (var sub in Directory.GetDirectories(dir)) Walk(sub, depth + 1);
                }
                Walk(item, 0);
                foreach (var dir in found)
                {
                    bool dlc = System.IO.Path.GetFileName(dir).StartsWith("dlc", StringComparison.OrdinalIgnoreCase)
                        || System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(dir) ?? "").Equals("dlc", StringComparison.OrdinalIgnoreCase);
                    list.Add(new ModSource(dir, dlc ? "dlc" : "mods", "Workshop item " + System.IO.Path.GetFileName(item), null, "Steam Workshop"));
                }
            }
        }
        catch { }
        return list;
    }

    public static IEnumerable<string> ContentFiles(string modDir, string pattern)
    {
        try { return Directory.EnumerateFiles(modDir, pattern, SearchOption.AllDirectories).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    public ScanResult Scan()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var res = new ScanResult { GameDir = _o.GameDir };
        var modRoot = _o.ModRoot ?? _o.GameDir;
        _settings = _o.SettingsFile != null ? GameSettings.LoadFile(_o.SettingsFile, _o.GameDir) : GameSettings.Load(_o.GameDir);

        if (_o.CompareVanilla)
        {
            Log("Reading the game's own file list (a few seconds)...");
            LoadBaseIndex();
            res.BaseFileCount = _baseFiles.Count;
            res.BaseTextureCount = _baseTex.Count;
        }

        var managers = ModManagers.Detect(_o.GameDir, modRoot, _o.Mo2Instance, Log);
        res.ManagerNotes = managers.Notes;
        var workshop = _o.ModRoot == null ? WorkshopMods(_o.GameDir) : new List<ModSource>();
        if (workshop.Count > 0) res.ManagerNotes.Add($"Steam Workshop: {workshop.Count} subscribed mod folder(s).");
        var order = DiscoverLoadOrder(modRoot, managers.ExtraMods.Concat(workshop).ToList(), _o.ModsSettings);
        res.BudgetFix = BudgetFix.FindExisting(modRoot);
        Log($"Found {order.Count} mod folder(s)." + (order.Count > 300 ? " Big mod lists take a few minutes; progress is shown below." : ""));
        int done = 0;
        var mods = new ModReport[order.Count];

        Parallel.For(0, order.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, i =>
        {
            var src = order[i];
            var info = managers.Lookup(src.Name);
            var mr = new ModReport
            {
                Name = src.Name, File = src.Dir, Source = src.Manager != null ? $"{src.Manager}: {src.Package}" : src.Kind, Priority = i,
                Package = info?.Package ?? src.Package, NexusId = info?.NexusId ?? src.NexusId, Manager = info?.Manager ?? src.Manager,
            };
            mods[i] = mr;
            try { AnalyzeMod(src, mr); }
            catch (Exception ex) { mr.Error = ex.Message; }
            _o.Progress?.Invoke(Interlocked.Increment(ref done), order.Count);
        });

        // Conflicts: the first mod in load order that has a file wins; later copies never load.
        var winner = new Dictionary<string, ModReport>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
            foreach (var a in m.Assets)
            {
                if (winner.TryGetValue(a.Key, out var w) && w != m) { a.ShadowedBy = w.Name; a.Flags.Add($"never loads: {w.Name} wins this file"); }
                else winner[a.Key] = m;
            }

        // Always loaded = replaces a game file, or is reachable from one through references (a replaced mesh or
        // material pointing at a new texture). New items reached only from item definitions load when used.
        var loaded = new Dictionary<string, List<AssetInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in mods.SelectMany(m => m.Assets).Where(a => a.Loads))
        {
            if (!loaded.TryGetValue(a.Path, out var l)) loaded[a.Path] = l = new();
            l.Add(a); // a texture's cache entry and its .xbm header share one path
        }
        var queue = new Queue<AssetInfo>(loaded.Values.SelectMany(l => l).Where(a => a.OverridesBase));
        foreach (var a in queue) a.AlwaysLoaded = true;
        while (queue.TryDequeue(out var cur))
            foreach (var r in cur.Refs)
                if (loaded.TryGetValue(r, out var next))
                    foreach (var n in next.Where(n => !n.AlwaysLoaded)) { n.AlwaysLoaded = true; queue.Enqueue(n); }

        var existing = new HashSet<string>(loaded.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods)
            foreach (var a in m.Assets.Where(a => a.Loads))
            {
                AddFlags(a);

                // Hard references that point nowhere (only ones the mod added, so vanilla's own dead links don't count).
                if (_o.CompareVanilla)
                    foreach (var imp in a.NewImports)
                        if (!existing.Contains(imp) && !_baseFiles.ContainsKey(imp)) a.MissingImports.Add(imp);
                if (a.MissingImports.Count > 0)
                    a.Flags.Add($"references {a.MissingImports.Count} missing file(s): {string.Join(", ", a.MissingImports.Take(3))}{(a.MissingImports.Count > 3 ? ", ..." : "")}");

                if (a.IdenticalToVanilla) a.Flags.Add("identical to vanilla (does nothing)");

                // New texture/mesh nothing points at: likely leftover (or spawned by a script we can't see).
                if (!a.OverridesBase && a.Kind != AssetKind.Other && a.GpuBytes >= 64 * 1024 && !_referenced.ContainsKey(a.Path))
                    a.Flags.Add("possibly unused: nothing references this new file");
            }

        foreach (var b in _baseBundles) b.Dispose();
        foreach (var c in _baseCaches) c.Dispose();
        res.Mods = mods.ToList();
        res.Gpu = GpuInfo.Detect();
        res.Settings = _settings;
        res.Elapsed = sw.Elapsed;
        return res;
    }

    /// <summary>The game's own bundles and texture caches: content\ and the official dlc\ folders.</summary>
    public static IEnumerable<string> BaseContentDirs(string gameDir)
    {
        var content = System.IO.Path.Combine(gameDir, "content");
        if (Directory.Exists(content)) yield return content;
        var dlc = System.IO.Path.Combine(gameDir, "dlc");
        if (Directory.Exists(dlc))
            foreach (var d in Directory.GetDirectories(dlc).Where(d => OfficialDlc.IsMatch(System.IO.Path.GetFileName(d))).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                yield return d;
    }

    void LoadBaseIndex()
    {
        foreach (var dir in BaseContentDirs(_o.GameDir))
        {
            foreach (var f in ContentFiles(dir, "*.bundle").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                try
                {
                    var b = new Bundle(f);
                    int bi = _baseBundles.Count;
                    _baseBundles.Add(b);
                    for (int i = 0; i < b.Entries.Count; i++) _baseFiles.TryAdd(Bundle.Normalize(b.Entries[i].Name), (bi, i));
                }
                catch (Exception ex) { Log($"! Skipping game bundle {System.IO.Path.GetFileName(f)}: {ex.Message}"); }
            foreach (var f in ContentFiles(dir, "texture.cache").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                try
                {
                    var c = new TextureCache(f);
                    int ci = _baseCaches.Count;
                    _baseCaches.Add(c);
                    for (int i = 0; i < c.Entries.Count; i++) _baseTex.TryAdd(c.Entries[i].Hash, (ci, i));
                }
                catch (Exception ex) { Log($"! Skipping game texture cache {f}: {ex.Message}"); }
        }
        Log($"Indexed {_baseFiles.Count:N0} vanilla files from {_baseBundles.Count} bundles and {_baseTex.Count:N0} vanilla textures from {_baseCaches.Count} texture cache(s).");
    }

    void AddReference(string path) => _referenced.TryAdd(Bundle.Normalize(path), 0);

    void AnalyzeMod(ModSource src, ModReport mr)
    {
        var errors = new List<string>();
        foreach (var f in ContentFiles(src.Dir, "*"))
            try { mr.DiskSize += new FileInfo(f).Length; } catch { }

        // Textures: the streamed mips live in texture.cache, keyed by a hash of the texture's path.
        foreach (var f in ContentFiles(src.Dir, "texture.cache"))
        {
            TextureCache c;
            try { c = new TextureCache(f); }
            catch (Exception ex) { errors.Add($"texture.cache: {ex.Message}"); continue; }
            using (c)
                foreach (var e in c.Entries)
                {
                    var a = new AssetInfo
                    {
                        Key = $"tex|{e.Hash:X8}", Path = Bundle.Normalize(e.Name), Class = e.IsCube ? "CCubeTexture" : e.Slices > 1 ? "CTextureArray" : "CBitmapTexture",
                        Kind = AssetKind.Texture, TexKey = e.Hash, DiskBytes = e.ZSize, FullGpuBytes = e.Size,
                        Width = e.Width, Height = e.Height, Mips = e.Mips, Slices = e.Slices, Compression = e.FormatName,
                        StreamMips = e.StreamMips, CacheVersion = c.Version,
                        Stamp = e.Stamp,
                    };
                    a.SkipMips = Skipped(e);
                    a.GpuBytes = Shown(e.Size, a.SkipMips);
                    if (_baseTex.TryGetValue(e.Hash, out var b))
                    {
                        var be = _baseCaches[b.Cache].Entries[b.Entry];
                        a.OverridesBase = true;
                        a.BaseGpuBytes = Shown(be.Size, Skipped(be)); a.BaseWidth = be.Width; a.BaseHeight = be.Height;
                        if (be.Size == e.Size && be.ZSize == e.ZSize && be.Width == e.Width && be.Height == e.Height && be.Mips == e.Mips)
                            try { a.IdenticalToVanilla = c.ReadRaw(e).AsSpan().SequenceEqual(_baseCaches[b.Cache].ReadRaw(be)); } catch { }
                    }
                    mr.Assets.Add(a);
                }
        }

        // Everything else is in bundles, under its depot path.
        foreach (var f in ContentFiles(src.Dir, "*.bundle"))
        {
            Bundle bundle;
            try { bundle = new Bundle(f); }
            catch (Exception ex) { errors.Add($"{System.IO.Path.GetFileName(f)}: {ex.Message}"); continue; }
            using (bundle)
            {
                // "<file>.<n>.buffer" entries are the deferred data of <file>.
                var bufferDisk = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in bundle.Entries.Where(e => e.Name.EndsWith(".buffer", StringComparison.OrdinalIgnoreCase)))
                {
                    var parent = Bundle.Normalize(BufferParent(e.Name));
                    bufferDisk[parent] = bufferDisk.GetValueOrDefault(parent) + e.ZSize;
                }
                foreach (var e in bundle.Entries)
                {
                    if (e.Name.EndsWith(".buffer", StringComparison.OrdinalIgnoreCase)) continue;
                    var path = Bundle.Normalize(e.Name);
                    var a = new AssetInfo { Key = "file|" + path, Path = path, DiskBytes = e.ZSize + bufferDisk.GetValueOrDefault(path), Stamp = $"{e.Size}:{e.Crc:X8}" };
                    AnalyzeFile(bundle, e, a);
                    mr.Assets.Add(a);
                }
            }
        }

        // Loose text files (item definitions, scripts) name the entities and meshes they spawn.
        foreach (var pattern in new[] { "*.xml", "*.ws", "*.csv" })
            foreach (var f in ContentFiles(src.Dir, pattern))
                try
                {
                    if (new FileInfo(f).Length < 8 << 20)
                        foreach (Match m in PathRx.Matches(File.ReadAllText(f))) AddReference(m.Value);
                }
                catch { }

        if (errors.Count > 0) mr.Error = string.Join("; ", errors);
    }

    /// <summary>Largest mips of a texture the game leaves out at the player's Texture Quality setting.</summary>
    int Skipped(TexEntry e) => _settings == null ? 0 : Math.Min(_settings.SkippedMips(Math.Max(e.Width, e.Height)), Math.Max(0, e.Mips - 1));

    /// <summary>A texture's bytes without its largest <paramref name="skip"/> mips: each level down is a quarter.</summary>
    static long Shown(long size, int skip) => size >> (2 * skip);

    static string BufferParent(string name)
    {
        // "x.w2mesh.1.buffer" -> "x.w2mesh"
        var s = name[..^".buffer".Length];
        int dot = s.LastIndexOf('.');
        return dot > 0 && s[(dot + 1)..].All(char.IsDigit) ? s[..dot] : s;
    }

    void AnalyzeFile(Bundle bundle, BundleEntry e, AssetInfo a)
    {
        byte[]? data = null;
        if (e.Size <= 256 << 20) try { data = bundle.Read(e); } catch { }

        byte[]? baseData = null;
        if (_baseFiles.TryGetValue(a.Path, out var b))
        {
            a.OverridesBase = true;
            var bb = _baseBundles[b.Bundle];
            var be = bb.Entries[b.Entry];
            if (be.Size <= 256 << 20) try { baseData = bb.Read(be); } catch { }
            a.IdenticalToVanilla = data != null && baseData != null && data.AsSpan().SequenceEqual(baseData);
        }

        if (!Cr2wFile.LooksLikeCr2w(data))
        {
            // Item definitions and other text inside a bundle name the files they use.
            if (data != null && data.Length < 8 << 20 && (a.Path.EndsWith(".xml") || a.Path.EndsWith(".csv")))
                foreach (Match m in PathRx.Matches(Encoding.Latin1.GetString(data))) AddReference(m.Value);
            return;
        }

        Cr2wFile c;
        try { c = new Cr2wFile(data!); } catch { return; }
        a.Class = c.RootClass;
        foreach (var imp in c.Imports.Where(i => i.Path.Length > 0))
        {
            var p = Bundle.Normalize(imp.Path);
            a.Refs.Add(p);
            _referenced.TryAdd(p, 0);
        }

        Cr2wFile? vanilla = null;
        if (Cr2wFile.LooksLikeCr2w(baseData)) try { vanilla = new Cr2wFile(baseData!); } catch { }
        var known = new HashSet<string>(vanilla?.Imports.Select(i => Bundle.Normalize(i.Path)) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        a.NewImports = c.Imports.Where(i => IsHardImport(i.Flags) && i.Path.Length > 0).Select(i => Bundle.Normalize(i.Path)).Where(p => !known.Contains(p)).Distinct().ToList();

        try
        {
            if (MeshReader.Read(c) is { } mesh)
            {
                a.Kind = AssetKind.Mesh;
                a.GpuBytes = mesh.GpuBytes; a.Tris0 = mesh.Tris0; a.Verts0 = mesh.Verts0; a.LodCount = mesh.LodCount;
                if (vanilla != null && MeshReader.Read(vanilla) is { } vm) { a.BaseGpuBytes = vm.GpuBytes; a.BaseTris0 = vm.Tris0; }
            }
            else if (c.RootClass is not ("CBitmapTexture" or "CCubeTexture" or "CTextureArray"))
                a.DataBytes = c.BufferSizes.Sum(s => (long)s); // animations, cloth, physics: system RAM
        }
        catch { /* keep sizes; details are best-effort */ }
    }

    // Import flags: 4 = soft (resolved only when asked for). Only the rest must exist.
    static bool IsHardImport(ushort flags) => (flags & 4) == 0;

    static void AddFlags(AssetInfo a)
    {
        if (a.Kind == AssetKind.Texture)
        {
            string shown = a.SkipMips > 0 ? $" (your Texture Quality setting shows it at {a.ShownDim} px)" : "";
            if (a.MaxDim >= 8192) a.Flags.Add("8K texture" + shown);
            else if (a.MaxDim >= 4096) a.Flags.Add("4K texture" + shown);
            if (a.Mips <= 1 && a.MaxDim >= 2048) a.Flags.Add("no mipmaps (full size at any distance)");
            if (a.Compression == "uncompressed" && a.MaxDim >= 1024) a.Flags.Add("uncompressed (4x+ the VRAM of a compressed texture)");
            if (a.OverridesBase && a.BaseWidth > 0 && (long)a.Width * a.Height >= 4L * a.BaseWidth * a.BaseHeight)
                a.Flags.Add($"{(long)a.Width * a.Height / ((long)a.BaseWidth * a.BaseHeight)}x vanilla pixels ({a.BaseWidth}x{a.BaseHeight} -> {a.Width}x{a.Height})");
        }
        else if (a.Kind == AssetKind.Mesh)
        {
            if (a.LodCount <= 1 && a.Tris0 >= 50_000) a.Flags.Add("no LODs (full detail at any distance)");
            if (a.Tris0 >= 150_000) a.Flags.Add($"very dense mesh ({a.Tris0:N0} tris)");
            if (a.OverridesBase && a.BaseTris0 > 0 && a.Tris0 >= 3 * a.BaseTris0)
                a.Flags.Add($"{(double)a.Tris0 / a.BaseTris0:0.#}x vanilla triangles");
        }
    }
}
