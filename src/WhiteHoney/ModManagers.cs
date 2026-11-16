using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhiteHoney;

public sealed record PackageInfo(string Package, int? NexusId, string Manager);

/// <summary>A mod folder to scan: mods\modXxx or dlc\dlcXxx (or the same inside a Mod Organizer 2 mod).</summary>
public sealed record ModSource(string Dir, string Kind, string? Package = null, int? NexusId = null, string? Manager = null)
{
    public string Name => Path.GetFileName(Dir.TrimEnd('\\', '/'));
}

/// <summary>
/// Maps mod folders back to the installed mod (and Nexus mod id) using Vortex's deployment manifest or a
/// Mod Organizer 2 instance. Read-only; nothing here touches the managers' data.
/// </summary>
public sealed class ModManagers
{
    readonly Dictionary<string, PackageInfo> _byFolder = new(StringComparer.OrdinalIgnoreCase);
    public List<ModSource> ExtraMods { get; } = new();
    public List<string> Notes { get; } = new();

    // Vortex staging folders are named "<name>-<nexusId>-<version>-<timestamp>".
    static readonly Regex VortexName = new(@"^(?<name>.+?)-(?<id>\d+)-(?<ver>.+)-(?<ts>\d{9,11})$", RegexOptions.Compiled);

    public PackageInfo? Lookup(string modFolderName) => _byFolder.GetValueOrDefault(modFolderName);

    public static ModManagers Detect(string gameDir, string modRoot, string? mo2Instance, Action<string> log)
    {
        var mm = new ModManagers();
        try { mm.ReadVortex(modRoot); } catch (Exception ex) { log($"! Vortex manifest unreadable: {ex.Message}"); }
        try
        {
            var instances = mo2Instance != null ? new List<string> { mo2Instance } : FindMo2Instances(gameDir);
            foreach (var inst in instances) mm.ReadMo2(inst, gameDir, explicitInstance: mo2Instance != null);
        }
        catch (Exception ex) { log($"! MO2 instance unreadable: {ex.Message}"); }
        foreach (var n in mm.Notes) log(n);
        return mm;
    }

    void ReadVortex(string root)
    {
        var candidates = new[] { root, Path.Combine(root, "mods"), Path.Combine(root, "dlc") }
            .Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "vortex.deployment*.json"));
        int n = 0;
        foreach (var manifest in candidates)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("files", out var files)) continue;
            foreach (var f in files.EnumerateArray())
            {
                var rel = f.TryGetProperty("relPath", out var r) ? r.GetString() : null;
                var source = f.TryGetProperty("source", out var s) ? s.GetString() : null;
                if (rel == null || source == null) continue;
                // relPath is "mods\modFoo\content\..." from the game root, or "modFoo\content\..." from the mods folder.
                var folder = rel.Split('\\', '/').FirstOrDefault(p => p.StartsWith("mod", StringComparison.OrdinalIgnoreCase) && !p.Equals("mods", StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith("dlc", StringComparison.OrdinalIgnoreCase) && !p.Equals("dlc", StringComparison.OrdinalIgnoreCase));
                if (folder == null || _byFolder.ContainsKey(folder)) continue;
                var m = VortexName.Match(source);
                _byFolder[folder] = m.Success
                    ? new PackageInfo(m.Groups["name"].Value, int.Parse(m.Groups["id"].Value), "Vortex")
                    : new PackageInfo(source, null, "Vortex");
                n++;
            }
        }
        if (n > 0) Notes.Add($"Vortex: matched {n} mod folder(s) to installed mods.");
    }

    /// <summary>The real (non-virtual) mods folder of an MO2 instance, or null.</summary>
    public static string? Mo2ModsDir(string instance) => Mo2Dir(instance, "mod_directory", "mods");

    /// <summary>The real Overwrite folder of an MO2 instance (where new files made through MO2 land), or null.</summary>
    public static string? Mo2OverwriteDir(string instance) => Mo2Dir(instance, "overwrite_directory", "overwrite");

    static string Unwrap(string? v) => v == null ? "" : Regex.Replace(v, @"^@ByteArray\((.*)\)$", "$1").Replace(@"\\", @"\").Replace('/', '\\');

    static string? Mo2Dir(string instance, string key, string fallback)
    {
        try
        {
            var iniPath = Path.Combine(instance, "ModOrganizer.ini");
            var ini = File.Exists(iniPath) ? ParseIni(File.ReadAllLines(iniPath)) : new();
            var baseDir = Unwrap(ini.GetValueOrDefault("base_directory"));
            if (baseDir.Length == 0) baseDir = instance;
            var dir = Unwrap(ini.GetValueOrDefault(key)).Replace("%BASE_DIR%", baseDir);
            if (dir.Length == 0) dir = Path.Combine(baseDir, fallback);
            return Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    /// <summary>A portable MO2 instance this tool sits inside (installed as an MO2 mod and started from there), or null.</summary>
    public static string? Mo2InstanceAround(string dir)
    {
        try
        {
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "ModOrganizer.ini"))) return d.FullName;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// The MO2 instance that is running right now (the one that launched us through its virtual file system):
    /// a portable instance next to ModOrganizer.exe, or the global instance MO2 last opened.
    /// </summary>
    public static string? RunningMo2Instance()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("ModOrganizer"))
            {
                string? exeDir = null;
                try { exeDir = Path.GetDirectoryName(p.MainModule?.FileName); } catch { }
                if (exeDir != null && File.Exists(Path.Combine(exeDir, "ModOrganizer.ini"))) return exeDir;
            }
            if (OperatingSystem.IsWindows() &&
                Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Mod Organizer Team\Mod Organizer", "CurrentInstance", null) is string name && name.Length > 0)
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer", name);
                if (File.Exists(Path.Combine(dir, "ModOrganizer.ini"))) return dir;
            }
        }
        catch { }
        var all = AllMo2Instances();
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>MO2 runs tools through its virtual file system (usvfs); new files then land in "Overwrite".</summary>
    public static bool UnderMo2()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Any(m => m.ModuleName.StartsWith("usvfs", StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    /// <summary>The MO2 folder for a path the player picked or pasted (the folder, or the ModOrganizer.ini in it); null when it doesn't exist.</summary>
    public static string? Mo2InstanceFrom(string? picked)
    {
        if (string.IsNullOrEmpty(picked)) return null;
        if (File.Exists(picked) && Path.GetFileName(picked).Equals("ModOrganizer.ini", StringComparison.OrdinalIgnoreCase)) picked = Path.GetDirectoryName(picked)!;
        return Directory.Exists(picked) ? picked : null;
    }

    /// <summary>Every Witcher 3 MO2 instance in the default location, whatever game path it points at.</summary>
    public static List<string> AllMo2Instances() => FindMo2Instances("");

    static List<string> FindMo2Instances(string gameDir)
    {
        var list = new List<string>();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (!Directory.Exists(root)) return list;
        foreach (var inst in Directory.GetDirectories(root))
        {
            var ini = Path.Combine(inst, "ModOrganizer.ini");
            if (!File.Exists(ini)) continue;
            var text = File.ReadAllText(ini);
            if (text.Contains("Witcher 3", StringComparison.OrdinalIgnoreCase)) list.Add(inst);
        }
        return list;
    }

    void ReadMo2(string instance, string gameDir, bool explicitInstance)
    {
        var iniPath = Path.Combine(instance, "ModOrganizer.ini");
        var ini = File.Exists(iniPath) ? ParseIni(File.ReadAllLines(iniPath)) : new();

        var gamePath = Unwrap(ini.GetValueOrDefault("gamePath"));
        if (!explicitInstance && gamePath.Length > 0 &&
            !string.Equals(Path.GetFullPath(gamePath).TrimEnd('\\'), Path.GetFullPath(gameDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return; // an instance for a different copy of the game

        var baseDir = Unwrap(ini.GetValueOrDefault("base_directory"));
        if (baseDir.Length == 0) baseDir = instance;
        var modsDir = Unwrap(ini.GetValueOrDefault("mod_directory")).Replace("%BASE_DIR%", baseDir);
        if (modsDir.Length == 0) modsDir = Path.Combine(baseDir, "mods");
        var profile = Unwrap(ini.GetValueOrDefault("selected_profile"));
        if (profile.Length == 0) profile = "Default";
        var modlist = Path.Combine(baseDir, "profiles", profile, "modlist.txt");
        if (!Directory.Exists(modsDir) || !File.Exists(modlist)) return;

        // modlist.txt: first line = highest priority; '+' enabled, '-' disabled.
        // An MO2 mod mirrors the game folder: <mod>\mods\modFoo\content\... and <mod>\dlc\dlcFoo\content\...
        int n = 0, extra = 0;
        foreach (var line in File.ReadAllLines(modlist))
        {
            if (!line.StartsWith('+')) continue;
            var mod = line[1..].Trim();
            var dir = Path.Combine(modsDir, mod);
            if (!Directory.Exists(dir)) continue;
            var meta = Path.Combine(dir, "meta.ini");
            int? id = null;
            if (File.Exists(meta) && ParseIni(File.ReadAllLines(meta)).TryGetValue("modid", out var mid) && int.TryParse(mid, out var parsed) && parsed > 0) id = parsed;
            bool any = false;
            foreach (var kind in new[] { "mods", "dlc" })
            {
                var sub = Path.Combine(dir, kind);
                if (!Directory.Exists(sub)) continue;
                foreach (var folder in Directory.GetDirectories(sub))
                {
                    var name = Path.GetFileName(folder);
                    if (_byFolder.ContainsKey(name)) continue; // a higher-priority MO2 mod already provides this folder
                    _byFolder[name] = new PackageInfo(mod, id, "MO2");
                    ExtraMods.Add(new ModSource(folder, kind, mod, id, "MO2"));
                    n++; any = true;
                }
            }
            if (any) extra++;
        }
        if (n > 0) Notes.Add($"MO2 ({Path.GetFileName(instance)}, profile {profile}): {n} mod folder(s) from {extra} enabled mod(s).");
    }

    public static Dictionary<string, string> ParseIni(IEnumerable<string> lines)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines)
        {
            int eq = l.IndexOf('=');
            if (eq <= 0 || l.StartsWith('[') || l.StartsWith(';')) continue;
            d.TryAdd(l[..eq].Trim(), l[(eq + 1)..].Trim());
        }
        return d;
    }
}
