using System.Diagnostics;

namespace WhiteHoney;

/// <summary>WhiteHoney.ini next to the tool: folders the player gave once (game, MO2, where the fix was saved).</summary>
public static class ToolSettings
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "WhiteHoney.ini");

    public static string? Read(string file, string key)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var line = File.ReadAllLines(file).FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            var v = line?[(key.Length + 1)..].Trim();
            return string.IsNullOrEmpty(v) ? null : v;
        }
        catch { return null; }
    }

    public static void Write(string file, string key, string value)
    {
        try
        {
            var lines = File.Exists(file) ? File.ReadAllLines(file).Where(l => !l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)).ToList() : new List<string>();
            lines.Add($"{key}={value}");
            File.WriteAllLines(file, lines);
        }
        catch { }
    }
}

/// <summary>What the fix can do, split the way players are asked about it: normal play first, then new items to 2K or 1K.</summary>
public sealed class FixChoices
{
    /// <summary>The plan for normal play only (new items left alone).</summary>
    public required BudgetPlan Normal;
    /// <summary>New items' textures of 4K or larger, shrunk to 2K.</summary>
    public required List<PlanStep> To2K;
    /// <summary>New items' textures of 2K or larger, shrunk to 1K (the 4K ones included).</summary>
    public required List<PlanStep> To1K;

    public static FixChoices Build(ScanResult r, long budget)
    {
        List<PlanStep> NewItems(int max) => BudgetPlan.Build(r, budget, includeNew: true, max).Steps.Where(s => !s.Asset.AlwaysLoaded).ToList();
        return new FixChoices { Normal = BudgetPlan.Build(r, budget, includeNew: false), To2K = NewItems(2048), To1K = NewItems(1024) };
    }

    /// <summary>Every mod the fix could touch (To1K holds all of the new-items choices), biggest saving first.</summary>
    public List<(ModReport Mod, int Count, long Saves)> Touched() =>
        Normal.Steps.Concat(To1K).GroupBy(s => s.Mod).Select(g => (Mod: g.Key, Count: g.Count(), Saves: g.Sum(s => s.Saves)))
            .OrderByDescending(x => x.Saves).ToList();
}

/// <summary>
/// One game install plus the files this tool keeps for it. The console tool and the window both scan, plan,
/// apply and undo through this, so they always agree.
/// </summary>
public sealed class Workspace
{
    public string Game { get; }
    /// <summary>A folder laid out like the game root to read mods from instead of the game (--mod-root; testing).</summary>
    public string? ModRootOverride { get; }
    public string ModRoot => ModRootOverride ?? Game;
    /// <summary>The game's mods.settings (load order priorities).</summary>
    public string ModsSettings { get; }
    /// <summary>The Mod Organizer 2 instance to read mods from, if the player gave one.</summary>
    public string? Mo2 { get; set; }
    /// <summary>WhiteHoney.ini.</summary>
    public string IniFile { get; }
    /// <summary>The list of mods the fix must leave alone.</summary>
    public string ExcludeFile { get; set; } = Exclusions.DefaultPath;
    /// <summary>The game's dx12user.settings or user.settings when the player named one; otherwise detected.</summary>
    public string? UserSettingsFile { get; set; }
    public bool CompareVanilla { get; set; } = true;
    /// <summary>Every mods folder outside the game's own where a fix may be, so Undo finds it however it was made.</summary>
    public List<string> FixDirs { get; }

    public Workspace(string game, string? iniFile = null, string? modRoot = null, string? modsSettings = null, string? mo2 = null)
    {
        Game = game;
        IniFile = iniFile ?? ToolSettings.DefaultPath;
        ModRootOverride = modRoot;
        ModsSettings = modsSettings ?? (modRoot != null ? Path.Combine(modRoot, "mods.settings") : Scanner.DefaultModsSettings);
        Mo2 = mo2 ?? ToolSettings.Read(IniFile, "mo2");
        // Where an MO2 user's fix was saved last time (a real MO2 mod folder, outside the virtual game folder).
        var savedFixDir = ToolSettings.Read(IniFile, "fixdir");
        FixDirs = BudgetFix.KnownFixDirs(savedFixDir,
            new[] { Mo2, ModManagers.RunningMo2Instance() }.Concat(ModManagers.AllMo2Instances()), AppContext.BaseDirectory);
    }

    /// <summary>Read-only scan of the installed mods.</summary>
    public ScanResult Scan(string? mo2Instance, Action<string>? log = null, Action<int, int>? progress = null) => new Scanner(new ScanOptions
    {
        GameDir = Game,
        ModRoot = ModRootOverride,
        Mo2Instance = mo2Instance,
        ModsSettings = ModsSettings,
        SettingsFile = UserSettingsFile,
        CompareVanilla = CompareVanilla,
        Log = log,
        Progress = progress,
    }).Scan();

    /// <summary>
    /// Nothing found in the game folder and no MO2 folder known yet: Mod Organizer 2 keeps mods outside the game
    /// folder, so it is worth asking the player which MO2 setup is theirs.
    /// </summary>
    public bool WorthAskingForMo2(ScanResult r) =>
        r.Mods.Count == 0 && Mo2 == null && ModRootOverride == null && !ModManagers.UnderMo2() && ModManagers.AllMo2Instances().Count > 0;

    /// <summary>Remembers the MO2 folder so the next scan finds the mods by itself.</summary>
    public void RememberMo2(string instance)
    {
        Mo2 = instance;
        ToolSettings.Write(IniFile, "mo2", instance);
    }

    /// <summary>
    /// After a scan: works out the room for mods, marks the mods on the exclusion list, and checks whether a fix that
    /// is already applied still matches the mods. Returns the room for mods in bytes.
    /// </summary>
    /// <param name="vramGb">The card's VRAM when the player named it; otherwise the detected card is used.</param>
    /// <param name="budgetGb">Room for mods when the player named it; otherwise it comes from the card and settings.</param>
    /// <param name="alsoExclude">Mods to leave alone for this run only, on top of the list file.</param>
    public long Prepare(ScanResult r, double? vramGb = null, string? gpuName = null, double? budgetGb = null,
        IEnumerable<string>? alsoExclude = null, bool createExcludeFile = true, Action<string>? log = null)
    {
        if (vramGb is > 0)
            r.Gpu = new GpuInfo(gpuName ?? r.Gpu?.Name ?? "GPU", (long)(vramGb.Value * (1L << 30)));

        long budget;
        if (budgetGb != null)
        {
            budget = (long)(budgetGb.Value * (1L << 30));
            r.BudgetWhy = "set with --budget";
        }
        else (budget, r.BudgetWhy) = GpuInfo.SuggestBudget(r.Gpu?.VramBytes ?? 8L << 30, r.Settings);
        // Mods the player wants left alone: the list file next to the tool (made on first run) plus this run's extras.
        if (createExcludeFile) Exclusions.EnsureFile(ExcludeFile);
        r.ExcludeFile = ExcludeFile;
        foreach (var unmatched in Exclusions.Apply(r.Mods, Exclusions.Load(ExcludeFile).Concat(alsoExclude ?? Enumerable.Empty<string>())))
            log?.Invoke($"Excluded mods list: no installed mod matches \"{unmatched}\"");
        r.BudgetFix ??= ExistingFix();
        if (r.BudgetFix != null) r.FixCheck = BudgetFix.Check(r.BudgetFix, r);
        return budget;
    }

    /// <summary>The applied fix's folder, or null when there is none.</summary>
    public string? ExistingFix() => BudgetFix.FindExisting(ModRoot, FixDirs);

    /// <summary>Looks again for an applied fix and whether it still matches the mods and the exclusions as they are now.</summary>
    public void RefreshFix(ScanResult r)
    {
        r.BudgetFix = ExistingFix();
        r.FixCheck = r.BudgetFix != null ? BudgetFix.Check(r.BudgetFix, r) : null;
    }

    /// <param name="Dir">The mods folder the fix is written to; null for the game's own.</param>
    /// <param name="ViaMo2">Running through Mod Organizer 2 (or forced for testing).</param>
    /// <param name="Mo2ModFolder">The fix becomes a mod in MO2's own mods folder.</param>
    public sealed record FixTarget(string? Dir, bool ViaMo2, bool Mo2ModFolder);

    /// <summary>
    /// Where a new fix goes. Through MO2, writing into the virtual game folder is unreliable, so the fix becomes a
    /// real MO2 mod folder instead. <paramref name="forceMo2"/> does the same outside MO2, into
    /// <paramref name="mo2Instance"/> (testing).
    /// </summary>
    public FixTarget TargetForFix(bool forceMo2 = false, string? mo2Instance = null)
    {
        bool viaMo2 = ModManagers.UnderMo2() || forceMo2;
        if (!viaMo2) return new FixTarget(null, false, false);
        var inst = mo2Instance ?? Mo2 ?? ModManagers.RunningMo2Instance();
        var modsDir = inst != null ? ModManagers.Mo2ModsDir(inst) : null;
        var root = modsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "White Honey");
        return new FixTarget(Path.Combine(root, BudgetFix.Mo2FixModName, "mods"), true, modsDir != null);
    }

    /// <summary>
    /// Writes the plan as the fix folder (replacing an older fix) and keeps a step-by-step log in
    /// White-Honey-Fix.log. <paramref name="status"/> can be read from another thread to show progress.
    /// </summary>
    public BudgetFix.Outcome ApplyFix(ScanResult r, BudgetPlan plan, FixTarget target, string outDir, Action<string> log, BudgetFix.BuildStatus? status = null)
    {
        var started = Stopwatch.StartNew();
        using var fixLog = OpenLog(Path.Combine(outDir, "White-Honey-Fix.log"));
        void Trace(string s) { lock (fixLog) fixLog.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {s}"); }
        Trace($"v{Report.Version} game={Game} mo2={target.ViaMo2} steps={plan.Steps.Count}");
        Trace($"output: {target.Dir ?? "game folder"}");
        var outcome = BudgetFix.Build(r, plan, ModRoot, log, status, Trace, target.Dir, FixDirs, target.ViaMo2 ? null : ModsSettings);
        if (target.Dir != null)
        {
            ToolSettings.Write(IniFile, "fixdir", target.Dir);
            if (!FixDirs.Contains(target.Dir, StringComparer.OrdinalIgnoreCase)) FixDirs.Insert(0, target.Dir);
        }
        Trace($"finished: {outcome.Textures} written, {outcome.Skipped.Count} skipped, {started.Elapsed.TotalSeconds:0}s");
        return outcome;
    }

    /// <summary>Deletes the fix wherever it was saved, and its mods.settings entry. Returns the folders removed.</summary>
    public List<string> RemoveFix(Action<string>? say = null) => BudgetFix.Remove(ModRoot, extraDirs: FixDirs, modsSettings: ModsSettings, say: say);

    /// <summary>Writes White-Honey-Report.html, .json and White-Honey-Summary.md into <paramref name="outDir"/>; returns the HTML report's path.</summary>
    public static string WriteReports(ScanResult r, BudgetPlan plan, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var html = Path.Combine(outDir, "White-Honey-Report.html");
        var json = Path.Combine(outDir, "White-Honey-Report.json");
        using (var fs = File.Create(json)) Report.WriteJson(r, plan, fs);
        Report.WriteHtml(r, plan, html);
        File.WriteAllText(Path.Combine(outDir, "White-Honey-Summary.md"), Summary.Markdown(r, plan));
        return html;
    }

    static StreamWriter OpenLog(string path)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); return new StreamWriter(path, append: false) { AutoFlush = true }; }
        catch { return StreamWriter.Null; }
    }
}
