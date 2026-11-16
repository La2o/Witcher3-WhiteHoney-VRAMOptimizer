using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;

// The window needs Windows (or Wine): Edge, the registry, the folder picker and message boxes.
[assembly: SupportedOSPlatform("windows")]

namespace WhiteHoney.UI;

/// <summary>Developer and test arguments, named and parsed like the console tool's (--name value).</summary>
sealed class Options
{
    static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "no-vanilla", "scan", "no-window", "no-open" };
    readonly Dictionary<string, string?> _opts = new(StringComparer.OrdinalIgnoreCase);

    public Options(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) { _opts["game"] = a; continue; }
            var key = a[2..];
            if (Flags.Contains(key)) _opts[key] = "1";
            else _opts[key] = i + 1 < args.Length ? args[++i] : null;
        }
    }

    public string? Get(string key) => _opts.GetValueOrDefault(key);
    public bool Has(string key) => _opts.ContainsKey(key);
    public string? Path(string key) => Platform.FromUser(Get(key));

    public double? Number(string key) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public string? Game => Path("game");
    public string? ModRoot => Get("mod-root");
    public string? ModsSettings => Path("mods-settings");
    public string? Mo2 => Path("mo2");
    public string OutDir => Path("out") ?? AppContext.BaseDirectory;
    public string ExcludeFile => Path("exclude-file") ?? Exclusions.DefaultPath;
    public string? SettingsFile => Path("settings");
    public bool CompareVanilla => !Has("no-vanilla");
    public double? VramGb => Number("vram") is > 0 and var v ? v : null;
    public string? GpuName => Get("gpu");
    public double? BudgetGb => Number("budget");
    public string ProcessName => Get("process") ?? "witcher3";
    public int Seconds => int.TryParse(Get("seconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
    public bool AutoScan => Has("scan");
    /// <summary>The local port for the page; 0 picks any free one.</summary>
    public int Port => int.TryParse(Get("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 65535 ? p : 0;
    public string? Token => Get("token") is { Length: > 0 } t ? t : null;
    /// <summary>Testing: open no window, never quit by itself, and accept POST /api/quit.</summary>
    public bool NoWindow => Has("no-window");
    /// <summary>Testing: open no window, but quit as usual when the page goes away.</summary>
    public bool NoOpen => Has("no-open");
}

static class Program
{
    public static string CrashLog => System.IO.Path.Combine(AppContext.BaseDirectory, "White-Honey-Crash.log");

    /// <summary>The tool quits this long after the window was closed (a reload reconnects well within it).</summary>
    static readonly TimeSpan GoneFor = TimeSpan.FromSeconds(4);
    /// <summary>
    /// The page went quiet without saying its window closed: the browser put a background window to sleep (the
    /// player is in the game), or crashed. The tool stays around this long so the page finds it again.
    /// </summary>
    static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(30);
    /// <summary>A measurement keeps running without a page for at most this long.</summary>
    static readonly TimeSpan MeasureAloneFor = TimeSpan.FromHours(6);
    /// <summary>The tool quits when no page ever connects within this time (the window could not be opened).</summary>
    static readonly TimeSpan NeverCameFor = TimeSpan.FromSeconds(90);

    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crashed(e.ExceptionObject, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
        try { return Run(new Options(args)); }
        catch (Exception ex)
        {
            Crashed(ex, terminating: true);
            return 1;
        }
    }

    static int Run(Options opt)
    {
        var app = new App(opt);
        app.StartUp();

        var token = opt.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var host = new HttpHost(app, token, devQuit: opt.NoWindow);
        try { host.Start(opt.Port); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Couldn't open the local connection the window talks to (port {opt.Port}): {ex.Message}", ex);
        }

        if (opt.AutoScan) _ = app.StartScan();
        if (!opt.NoWindow && !opt.NoOpen) Browser.Open(host.PageUrl);

        var started = Stopwatch.StartNew();
        while (true)
        {
            if (host.QuitRequested.Wait(500)) break;
            if (opt.NoWindow) continue;
            if (host.Streams > 0) continue;
            if (!host.EverConnected)
            {
                if (started.Elapsed > NeverCameFor) break;
                continue;
            }
            // The page is gone. A fix being written or removed is finished first.
            if (app.IsWritingFiles) continue;
            if (host.SaidBye)
            {
                if (host.SinceLastStream < GoneFor) continue;
                break; // the window was closed
            }
            // Quiet, but not closed: never cut a measurement short because the browser dozed off while the player
            // is in the game, and stay available for a while.
            if (app.IsMeasuring && host.SinceLastStream < MeasureAloneFor) continue;
            if (host.SinceLastStream < QuietFor) continue;
            break;
        }

        // Never leave half a fix behind, and let a measurement save its session.
        while (app.IsWritingFiles) Thread.Sleep(250);
        app.StopMeasureForExit(TimeSpan.FromSeconds(20));
        host.Stop();
        return 0;
    }

    public static void Log(object error)
    {
        try { File.AppendAllText(CrashLog, $"{DateTime.Now}: {error}{Environment.NewLine}"); } catch { }
    }

    static void Crashed(object error, bool terminating)
    {
        Log($"White Honey crashed: {error}");
        Native.ShowError(
            "Something went wrong inside White Honey" + (terminating ? " and it has to close." : ".") + Environment.NewLine + Environment.NewLine
            + (error is Exception ex ? ex.Message : error.ToString()) + Environment.NewLine + Environment.NewLine
            + "The details were saved in White-Honey-Crash.log next to the tool.",
            "White Honey - VRAM Optimizer");
    }
}
