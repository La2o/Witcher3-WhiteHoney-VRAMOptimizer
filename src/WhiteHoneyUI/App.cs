using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace WhiteHoney.UI;

/// <summary>
/// Everything the window does: pick the game, scan, keep some mods out of the budget fix, apply or undo it, and
/// measure real VRAM. All the work goes through <see cref="Workspace"/>, like the console tool. The page only shows
/// <see cref="StateJson"/>, so every sentence the player reads is written here.
/// </summary>
sealed class App
{
    public const string Title = "White Honey - VRAM Optimizer";
    const string ScanFirst = "Press \"Scan my mods\" to see how much VRAM your mods add. The scan only reads files; it changes nothing.";
    const string NoGame = "Couldn't find The Witcher 3 by itself. Press Browse and pick your Witcher 3 folder (the one with \"bin\" and \"content\").";
    const string WrongFolder = "That doesn't look like the Witcher 3 folder (there's no content folder and bin\\x64 or bin\\x64_dx12\\witcher3.exe inside).";
    const string GamePickerTitle = "Pick your Witcher 3 folder (the one with \"bin\" and \"content\")";
    const string Mo2PickerTitle = "Pick your Mod Organizer 2 folder (the one with ModOrganizer.ini)";
    const string Mo2Question = "No mods were found in your game folder. If you use Mod Organizer 2, pick your setup:";
    const string Mo2NotThere = "That folder couldn't be opened. Pick the folder that contains ModOrganizer.ini.";
    const string NewItemsCaption = "New armor, weapons and other added content (they only use VRAM while worn or spawned):";
    const string MeasureHelp = "Records how much video memory the game really uses while you play. Measure once named \"before\", "
        + "apply the budget fix, then again named \"after\" in the same save to see the difference.";
    const string BusyText = "White Honey is busy with something else. Wait until it is done.";

    /// <summary>What a POST answers: an HTTP status and a small JSON body.</summary>
    public readonly record struct Reply(int Status, byte[] Body)
    {
        public static Reply Ok() => new(200, Obj(w => w.WriteBoolean("ok", true)));
        public static Reply Fail(int status, string error) => new(status, Obj(w => { w.WriteBoolean("ok", false); w.WriteString("error", error); }));
        public static Reply Busy() => new(409, Obj(w => { w.WriteBoolean("ok", false); w.WriteBoolean("busy", true); w.WriteString("error", BusyText); }));
        public static Reply Path(string? path) => new(200, Obj(w => { w.WriteBoolean("ok", true); if (path == null) w.WriteNull("path"); else w.WriteString("path", path); }));

        static byte[] Obj(Action<Utf8JsonWriter> body)
        {
            var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                body(w);
                w.WriteEndObject();
            }
            return ms.ToArray();
        }
    }

    enum Job { None, Scan, Apply, Undo, Report }
    enum NewItems { Leave, To2K, To1K }

    /// <summary>One line of the summary is made of pieces, some coloured or bold ("Over by 130.2 MB." in red).</summary>
    sealed record Piece(string Text, string Tone = "", bool Bold = false);

    /// <summary>A mod's numbers, worked out once per scan (the mod report sums its assets every time it is asked).</summary>
    sealed record ModRow(ModReport Mod, long Always, long WhenUsed, int Textures, int Meshes, int Flags, long GpuDelta, string Tip, string? ManagerText);

    sealed class Mo2Ask
    {
        public required List<string> Instances;
        public readonly TaskCompletionSource<string?> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly Options _opt;
    readonly string _outDir;
    readonly string _ini = ToolSettings.DefaultPath;

    /// <summary>Guards every field below. Engine calls that take time run outside it.</summary>
    readonly object _lock = new();
    /// <summary>Lets one change at a time through (exclusions, new items, game folder, starting a job).</summary>
    readonly SemaphoreSlim _gate = new(1, 1);

    Workspace? _ws;
    string? _gameShown, _gameTip;
    ScanResult? _result;
    long _budget, _alwaysTotal, _whenUsedTotal;
    FixChoices? _choices;
    BudgetPlan? _plan;
    List<ModRow> _rows = new();
    byte[]? _modsJson;
    /// <summary>Something changed since the reports were last written.</summary>
    bool _reportsDirty;
    /// <summary>The applied fix's folder (null when there is none), as last looked up.</summary>
    string? _existingFix;
    /// <summary>The outcome of the last Apply or Undo; shown in the fix status until something else changes.</summary>
    string? _fixMessage;
    bool _fixMessageWarn;
    string? _mo2Notice;
    NewItems _newItems;

    volatile Job _job;
    volatile int _scanDone, _scanTotal;
    volatile string? _scanLog;
    BudgetFix.BuildStatus? _buildStatus;
    readonly Stopwatch _buildClock = new();

    string _status = "";
    List<string> _statusNotes = new();
    int _progDone, _progTotal = 100;
    bool _progIndeterminate;
    string _gridHint = "";
    bool _gridHintWarn;
    Mo2Ask? _mo2Ask;
    int _messageId;
    string? _messageText;
    bool _messageError;

    Task? _measureTask;
    bool _measureRunning, _measureStopping;
    volatile bool _stopMeasure;
    string _measureStatus = "", _measureName = "before";
    List<LiveMonitor.Session> _sessions = new();

    // Change notification for the event streams.
    readonly object _signal = new();
    long _version;
    TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Timer _ticker;

    public App(Options opt)
    {
        _opt = opt;
        _outDir = opt.OutDir;
        // Progress of a scan or a fix being written is read from counters the engine bumps; show it a few times a second.
        _ticker = new Timer(_ => { if (_job is Job.Scan or Job.Apply) Changed(); }, null, 150, 150);
    }

    // ---------------------------------------------------------------- change notification

    void Changed()
    {
        TaskCompletionSource done;
        lock (_signal)
        {
            _version++;
            done = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        done.TrySetResult();
    }

    /// <summary>The state's version now, and a task that completes when it changes.</summary>
    public (long Version, Task Changed) Watch()
    {
        lock (_signal) return (_version, _changed.Task);
    }

    /// <summary>A fix is being written or removed, or the reports are being written: the tool must not quit now.</summary>
    public bool IsWritingFiles => _job is Job.Apply or Job.Undo or Job.Report;
    public bool IsMeasuring { get { lock (_lock) return _measureRunning; } }

    // ---------------------------------------------------------------- start, game folder

    public void StartUp()
    {
        _gridHint = HintText;
        _sessions = LiveMonitor.LoadSessions(_outDir);
        string? game = null;
        try { game = GameFinder.Find(_opt.Game, ToolSettings.Read(_ini, "game")); } catch { }
        if (game == null) _status = NoGame;
        else SetGame(game, announce: false);
    }

    string HintText => $"Untick a mod to keep it out of the budget fix. Your choice is saved in \"{Path.GetFileName(_opt.ExcludeFile)}\" next to the tool.";

    /// <summary>Opens <paramref name="game"/> and forgets the last scan. Call with the gate held (or at start).</summary>
    void SetGame(string game, bool announce)
    {
        Workspace ws;
        try
        {
            var modRoot = _opt.ModRoot;
            ws = new Workspace(game, _ini, modRoot,
                _opt.ModsSettings ?? (modRoot != null ? Path.Combine(modRoot, "mods.settings") : Scanner.DefaultModsSettings), _opt.Mo2)
            {
                ExcludeFile = _opt.ExcludeFile,
                UserSettingsFile = _opt.SettingsFile,
                CompareVanilla = _opt.CompareVanilla,
            };
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _ws = null;
                _status = "Couldn't open that game folder: " + ex.Message;
                _statusNotes = new();
            }
            Changed();
            return;
        }
        var fixExists = FindFix(ws);
        lock (_lock)
        {
            _ws = ws;
            _gameShown = Platform.Shown(game);
            _gameTip = Platform.Shown(game) + (ws.ModRootOverride != null ? $"\nMods are read from {Platform.Shown(ws.ModRootOverride)}" : "");
            _result = null;
            _plan = null;
            _choices = null;
            _fixMessage = null;
            _rows = new();
            _modsJson = null;
            _progIndeterminate = false;
            _progDone = 0;
            _progTotal = 100;
            _status = announce ? "Game folder saved. Press \"Scan my mods\"." : "Press \"Scan my mods\" to start.";
            _statusNotes = new();
            _existingFix = fixExists;
        }
        Changed();
    }

    public async Task<Reply> SetGameFromUser(string? path)
    {
        await _gate.WaitAsync();
        try
        {
            if (_job != Job.None) return Reply.Busy();
            var game = GameFinder.FromPath(Platform.FromUser(path));
            if (game == null) return Reply.Fail(400, WrongFolder);
            ToolSettings.Write(_ini, "game", game);
            SetGame(game, announce: true);
            return Reply.Ok();
        }
        finally { _gate.Release(); }
    }

    /// <summary>The Windows folder picker: for the game folder, or (<paramref name="purpose"/> "mo2") for the MO2 question.</summary>
    public async Task<Reply> Browse(string? purpose)
    {
        string? startIn;
        bool mo2 = purpose == "mo2";
        lock (_lock)
        {
            if (mo2 ? _mo2Ask == null : _job != Job.None) return Reply.Busy();
            startIn = mo2 ? null : _ws?.Game;
        }
        var picked = await Native.PickFolder(mo2 ? Mo2PickerTitle : GamePickerTitle, startIn);
        return Reply.Path(picked == null ? null : Platform.Shown(picked));
    }

    static string? FindFix(Workspace? ws)
    {
        try { return ws?.ExistingFix(); }
        catch { return null; }
    }

    /// <summary>Looks again for an applied fix and whether it still matches the mods (file reads, so outside the lock).</summary>
    void RefreshFix(Workspace ws, ScanResult r)
    {
        string? fix = null;
        BudgetFix.FixCheck? check = null;
        try
        {
            fix = ws.ExistingFix();
            check = fix != null ? BudgetFix.Check(fix, r) : null;
        }
        catch { }
        lock (_lock)
        {
            r.BudgetFix = fix;
            r.FixCheck = check;
        }
    }

    void ShowMessage(string text, bool error)
    {
        _messageId++;
        _messageText = text;
        _messageError = error;
    }

    // ---------------------------------------------------------------- scan

    public async Task<Reply> StartScan()
    {
        Workspace ws;
        await _gate.WaitAsync();
        try
        {
            lock (_lock)
            {
                if (_job != Job.None) return Reply.Busy();
                if (_ws == null) return Reply.Fail(400, NoGame);
                ws = _ws;
                _job = Job.Scan;
                _fixMessage = null;
                StartScanProgress();
            }
        }
        finally { _gate.Release(); }
        Changed();
        _ = Task.Run(() => ScanJob(ws));
        return Reply.Ok();
    }

    void StartScanProgress()
    {
        _scanDone = 0;
        _scanTotal = 0;
        _scanLog = null;
        _progIndeterminate = true;
        _status = "Reading the game's own file list (a few seconds)...";
        _statusNotes = new();
    }

    void ScanLog(string line)
    {
        if (!line.StartsWith('!')) _scanLog = line;
    }

    void ScanProgress(int done, int total)
    {
        _scanTotal = total;
        _scanDone = done;
    }

    async Task ScanJob(Workspace ws)
    {
        var notes = new ConcurrentQueue<string>();
        try
        {
            var r = ws.Scan(ws.Mo2, ScanLog, ScanProgress);
            if (r.Mods.Count == 0 && ws.WorthAskingForMo2(r))
            {
                // Wait for the player's answer, like the dialog the window used to show.
                var ask = new Mo2Ask { Instances = ModManagers.AllMo2Instances() };
                lock (_lock) _mo2Ask = ask;
                Changed();
                string? picked;
                try { picked = await ask.Answer.Task; }
                finally
                {
                    lock (_lock) _mo2Ask = null;
                    Changed();
                }
                if (picked != null)
                {
                    lock (_lock) StartScanProgress();
                    Changed();
                    var again = ws.Scan(picked, ScanLog, ScanProgress);
                    if (again.Mods.Count > 0)
                    {
                        ws.RememberMo2(picked);
                        r = again;
                    }
                    else notes.Enqueue("No mods found there either. Pick the folder that contains ModOrganizer.ini.");
                }
            }

            var budget = ws.Prepare(r, _opt.VramGb, _opt.GpuName, _opt.BudgetGb, log: notes.Enqueue);
            try
            {
                Directory.CreateDirectory(_outDir);
                r.HistoryDiff = History.SaveAndDiff(r, _outDir);
            }
            catch (Exception ex) { notes.Enqueue("Couldn't save the scan history: " + ex.Message); }

            var rows = r.Mods.Select(RowOf).ToList();
            long always = rows.Sum(x => x.Always), whenUsed = rows.Sum(x => x.WhenUsed);
            BudgetPlan plan;
            lock (_lock)
            {
                _result = r;
                _budget = budget;
                _alwaysTotal = always;
                _whenUsedTotal = whenUsed;
                _rows = rows;
                _modsJson = null;
                RebuildPlan();
                plan = _plan!;
            }
            Changed();
            if (!WriteReports(r, plan)) notes.Enqueue("Couldn't write the report next to the tool.");

            lock (_lock)
            {
                _progIndeterminate = false;
                _progTotal = 100;
                _progDone = 100;
                var done = r.Mods.Count == 0
                    ? "No texture or mesh mods found in the mods or dlc folder. Nothing to optimize."
                    : $"{r.Mods.Count} mod folder(s), scanned in {r.Elapsed.TotalSeconds:0.0}s";
                _status = notes.IsEmpty ? done : done + "\n" + string.Join("\n", notes.Take(3));
                _statusNotes = notes.Concat(r.HistoryDiff).ToList();
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _progIndeterminate = false;
                _progDone = 0;
                _status = "Scan failed: " + ex.Message;
            }
        }
        finally
        {
            var exists = FindFix(ws);
            lock (_lock)
            {
                _job = Job.None;
                _existingFix = exists;
            }
            Changed();
        }
    }

    static ModRow RowOf(ModReport m)
    {
        var modTip = m.Package != null && m.Package != m.Name
            ? $"{m.Manager}: {m.Package}{(m.NexusId != null ? $" (nexusmods.com/witcher3/mods/{m.NexusId})" : "")}" : "";
        return new ModRow(m, m.AlwaysDelta, m.WhenUsedDelta, m.Textures, m.Meshes, m.FlagCount, m.GpuDelta,
            modTip.Length > 0 ? $"{m.Name}\n{modTip}" : m.Name,
            m.Package != null && m.Package != m.Name ? $"{m.Manager}: {m.Package}" : null);
    }

    public Reply AnswerMo2(string? path, bool skip)
    {
        Mo2Ask? ask;
        lock (_lock) ask = _mo2Ask;
        if (ask == null) return Reply.Busy();
        if (skip)
        {
            ask.Answer.TrySetResult(null);
            return Reply.Ok();
        }
        var inst = ModManagers.Mo2InstanceFrom(Platform.FromUser(path));
        if (inst == null) return Reply.Fail(400, Mo2NotThere);
        ask.Answer.TrySetResult(inst);
        return Reply.Ok();
    }

    // ---------------------------------------------------------------- plan, new items

    /// <summary>Call with the lock held.</summary>
    void RebuildPlan()
    {
        if (_result == null) { _choices = null; _plan = null; return; }
        _choices = FixChoices.Build(_result, _budget);
        if (_newItems == NewItems.To2K && _choices.To2K.Count == 0 || _newItems == NewItems.To1K && _choices.To1K.Count == 0) _newItems = NewItems.Leave;
        _plan = BudgetPlan.Build(_result, _budget, includeNew: _newItems != NewItems.Leave, newItemsMax: _newItems == NewItems.To1K ? 1024 : 2048);
        _reportsDirty = true;
    }

    public async Task<Reply> SetNewItems(string? choice)
    {
        NewItems want = choice switch { "leave" => NewItems.Leave, "2k" => NewItems.To2K, "1k" => NewItems.To1K, _ => (NewItems)(-1) };
        if ((int)want < 0) return Reply.Fail(400, "Bad request.");
        await _gate.WaitAsync();
        try
        {
            lock (_lock)
            {
                if (_job != Job.None) return Reply.Busy();
                if (_result == null || _choices == null) return Reply.Fail(400, "Scan your mods first to see what the fix would do.");
                if (want == NewItems.To2K && _choices.To2K.Count == 0 || want == NewItems.To1K && _choices.To1K.Count == 0)
                    return Reply.Fail(400, "Nothing to shrink there.");
                if (want == _newItems) return Reply.Ok();
                _newItems = want;
                RebuildPlan();
            }
            Changed();
            return Reply.Ok();
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- exclusions

    /// <summary>The "Fix may trim" checkbox of one mod.</summary>
    public async Task<Reply> SetTrim(string? name, bool? trimArg)
    {
        if (name == null || trimArg is not { } trim) return Reply.Fail(400, "Bad request.");
        await _gate.WaitAsync();
        try
        {
            Workspace ws;
            ScanResult r;
            lock (_lock)
            {
                if (_job != Job.None) return Reply.Busy();
                if (_ws == null || _result == null) return Reply.Fail(400, "Scan your mods first.");
                ws = _ws;
                r = _result;
                var mod = r.Mods.FirstOrDefault(m => m.Name == name) ?? r.Mods.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (mod == null) return Reply.Fail(404, "That mod isn't in the list any more. Scan again.");
                if (mod.Error != null) return Reply.Fail(400, "A broken mod can't be trimmed.");
                if (trim == !mod.ExcludedFromFix) return Reply.Ok();

                mod.ExcludedFromFix = !trim;
                bool saved = trim ? Exclusions.Remove(ws.ExcludeFile, mod, r.Mods) : Exclusions.Append(ws.ExcludeFile, new[] { mod.Name });
                if (saved)
                {
                    // One list entry can cover several mods: read the list back so every tick matches it.
                    foreach (var m in r.Mods) m.ExcludedFromFix = false;
                    Exclusions.Apply(r.Mods, Exclusions.Load(ws.ExcludeFile));
                    _gridHint = HintText;
                    _gridHintWarn = false;
                }
                else
                {
                    _gridHint = "Couldn't save that list this time (the file can't be written). Your choice still counts until you close the window.";
                    _gridHintWarn = true;
                }
                _modsJson = null;
                _fixMessage = null;
                RebuildPlan();
            }
            Changed();
            RefreshFix(ws, r);
            var exists = FindFix(ws);
            lock (_lock) _existingFix = exists;
            Changed();
            return Reply.Ok();
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- reports

    bool WriteReports(ScanResult r, BudgetPlan plan)
    {
        try
        {
            Workspace.WriteReports(r, plan, _outDir);
            lock (_lock) _reportsDirty = false;
            return true;
        }
        catch { return false; }
    }

    public async Task<Reply> OpenReport()
    {
        var html = Path.Combine(_outDir, "White-Honey-Report.html");
        ScanResult r;
        BudgetPlan plan;
        string before;
        await _gate.WaitAsync();
        try
        {
            bool upToDate;
            lock (_lock)
            {
                if (_job != Job.None) return Reply.Busy();
                if (_result == null || _plan == null) return Reply.Fail(400, "Scan your mods first.");
                upToDate = !_reportsDirty && File.Exists(html);
            }
            if (upToDate)
            {
                OpenFile(html);
                return Reply.Ok();
            }
            lock (_lock)
            {
                r = _result;
                plan = _plan;
                before = _status;
                _job = Job.Report;
                _status = "Writing the report...";
            }
        }
        finally { _gate.Release(); }
        Changed();
        _ = Task.Run(() =>
        {
            bool ok = false;
            try { ok = WriteReports(r, plan); }
            finally
            {
                lock (_lock)
                {
                    _job = Job.None;
                    _status = ok ? before : "Couldn't write the report next to the tool (the folder can't be written).";
                }
                Changed();
            }
            if (ok) OpenFile(html);
        });
        return Reply.Ok();
    }

    void OpenFile(string html)
    {
        try { Process.Start(new ProcessStartInfo(html) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex)
        {
            lock (_lock) _status = $"Couldn't open the report: {ex.Message} It is here: {Platform.Shown(html)}";
            Changed();
        }
    }

    // ---------------------------------------------------------------- apply, undo

    public async Task<Reply> StartApply()
    {
        Workspace ws;
        ScanResult r;
        BudgetPlan plan;
        BudgetFix.BuildStatus status;
        await _gate.WaitAsync();
        try
        {
            lock (_lock)
            {
                if (_job != Job.None) return Reply.Busy();
                if (_ws == null || _result == null || _plan is not { Steps.Count: > 0 }) return Reply.Fail(400, "With these choices the fix has nothing to change.");
                ws = _ws;
                r = _result;
                plan = _plan;
                _job = Job.Apply;
                _fixMessage = null;
                _mo2Notice = null;
                status = new BudgetFix.BuildStatus();
                _buildStatus = status;
                _buildClock.Restart();
                _progIndeterminate = false;
                _progDone = 0;
                _progTotal = 100;
                _status = $"Writing the fix: {plan.Steps.Count:N0} texture(s). Leave this window open until it says DONE.";
                _statusNotes = new();
            }
        }
        finally { _gate.Release(); }
        Changed();
        _ = Task.Run(() => ApplyJob(ws, r, plan, status));
        return Reply.Ok();
    }

    void ApplyJob(Workspace ws, ScanResult r, BudgetPlan plan, BudgetFix.BuildStatus status)
    {
        try
        {
            var t = ws.TargetForFix();
            var outcome = ws.ApplyFix(r, plan, t, _outDir, _ => { }, status);
            lock (_lock) r.BudgetFix = outcome.Path;
            RefreshFix(ws, r);
            BudgetPlan current;
            lock (_lock)
            {
                _buildStatus = null;
                _progDone = _progTotal = 100;
                _status = $"Writing fix: {outcome.Textures:N0} texture(s), saved. Finished in {_buildClock.Elapsed.TotalSeconds:0}s.";
                current = _plan ?? plan;
            }
            Changed();
            WriteReports(r, current);

            var msg = $"DONE: {outcome.Textures} texture(s) shrunk, saving up to {Report.Size(outcome.SavedBytes)} of VRAM. "
                + $"The fix is ONE new mod folder (your mods were not changed): {Platform.Shown(outcome.Path)}";
            if (outcome.Skipped.Count > 0) msg += $"\n({outcome.Skipped.Count} texture(s) were skipped because their layout can't be shrunk safely.)";
            if (!t.ViaMo2) msg += "\nStart the game as usual. To undo, press \"Undo budget fix\".";

            string? notice = null;
            if (t.Mo2ModFolder)
                notice = string.Join("\n", new[]
                {
                    $"MOD ORGANIZER 2: the fix was saved as a new MO2 mod called \"{BudgetFix.Mo2FixModName}\".",
                    "",
                    "1. In MO2, press F5 (or the Refresh button) so it shows up in the left list.",
                    "2. Tick its checkbox to enable it. That's it; start the game from MO2 as usual.",
                    "",
                    "To undo: untick it, or press \"Undo budget fix\".",
                });
            else if (t.ViaMo2)
                notice = string.Join("\n", new[]
                {
                    "MOD ORGANIZER 2: your MO2 mods folder wasn't found, so the fix was saved as a ready-made mod folder:",
                    Platform.Shown(Path.GetDirectoryName(t.Dir!)!),
                    "",
                    "Copy that folder into MO2's mods folder, then enable it.",
                });

            lock (_lock)
            {
                _fixMessage = msg;
                _fixMessageWarn = false;
                _mo2Notice = notice;
                if (notice != null) ShowMessage(notice, error: false);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _buildStatus = null;
                _progDone = 0;
                _status = "Writing the budget fix failed: " + ex.Message;
                _fixMessage = "Writing the budget fix failed: " + ex.Message + " Your mods were not changed. Press \"Undo budget fix\" to remove what was written.";
                _fixMessageWarn = true;
                ShowMessage($"Writing the budget fix failed:\n{ex.Message}\n\n"
                    + "Your mods were not changed. Press \"Undo budget fix\" to remove what was written. White-Honey-Fix.log next to the tool has the details.", error: true);
            }
        }
        finally
        {
            var exists = FindFix(ws);
            lock (_lock)
            {
                _buildStatus = null;
                _existingFix = exists;
                _job = Job.None;
            }
            Changed();
        }
    }

    public async Task<Reply> StartUndo()
    {
        Workspace ws;
        ScanResult? r;
        await _gate.WaitAsync();
        try
        {
            lock (_lock)
            {
                if (_job != Job.None) return Reply.Busy();
                if (_ws == null) return Reply.Fail(400, NoGame);
                ws = _ws;
                r = _result;
                _job = Job.Undo;
                _fixMessage = null;
                _mo2Notice = null;
                _progIndeterminate = true;
                _status = "Removing the budget fix...";
                _statusNotes = new();
            }
        }
        finally { _gate.Release(); }
        Changed();
        _ = Task.Run(() => UndoJob(ws, r));
        return Reply.Ok();
    }

    void UndoJob(Workspace ws, ScanResult? r)
    {
        var said = new List<string>();
        try
        {
            var removed = ws.RemoveFix(s => { lock (said) said.Add(s); });
            if (r != null) RefreshFix(ws, r);
            var lines = new List<string>();
            if (removed.Count > 0)
            {
                lines.Add("The budget fix was removed. Everything is back as it was.");
                if (removed.Any(f => f.Contains(BudgetFix.Mo2FixModName, StringComparison.OrdinalIgnoreCase)))
                    lines.Add($"(In Mod Organizer 2 you can now also delete the empty \"{BudgetFix.Mo2FixModName}\" mod.)");
            }
            else
            {
                lines.Add("No budget fix found, so there was nothing to remove.");
                lines.Add($"(Mod Organizer 2: if a \"{BudgetFix.Mo2FixModName}\" mod is still in MO2's list, untick or delete it there.)");
            }
            lock (said) lines.AddRange(said.Where(s => s.StartsWith("Left alone", StringComparison.Ordinal)).Select(Platform.Shown));
            lock (_lock)
            {
                _fixMessage = string.Join("\n", lines);
                _fixMessageWarn = false;
                _reportsDirty = true;
                _status = lines[0];
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _status = "Removing the budget fix failed: " + ex.Message;
                _fixMessage = _status;
                _fixMessageWarn = true;
                ShowMessage($"Removing the budget fix failed:\n{ex.Message}", error: true);
            }
        }
        finally
        {
            var exists = FindFix(ws);
            lock (_lock)
            {
                _progIndeterminate = false;
                _progDone = 0;
                _existingFix = exists;
                _job = Job.None;
            }
            Changed();
        }
    }

    public Reply ShowFixFolder()
    {
        Workspace? ws;
        lock (_lock) ws = _ws;
        try
        {
            var fix = ws?.ExistingFix();
            if (fix == null)
            {
                lock (_lock) _existingFix = null;
                Changed();
                return Reply.Ok();
            }
            Process.Start("explorer.exe", $"/select,\"{fix}\"")?.Dispose();
        }
        catch (Exception ex)
        {
            lock (_lock) _status = "Couldn't open Explorer: " + ex.Message;
            Changed();
        }
        return Reply.Ok();
    }

    // ---------------------------------------------------------------- measuring

    public Reply StartMeasure(string? typed)
    {
        Workspace? ws;
        lock (_lock)
        {
            if (_measureRunning) return Reply.Busy();
            _measureRunning = true;
            _measureStopping = false;
            _stopMeasure = false;
            _measureName = typed ?? "";
            _measureStatus = "Getting ready to measure...";
            ws = _ws;
        }
        Changed();
        var label = string.IsNullOrWhiteSpace(typed) ? "session" : typed.Trim();
        var task = Task.Run(() =>
        {
            bool fixActive = FindFix(ws) != null;
            MeasureJob(label, fixActive);
        });
        lock (_lock) _measureTask = task;
        return Reply.Ok();
    }

    void SetMeasureStatus(string text)
    {
        lock (_lock) _measureStatus = text;
        Changed();
    }

    void MeasureJob(string label, bool fixActive)
    {
        var hooks = new LiveMonitor.Hooks
        {
            Stop = () => _stopMeasure,
            Waiting = name => SetMeasureStatus(
                $"Waiting for {name}.exe ... Start the game and load a save. Play for a minute or two, then close the game (or press Stop measuring)."),
            Started = what => SetMeasureStatus($"Measuring {what} as \"{label}\"{(fixActive ? " (budget fix active)" : "")}..."),
            Sampled = s => SetMeasureStatus(
                $"{s.Elapsed:hh\\:mm\\:ss}  game VRAM {Report.Size(s.Game)}  peak {Report.Size(s.Peak)}  GPU total {Report.Size(s.GpuTotal)}"),
        };
        try
        {
            var o = LiveMonitor.Measure(_opt.ProcessName, label, _opt.Seconds, _outDir, fixActive, hooks);
            lock (_lock)
            {
                if (!o.GameSeen) _measureStatus = "Stopped before the game was started.";
                else if (o.Session is not { } s) _measureStatus = "No samples collected.";
                else
                {
                    _measureStatus = $"Session \"{label}\": {s.Samples} samples, peak {Report.Size(s.Peak)}, 95th pct {Report.Size(s.P95)}, steady (last 60 s) {Report.Size(s.Steady)}";
                    if (label.Equals("before", StringComparison.OrdinalIgnoreCase) && _measureName.Trim().Equals("before", StringComparison.OrdinalIgnoreCase))
                        _measureName = "after";
                }
                _sessions = o.History;
            }
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        {
            lock (_lock) _measureStatus = ex.Message;
        }
        catch (Exception ex)
        {
            lock (_lock) _measureStatus = "Measuring failed: " + ex.Message;
        }
        finally
        {
            lock (_lock)
            {
                _measureRunning = false;
                _measureStopping = false;
            }
            Changed();
        }
    }

    public Reply StopMeasure()
    {
        lock (_lock)
        {
            if (!_measureRunning) return Reply.Ok();
            _stopMeasure = true;
            _measureStopping = true;
            _measureStatus = "Stopping the measurement...";
        }
        Changed();
        return Reply.Ok();
    }

    /// <summary>Lets a running measurement save its session before the tool quits.</summary>
    public void StopMeasureForExit(TimeSpan wait)
    {
        Task? task;
        lock (_lock) task = _measureTask;
        if (task is not { IsCompleted: false }) return;
        StopMeasure();
        try { task.Wait(wait); } catch { }
    }

    // ---------------------------------------------------------------- state

    public byte[] StateJson()
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            lock (_lock) WriteState(w);
        return ms.ToArray();
    }

    static string JobName(Job j) => j switch
    {
        Job.Scan => "scan",
        Job.Apply => "apply",
        Job.Undo => "undo",
        Job.Report => "report",
        _ => "none",
    };

    static void StringOrNull(Utf8JsonWriter w, string name, string? value)
    {
        if (value == null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    static void Strings(Utf8JsonWriter w, string name, IEnumerable<string> values)
    {
        w.WriteStartArray(name);
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    /// <summary>The line next to the Scan button; while a scan or fix runs it follows the engine's counters.</summary>
    string StatusNow()
    {
        if (_job == Job.Scan && _mo2Ask == null)
        {
            int total = _scanTotal, done = Math.Min(_scanDone, Math.Max(total, 0));
            if (total > 0) return $"Scanning mods: {done:N0} / {total:N0} ({done * 100 / total}%)";
            if (_scanLog is { } log) return log.Replace(" Big mod lists take a few minutes; progress is shown below.", " Big mod lists take a few minutes.");
        }
        else if (_job == Job.Apply && _buildStatus is { } st && (st.Total > 0 || st.Phase == "saving"))
        {
            int done = Volatile.Read(ref st.Done), total = Math.Max(1, st.Total);
            if (st.Phase == "saving") return "Saving the fix...";
            string eta = "";
            if (done >= 3 && done < total)
            {
                var left = TimeSpan.FromSeconds(_buildClock.Elapsed.TotalSeconds / done * (total - done));
                eta = left.TotalMinutes >= 1 ? $", about {Math.Ceiling(left.TotalMinutes)} min left" : $", about {Math.Max(1, Math.Ceiling(left.TotalSeconds))} s left";
            }
            return $"Writing fix: {done:N0} / {total:N0} textures ({done * 100 / total}%){eta}";
        }
        return _status;
    }

    (int Done, int Total, bool Indeterminate) ProgressNow()
    {
        if (_job == Job.Scan && _scanTotal > 0) return (Math.Min(_scanDone, _scanTotal), _scanTotal, false);
        if (_job == Job.Apply && _buildStatus is { } st)
        {
            int total = Math.Max(1, st.Total);
            return (Math.Min(Volatile.Read(ref st.Done), total), total, false);
        }
        return (_progDone, _progTotal, _progIndeterminate);
    }

    void WriteState(Utf8JsonWriter w)
    {
        bool idle = _job == Job.None;
        var status = StatusNow();
        w.WriteStartObject();
        w.WriteString("version", Report.Version);
        w.WriteString("title", $"{Title} {Report.Version}");
        StringOrNull(w, "game", _gameShown);
        StringOrNull(w, "gameTip", _gameTip);
        StringOrNull(w, "gameHint", _ws == null && _gameShown == null ? NoGame : null);
        w.WriteString("job", JobName(_job));

        var (pDone, pTotal, pIndeterminate) = ProgressNow();
        w.WriteStartObject("progress");
        w.WriteNumber("done", pDone);
        w.WriteNumber("total", pTotal);
        w.WriteBoolean("indeterminate", pIndeterminate);
        w.WriteString("text", idle ? "" : status);
        w.WriteEndObject();

        w.WriteString("status", status);
        Strings(w, "statusNotes", _statusNotes);

        w.WriteStartArray("summary");
        foreach (var line in SummaryLines())
        {
            w.WriteStartArray();
            foreach (var p in line)
            {
                w.WriteStartObject();
                w.WriteString("text", p.Text);
                w.WriteString("tone", p.Tone);
                w.WriteBoolean("bold", p.Bold);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndArray();
        StringOrNull(w, "summaryTip", _result != null ? $"Room for your mods: {_result.BudgetWhy}" : null);

        w.WriteBoolean("scanned", _result != null);
        w.WritePropertyName("mods");
        w.WriteRawValue(_modsJson ??= ModsJson(), skipInputValidation: true);
        w.WriteString("gridHint", _gridHint);
        w.WriteBoolean("gridHintWarn", _gridHintWarn);

        WriteFix(w, idle);

        if (_mo2Ask is { } ask)
        {
            w.WriteStartObject("mo2Ask");
            w.WriteString("text", Mo2Question);
            Strings(w, "instances", ask.Instances.Select(Platform.Shown));
            w.WriteEndObject();
        }
        else w.WriteNull("mo2Ask");

        WriteMeasure(w);

        if (_messageText != null)
        {
            w.WriteStartObject("message");
            w.WriteNumber("id", _messageId);
            w.WriteString("text", _messageText);
            w.WriteBoolean("error", _messageError);
            w.WriteEndObject();
        }
        else w.WriteNull("message");

        w.WriteBoolean("canScan", idle && _ws != null);
        w.WriteBoolean("canReport", idle && _result != null);
        w.WriteBoolean("canBrowse", idle);
        w.WriteEndObject();
    }

    byte[] ModsJson()
    {
        var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var row in _rows)
            {
                var m = row.Mod;
                w.WriteStartObject();
                w.WriteString("name", m.Name);
                StringOrNull(w, "package", m.Package);
                StringOrNull(w, "manager", m.Manager);
                if (m.NexusId is { } id) w.WriteNumber("nexusId", id); else w.WriteNull("nexusId");
                w.WriteNumber("always", row.Always);
                w.WriteNumber("whenUsed", row.WhenUsed);
                w.WriteNumber("textures", row.Textures);
                w.WriteNumber("meshes", row.Meshes);
                w.WriteNumber("flags", row.Flags);
                w.WriteNumber("gpuDelta", row.GpuDelta);
                StringOrNull(w, "error", m.Error);
                w.WriteBoolean("trim", !m.ExcludedFromFix);
                w.WriteString("alwaysText", Report.Size(row.Always));
                w.WriteString("whenUsedText", Report.Size(row.WhenUsed));
                w.WriteString("tip", row.Tip);
                StringOrNull(w, "managerText", row.ManagerText);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return ms.ToArray();
    }

    List<List<Piece>> SummaryLines()
    {
        var r = _result;
        if (r == null) return new() { new() { new(ScanFirst) } };
        var normal = _choices?.Normal ?? BudgetPlan.Build(r, _budget);
        var lines = new List<List<Piece>>
        {
            new()
            {
                new(r.Gpu != null ? $"GPU: {r.Gpu.Name} ({Report.Size(r.Gpu.VramBytes)})" : "GPU: not detected"),
                new("     "),
                new(r.Settings != null ? $"Settings: {r.Settings.Describe()}" : "Settings: not found"),
            },
            new()
            {
                new("Extra VRAM your mods use during normal play: "), new(Report.Size(_alwaysTotal), Bold: true),
                new("     New items, only while you use them: "), new(Report.Size(_whenUsedTotal), Bold: true),
            },
        };
        var verdict = new List<Piece> { new($"Room for your mods: {Report.Size(normal.Budget)} on top of the game itself.  ") };
        if (normal.Current <= normal.Budget) verdict.Add(new("OK: your mods fit. Nothing needs fixing.", "good", true));
        else
        {
            verdict.Add(new($"Over by {Report.Size(normal.Current - normal.Budget)}.", "bad", true));
            if (r.BudgetFix != null && r.FixCheck is not { Stale: true })
                verdict.Add(new($" The budget fix is active and brings this to about {Report.Size(normal.AfterPlan)}."));
            else if (normal.Steps.Count > 0)
                verdict.Add(new(normal.Reachable
                    ? $" Halving {normal.Steps.Count:N0} texture(s) fixes it."
                    : $" Halving textures gets you to {Report.Size(normal.AfterPlan)}; to go lower, remove or swap the heaviest mod at the top of the list."));
        }
        lines.Add(verdict);
        lines.Add(new() { new($"({r.BudgetWhy})", "dim") });
        if (normal.Current > normal.Budget && Report.SettingsTips(r) is { Length: > 0 } tips)
            lines.Add(new() { new($"Lower settings also give mods more room (estimates): {tips}.", "dim") });
        return lines;
    }

    void WriteFix(Utf8JsonWriter w, bool idle)
    {
        // Status of the fix that is applied now.
        string status;
        bool warn = false;
        if (_fixMessage != null) { status = _fixMessage; warn = _fixMessageWarn; }
        else if (_result?.FixCheck is { Stale: true } stale)
        {
            warn = true;
            status = $"The budget fix is out of date: {stale.Describe()}. "
                + (stale.Changed.Count + stale.Removed.Count > 0 ? "It shows old copies of those textures." : "It still trims the mods you excluded.")
                + " Press \"Apply budget fix\" again to rebuild it, or \"Undo budget fix\" to remove it.";
        }
        else if (_result?.BudgetFix is { } fix)
            status = $"The budget fix is active: {Path.GetFileName(fix)}" + (_result.FixCheck is { } fc ? $" ({fc.Textures:N0} texture(s))." : ".");
        else if (_result == null && _existingFix is { } existing)
            status = $"The budget fix is active: {Path.GetFileName(existing)}.";
        else status = "No budget fix is applied.";

        // What a fix would do with the current choices.
        var plan = new List<string>();
        string to2k = "Shrink 4K textures to 2K", to1k = "Shrink to 1K (softer up close)";
        if (_result == null || _choices == null || _plan == null)
            plan.Add("Scan your mods first to see what the fix would do.");
        else
        {
            var normal = _choices.Normal;
            if (normal.Current <= normal.Budget)
                plan.Add($"Normal play: your mods add about {Report.Size(normal.Current)} on top of the game; your card has room for {Report.Size(normal.Budget)}. Nothing to fix there.");
            else if (normal.Steps.Count == 0)
                plan.Add($"Normal play: your mods add {Report.Size(normal.Current - normal.Budget)} more than your card has room for, but no texture is big enough to halve. Swap the heaviest mod for a lighter version instead.");
            else
            {
                plan.Add($"Normal play: your mods add about {Report.Size(normal.Current)} on top of the game, but your card only has room for {Report.Size(normal.Budget)} ({Report.Size(normal.Current - normal.Budget)} too much).");
                plan.Add($"The fix would halve {normal.Steps.Count:N0} texture(s), saving about {Report.Size(normal.Steps.Sum(s => s.Saves))}."
                    + (normal.Reachable ? "" : $" That gets you to {Report.Size(normal.AfterPlan)}; to go lower, remove or swap the heaviest mod at the top of the list."));
            }
            if (normal.ExcludedWithTextures.Count > 0 && !normal.Reachable)
                plan.Add("The mods you excluded are part of the reason the budget can't be reached: their big textures are left as they are.");
            plan.Add(_plan.Steps.Count == 0
                ? "With these choices the fix has nothing to change."
                : $"With these choices: {_plan.Steps.Count:N0} texture(s), saving up to {Report.Size(_plan.Steps.Sum(s => s.Saves))} of VRAM, about {Report.Size(BudgetFix.EstimateBytes(_plan.Steps))} on disk.");
            to2k = $"Shrink 4K textures to 2K ({_choices.To2K.Count:N0} texture(s), saves up to {Report.Size(_choices.To2K.Sum(s => s.Saves))})";
            to1k = $"Shrink to 1K ({_choices.To1K.Count:N0} texture(s), saves up to {Report.Size(_choices.To1K.Sum(s => s.Saves))}; softer up close)";
        }

        w.WriteStartObject("fix");
        w.WriteBoolean("exists", _existingFix != null);
        w.WriteString("status", status);
        w.WriteBoolean("warn", warn);
        Strings(w, "plan", plan);
        w.WriteBoolean("canApply", idle && _result != null && _plan is { Steps.Count: > 0 });
        w.WriteBoolean("canUndo", idle && _ws != null && _existingFix != null);
        StringOrNull(w, "confirm", _plan is { Steps.Count: > 0 } p
            ? $"Write the fix: {p.Steps.Count:N0} texture(s), about {Report.Size(BudgetFix.EstimateBytes(p.Steps))} on disk. It is ONE new mod folder; your mods are not changed. Continue?"
            : null);
        w.WriteStartObject("newItems");
        w.WriteString("caption", NewItemsCaption);
        w.WriteString("choice", _newItems switch { NewItems.To2K => "2k", NewItems.To1K => "1k", _ => "leave" });
        w.WriteStartObject("leave");
        w.WriteString("text", "Leave them as they are");
        w.WriteBoolean("enabled", idle && _result != null);
        w.WriteEndObject();
        w.WriteStartObject("to2k");
        w.WriteString("text", to2k);
        w.WriteBoolean("enabled", idle && _choices is { To2K.Count: > 0 });
        w.WriteEndObject();
        w.WriteStartObject("to1k");
        w.WriteString("text", to1k);
        w.WriteBoolean("enabled", idle && _choices is { To1K.Count: > 0 });
        w.WriteEndObject();
        w.WriteEndObject();
        StringOrNull(w, "mo2Notice", _mo2Notice);
        w.WriteEndObject();
    }

    void WriteMeasure(Utf8JsonWriter w)
    {
        w.WriteStartObject("measure");
        w.WriteString("help", MeasureHelp);
        w.WriteBoolean("running", _measureRunning);
        w.WriteString("buttonText", _measureRunning ? "Stop measuring" : "Measure VRAM in game");
        w.WriteBoolean("buttonEnabled", !_measureStopping);
        w.WriteString("status", _measureStatus);
        w.WriteString("name", _measureName);
        w.WriteStartArray("sessions");
        foreach (var s in _sessions.TakeLast(6))
        {
            w.WriteStartObject();
            w.WriteString("label", s.Label);
            w.WriteString("when", s.Start.ToString("yyyy-MM-dd HH:mm"));
            w.WriteString("peak", Report.Size(s.Peak));
            w.WriteString("p95", Report.Size(s.P95));
            w.WriteString("steady", Report.Size(s.Steady));
            w.WriteString("budgetFix", s.BudgetArchive ? "yes" : "no");
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteString("change", LiveMonitor.ChangeLine(_sessions) ?? "Run again after changing mods (for example named \"after\") to compare.");
        w.WriteEndObject();
    }
}
