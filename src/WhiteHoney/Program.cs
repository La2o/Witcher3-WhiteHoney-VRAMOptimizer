using System.Diagnostics;
using WhiteHoney;

const string Usage = """
White Honey - VRAM Optimizer
Ranks your Witcher 3 texture and mesh mods by the VRAM and geometry they add, and plans a budget.
Most players only need the double-click shortcuts next to this exe (see START HERE.txt).

Usage: WhiteHoney [options]
 Scan (default, read-only)
  --game <dir>              The Witcher 3 folder (auto-detected for Steam/GOG/Epic or when run from inside it; otherwise asked for once)
  --budget <GB>             VRAM your mods may add during normal play (default: from your GPU and in-game settings)
  --vram <GB> [--gpu <name>]  Your graphics card's VRAM (default: detected)
  --settings <file>         The game's dx12user.settings or user.settings (default: detected)
  --mods-settings <file>    The game's mods.settings with load order priorities (default: detected)
  --out <dir>               Where to write reports (default: next to the exe)
  --mo2 <instance dir>      Read mods from a Mod Organizer 2 instance (auto-detected, or asked for once)
  --top <n>                 Rows to show in the console (default 25)
  --no-vanilla              Skip comparing against the base game files (faster, less accurate)
  --no-open                 Don't open the HTML report (or Explorer) when done
  --json                    Print the JSON report to stdout instead of the table
 Apply the plan (optional)
  --make-budget-fix         Write ONE extra mod folder with the planned textures shrunk (your mods are untouched)
  --include-new-items       Also shrink new armor, weapons and other added content to 2K (they only cost VRAM while used)
  --new-items-to 1k         ... to 1K instead (softer up close; fine on small screens like the Steam Deck)
  --remove-budget-fix       Delete it again
  --exclude "<mod>;<mod>"   Never shrink these mods (this run only; folder name or mod manager name, * matches anything)
  --exclude-file <path>     Mods to never shrink, one per line (default: "White Honey - Excluded mods.txt" next to the exe, made for you)
 Measure real VRAM
  --live [--label <name>] [--seconds <n>]   Record the game's actual VRAM use while you play (Ctrl+C to stop)
""";

var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "no-vanilla", "no-open", "json", "help", "pause", "make-budget-fix", "include-new-items", "remove-budget-fix", "live", "ask-mo2", "ask-game", "ask", "stress-all", "as-wine", "vanilla-check" };
var opts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    var a = args[i];
    if (!a.StartsWith("--")) { opts["game"] = a; continue; }
    var key = a[2..];
    if (flags.Contains(key)) opts[key] = "1";
    else opts[key] = i + 1 < args.Length ? args[++i] : null;
}
if (opts.ContainsKey("help")) { Console.WriteLine(Usage); return 0; }
if (opts.ContainsKey("as-wine")) Platform.IsWine = true; // test hook: behave as under Wine on Linux

DisableQuickEdit();
// A crash should leave a message, not a silently closed window.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var msg = $"White Honey crashed: {e.ExceptionObject}";
    try { Console.Error.WriteLine(msg); File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "White-Honey-Crash.log"), $"{DateTime.Now}: {msg}{Environment.NewLine}"); } catch { }
};

// Developer self-tests (not part of the user-facing help).
if (opts.TryGetValue("dump-cache", out var dumpCache) && dumpCache != null)
    return DevTools.DumpCache(dumpCache, int.Parse(opts.GetValueOrDefault("count") ?? "20"), opts.GetValueOrDefault("filter"));
if (opts.TryGetValue("verify-cache", out var verifyCache) && verifyCache != null)
    return DevTools.VerifyCache(verifyCache, int.Parse(opts.GetValueOrDefault("samples") ?? "500"));
if (opts.TryGetValue("verify-shrink", out var shrinkCache) && shrinkCache != null)
    return DevTools.VerifyShrink(shrinkCache, int.Parse(opts.GetValueOrDefault("samples") ?? "300"));
if (opts.ContainsKey("vanilla-check"))
    return DevTools.VanillaCheck(GameFinder.Find(opts.GetValueOrDefault("game")) ?? throw new Exception("game not found"), int.Parse(opts.GetValueOrDefault("every") ?? "1"));
if (opts.TryGetValue("make-fixtures", out var fixturesOut) && fixturesOut != null)
    return Fixtures.Make(GameFinder.Find(opts.GetValueOrDefault("game")) ?? throw new Exception("game not found"), fixturesOut);
if (opts.TryGetValue("ingame-test", out var ingameCount) && ingameCount != null)
    return Fixtures.MakeIngameTest(GameFinder.Find(opts.GetValueOrDefault("game")) ?? throw new Exception("game not found"),
        opts.GetValueOrDefault("to") ?? throw new Exception("--to <mods dir> is required"), int.Parse(ingameCount), opts.GetValueOrDefault("filter"), int.Parse(opts.GetValueOrDefault("halvings") ?? "1"));

var outDir = opts.GetValueOrDefault("out") ?? AppContext.BaseDirectory;
bool quiet = opts.ContainsKey("json");
void Log(string s) { if (!quiet) Console.Error.WriteLine(s); }

// Remember folders the user gave us once (game, MO2), so the shortcuts keep working.
var settingsFile = ToolSettings.DefaultPath;

// The tool doesn't have to live inside the game folder: it can be unzipped anywhere.
var game = GameFinder.Find(opts.GetValueOrDefault("game"), ToolSettings.Read(settingsFile, "game"));
if (game == null && !quiet && (!Console.IsInputRedirected || opts.ContainsKey("ask-game"))) // --ask-game: test hook
{
    game = AskForGame();
    if (game != null)
    {
        ToolSettings.Write(settingsFile, "game", game);
        Console.WriteLine("Saved: next time your game is found automatically.");
    }
}
if (game == null)
{
    Console.Error.WriteLine("Could not find The Witcher 3. Pass it with --game \"D:\\Games\\The Witcher 3\".");
    Pause();
    return 2;
}
var modRoot = opts.GetValueOrDefault("mod-root") ?? game;
var modsSettings = opts.GetValueOrDefault("mods-settings") ?? (opts.ContainsKey("mod-root") ? Path.Combine(modRoot, "mods.settings") : Scanner.DefaultModsSettings);

var ws = new Workspace(game, settingsFile, opts.GetValueOrDefault("mod-root"), modsSettings, opts.GetValueOrDefault("mo2"))
{
    UserSettingsFile = Platform.FromUser(opts.GetValueOrDefault("settings")),
    CompareVanilla = !opts.ContainsKey("no-vanilla"),
};

if (opts.ContainsKey("remove-budget-fix"))
{
    var removed = ws.RemoveFix();
    if (removed.Count > 0)
    {
        Console.WriteLine("The budget fix was removed. Everything is back as it was.");
        if (removed.Any(f => f.Contains(BudgetFix.Mo2FixModName, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine("(In Mod Organizer 2 you can now also delete the empty \"" + BudgetFix.Mo2FixModName + "\" mod.)");
    }
    else
    {
        Console.WriteLine("No budget fix found, so there was nothing to remove.");
        Console.WriteLine("(Mod Organizer 2: if a \"" + BudgetFix.Mo2FixModName + "\" mod is still in MO2's list, untick or delete it there.)");
    }
    return 0;
}
if (opts.ContainsKey("live"))
    return LiveMonitor.Run(opts.GetValueOrDefault("process") ?? "witcher3", opts.GetValueOrDefault("label") ?? "session",
        int.TryParse(opts.GetValueOrDefault("seconds"), out var secs) ? secs : 0, outDir, ws.ExistingFix() != null,
        samplesIn: opts.GetValueOrDefault("samples") == "-" ? Console.In : null);

Log($"White Honey v{Report.Version} - scanning {Platform.Shown(game)}");
bool interactive = !quiet && (!Console.IsInputRedirected || opts.ContainsKey("ask-mo2")); // --ask-mo2: test hook

int lastPct = -1;
var progressLock = new object();
void ShowProgress(int done, int total)
{
    lock (progressLock)
    {
        if (quiet || total < 20) return;
        int pct = done * 100 / total;
        if (pct == lastPct && done != total) return;
        lastPct = pct;
        Console.Error.Write($"\r  Scanning mods: {done:N0} / {total:N0} ({pct}%)   ");
        if (done == total) Console.Error.WriteLine();
    }
}

ScanResult result;
try
{
    result = ws.Scan(ws.Mo2, Log, ShowProgress);
    // Nothing found in the game folder: MO2 keeps mods outside it. Ask once, then remember.
    if (interactive && ws.WorthAskingForMo2(result))
    {
        var picked = AskForMo2();
        if (picked != null)
        {
            lastPct = -1;
            result = ws.Scan(picked, Log, ShowProgress);
            if (result.Mods.Count > 0)
            {
                ws.RememberMo2(picked);
                Console.WriteLine("Saved: next time your MO2 mods are found automatically.");
            }
            else Console.WriteLine("No mods found there either. Pick the folder that contains ModOrganizer.ini.");
        }
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine("Scan failed: " + ex.Message);
    Pause();
    return 1;
}

ws.ExcludeFile = Platform.FromUser(opts.GetValueOrDefault("exclude-file")) ?? Exclusions.DefaultPath;
long budget = ws.Prepare(result, Gb(opts.GetValueOrDefault("vram")), opts.GetValueOrDefault("gpu"), Gb(opts.GetValueOrDefault("budget")),
    Exclusions.Parse(opts.GetValueOrDefault("exclude")), createExcludeFile: !quiet, log: Log);
int newItemsMax = opts.GetValueOrDefault("new-items-to")?.Trim().ToLowerInvariant() is "1k" or "1024" ? 1024 : 2048;
var plan = BudgetPlan.Build(result, budget, includeNew: opts.ContainsKey("include-new-items"), newItemsMax);
if (opts.ContainsKey("stress-all")) // developer stress test: halve every loaded 2K+ texture
    plan.Steps = result.Mods.Where(m => !m.ExcludedFromFix).SelectMany(m => m.Assets.Where(BudgetPlan.Shrinkable).Select(a => new PlanStep(m, a, 1, a.GpuBytes * 3 / 4))).ToList();

if (quiet)
{
    using var stdout = Console.OpenStandardOutput();
    Report.WriteJson(result, plan, stdout);
    return 0;
}

Directory.CreateDirectory(outDir);
result.HistoryDiff = History.SaveAndDiff(result, outDir);
bool makingFix = opts.ContainsKey("make-budget-fix");
if (!makingFix) Report.PrintConsole(result, plan, int.TryParse(opts.GetValueOrDefault("top"), out var top) ? top : 25);

string? openAfter = null;
if (makingFix)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 72));
    bool asked = false;
    if (opts.ContainsKey("ask") && !quiet)
    {
        // Show what each choice involves (counts, size, rough time) before asking.
        asked = true;
        var choices = FixChoices.Build(result, budget);
        var normal = choices.Normal;
        var to2k = choices.To2K;
        var to1k = choices.To1K;

        // Every mod the fix could touch, so the player can keep some untouched.
        var touched = choices.Touched();
        if (touched.Count > 0)
        {
            Console.WriteLine("The fix could touch these mods:");
            for (int i = 0; i < touched.Count; i++)
                Console.WriteLine($"  {i + 1}) {touched[i].Mod.Name}: {touched[i].Count:N0} texture(s), saves {Report.Size(touched[i].Saves)}");
            Console.Write("Leave any of these mods untouched? Type their numbers (for example 1,3), or press Enter to trim them all: ");
            var picked = (Console.ReadLine() ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => int.TryParse(t, out var n) && n >= 1 && n <= touched.Count ? touched[n - 1].Mod : null).OfType<ModReport>().Distinct().ToList();
            if (picked.Count > 0)
            {
                foreach (var m in picked) m.ExcludedFromFix = true;
                Console.WriteLine($"Leaving untouched: {string.Join(", ", picked.Select(m => m.Name))}");
                if (Ask("Remember this for next time?"))
                    Console.WriteLine(Exclusions.Append(ws.ExcludeFile, picked.Select(m => m.Name))
                        ? $"Saved to \"{Path.GetFileName(ws.ExcludeFile)}\" next to the tool."
                        : "Couldn't save that list this time (the file can't be written). The mods are still left alone for this run.");
                Console.WriteLine();
                // Plan again without them, so the questions below show the new numbers.
                plan = BudgetPlan.Build(result, budget, includeNew: opts.ContainsKey("include-new-items"), newItemsMax);
                choices = FixChoices.Build(result, budget);
                normal = choices.Normal;
                to2k = choices.To2K;
                to1k = choices.To1K;
            }
        }
        var chosen = new List<PlanStep>();
        if (normal.Current <= normal.Budget)
            Console.WriteLine($"Normal play: your mods add about {Report.Size(normal.Current)} on top of the game; your card has room for {Report.Size(normal.Budget)}. Nothing to fix there.");
        else if (normal.Steps.Count == 0)
            Console.WriteLine($"Normal play: your mods add {Report.Size(normal.Current - normal.Budget)} more than your card has room for, but no texture is big enough to halve. Swap the heaviest mod for a lighter version instead.");
        else
        {
            Console.WriteLine($"Normal play: your mods add about {Report.Size(normal.Current)} on top of the game, but your card only has room for {Report.Size(normal.Budget)} ({Report.Size(normal.Current - normal.Budget)} too much).");
            Console.WriteLine($"  Fix: halve {normal.Steps.Count:N0} texture(s), saving about {Report.Size(normal.Steps.Sum(s => s.Saves))}. {BudgetFix.DescribeWork(normal.Steps)}");
            if (Ask("Halve these textures?")) chosen.AddRange(normal.Steps);
        }
        if (normal.Current > normal.Budget && Report.SettingsTips(result) is { Length: > 0 } tips)
            Console.WriteLine($"  Lower settings also give mods more room (estimates): {tips}.");
        Console.WriteLine();
        if (to1k.Count == 0)
            Console.WriteLine("New armor, weapons and other added content: none have textures of 2K or larger. Nothing to shrink there.");
        else
        {
            // 2K first; 1K is offered on top, since it's softer up close (fine on small screens like the Steam Deck's).
            List<PlanStep>? pick = null;
            if (to2k.Count > 0)
            {
                Console.WriteLine($"New armor, weapons and other added content: {to2k.Count:N0} texture(s) are 4K or larger. They only use VRAM while worn or spawned.");
                Console.WriteLine($"  Shrinking them to 2K saves up to {Report.Size(to2k.Sum(s => s.Saves))} while they're in use. {BudgetFix.DescribeWork(to2k)}");
                if (Ask("Also shrink these to 2K?")) pick = to2k;
            }
            else Console.WriteLine($"New armor, weapons and other added content: {to1k.Count:N0} texture(s) are 2K. They only use VRAM while worn or spawned.");
            if (to2k.Count == 0 || pick != null)
            {
                long more = to1k.Sum(s => s.Saves) - (pick?.Sum(s => s.Saves) ?? 0);
                Console.WriteLine($"  Going to 1K ({to1k.Count:N0} texture(s), 2K ones included) saves {(pick != null ? "another" : "up to")} {Report.Size(more)}. "
                    + $"They look softer up close; on small screens like the Steam Deck's that's hard to see. {BudgetFix.DescribeWork(to1k)}");
                if (Ask(pick != null ? "Go further, to 1K instead?" : "Shrink these to 1K?")) pick = to1k;
            }
            if (pick != null) chosen.AddRange(pick);
        }
        plan.Steps = chosen;
        Console.WriteLine();
    }
    if (plan.Steps.Count == 0)
    {
        if (asked) Console.WriteLine("Nothing was changed.");
        else
        {
            Console.WriteLine(plan.Current <= plan.Budget
                ? "Nothing to fix: during normal play your mods already fit in your card's VRAM."
                : "Nothing to fix: none of your mods' textures are big enough to halve (2K or larger).");
            if (!plan.IncludeNew && result.WhenUsedDelta > 0)
                Console.WriteLine("New armor, weapons and other added content were left alone (add --include-new-items to shrink them to 2K, or also --new-items-to 1k).");
        }
    }
    else
    {
        var target = ws.TargetForFix(opts.ContainsKey("mo2-output"), opts.GetValueOrDefault("mo2-output"));
        Console.WriteLine($"Writing the fix: {plan.Steps.Count:N0} texture(s). Leave this window open until it says DONE.");
        var started = Stopwatch.StartNew();
        var status = new BudgetFix.BuildStatus();
        // Progress runs on its own thread and only reads counters, so a blocked console can never stall the work.
        var buildDone = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            while (!buildDone.Wait(500))
            {
                int done = Volatile.Read(ref status.Done), total = Math.Max(1, status.Total);
                string eta = "";
                if (done >= 3 && done < total)
                {
                    var left = TimeSpan.FromSeconds(started.Elapsed.TotalSeconds / done * (total - done));
                    eta = left.TotalMinutes >= 1 ? $" · about {Math.Ceiling(left.TotalMinutes)} min left" : $" · about {Math.Max(1, Math.Ceiling(left.TotalSeconds))} s left";
                }
                Console.Write(status.Phase == "saving"
                    ? $"\r  Saving the fix...                                                   "
                    : $"\r  Writing fix: {done:N0} / {total:N0} textures ({done * 100 / total}%){eta}        ");
            }
            Console.WriteLine($"\r  Writing fix: {status.Done:N0} / {status.Total:N0} textures (100%), saved.                         ");
        }) { IsBackground = true };
        ui.Start();
        BudgetFix.Outcome outcome;
        try { outcome = ws.ApplyFix(result, plan, target, outDir, Log, status); }
        finally { buildDone.Set(); ui.Join(2000); }
        Console.WriteLine($"  Finished in {started.Elapsed.TotalSeconds:0}s.");
        Console.WriteLine();
        result.BudgetFix = outcome.Path;
        Console.WriteLine($"DONE: {outcome.Textures} texture(s) shrunk, saving up to {Report.Size(outcome.SavedBytes)} of VRAM.");
        Console.WriteLine();
        Console.WriteLine("The fix is ONE new mod folder (your mods were not changed):");
        Console.WriteLine($"  {Platform.Shown(outcome.Path)}");
        if (outcome.Skipped.Count > 0) Console.WriteLine($"({outcome.Skipped.Count} texture(s) were skipped because their layout can't be shrunk safely.)");
        Console.WriteLine();
        if (target.Mo2ModFolder)
        {
            Console.WriteLine($"MOD ORGANIZER 2: the fix was saved as a new MO2 mod called \"{BudgetFix.Mo2FixModName}\".");
            Console.WriteLine("  1. In MO2, press F5 (or the Refresh button) so it shows up in the left list.");
            Console.WriteLine("  2. Tick its checkbox to enable it. That's it; start the game from MO2 as usual.");
            Console.WriteLine("  To undo: untick it, or double-click \"Undo budget fix.bat\".");
        }
        else if (target.ViaMo2)
        {
            Console.WriteLine("MOD ORGANIZER 2: your MO2 mods folder wasn't found, so the fix was saved as a ready-made mod folder:");
            Console.WriteLine($"  {Platform.Shown(Path.GetDirectoryName(target.Dir!)!)}");
            Console.WriteLine("  Copy that folder into MO2's mods folder, then enable it.");
        }
        else Console.WriteLine("Start the game as usual. To undo, double-click \"Undo budget fix.bat\".");
        openAfter = outcome.Path;
    }
    Console.WriteLine(new string('=', 72));
}

var html = Workspace.WriteReports(result, plan, outDir);
Console.WriteLine();
Console.WriteLine($"Report: {Platform.Shown(html)}");
if (!opts.ContainsKey("no-open") && OperatingSystem.IsWindows())
    try
    {
        // After a fix, show the new folder in Explorer; after a scan, open the report.
        if (openAfter != null && Directory.Exists(openAfter)) Process.Start("explorer.exe", $"/select,\"{openAfter}\"");
        else if (!makingFix) Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
    }
    catch { }
Pause();
return 0;

void Pause()
{
    // Double-clicked (no args): keep the window open so the summary can be read.
    if ((args.Length == 0 || opts.ContainsKey("pause")) && !Console.IsInputRedirected)
    {
        Console.WriteLine(Platform.IsWine ? "Press Enter to close." : "Press any key to close.");
        if (Platform.IsWine) Console.ReadLine(); else Console.ReadKey(true);
    }
}

// Windows "QuickEdit": clicking in a console window starts a text selection and pauses every
// program writing to it until Esc/Enter. Players click windows; switch it off for this process.
static void DisableQuickEdit()
{
    if (!OperatingSystem.IsWindows() || Console.IsInputRedirected) return;
    try
    {
        var h = NativeConsole.GetStdHandle(-10); // STD_INPUT_HANDLE
        if (NativeConsole.GetConsoleMode(h, out uint mode))
            NativeConsole.SetConsoleMode(h, (mode & ~0x0040u) | 0x0080u); // clear QUICK_EDIT, set EXTENDED_FLAGS
    }
    catch { }
}

// Y/N question; works with a keyboard or piped input.
bool Ask(string question)
{
    // Under Wine single key presses aren't reliable in a Linux terminal, so the answer is typed and ends with Enter.
    bool lineInput = Console.IsInputRedirected || Platform.IsWine;
    bool echoed = lineInput && !Console.IsInputRedirected; // the terminal already shows what was typed
    while (true)
    {
        Console.Write($"{question} [Y/N]{(echoed ? " then Enter" : "")} ");
        string? answer;
        if (lineInput) answer = Console.ReadLine();
        else { var k = Console.ReadKey(intercept: true); answer = k.KeyChar.ToString(); }
        if (answer == null) { Console.WriteLine("N"); return false; }
        answer = new string(answer.Where(char.IsLetter).ToArray()).ToUpperInvariant(); // ignores BOMs and stray characters
        if (answer.StartsWith('Y')) { if (!echoed) Console.WriteLine("Y"); return true; }
        if (answer.StartsWith('N')) { if (!echoed) Console.WriteLine("N"); return false; }
        if (!lineInput) Console.WriteLine();
    }
}

string? AskForMo2()
{
    Console.WriteLine();
    Console.WriteLine("No mods were found in your game folder.");
    var found = ModManagers.AllMo2Instances();
    Console.WriteLine("If you use Mod Organizer 2, pick your setup:");
    for (int i = 0; i < found.Count; i++) Console.WriteLine($"  {i + 1}) {found[i]}");
    Console.Write("Type its number, paste a different MO2 folder, or press Enter to skip: ");
    var answer = ReadPath();
    if (string.IsNullOrEmpty(answer)) return null;
    if (int.TryParse(answer, out var n) && n >= 1 && n <= found.Count) return found[n - 1];
    return ModManagers.Mo2InstanceFrom(answer);
}

static string? ReadPath() => Platform.FromUser(Console.ReadLine());

string? AskForGame()
{
    Console.WriteLine();
    Console.WriteLine("Couldn't find The Witcher 3 by itself.");
    Console.Write("Drag your Witcher 3 folder (the one with \"bin\" and \"content\") into this window, or paste its path, then press Enter: ");
    var answer = ReadPath();
    if (string.IsNullOrEmpty(answer)) return null;
    if (GameFinder.FromPath(answer) is { } dir) return dir;
    Console.WriteLine("That doesn't look like the Witcher 3 folder (there's no content folder and bin\\x64 or bin\\x64_dx12\\witcher3.exe inside).");
    return null;
}

static double? Gb(string? s) => double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

static class NativeConsole
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int nStdHandle);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
