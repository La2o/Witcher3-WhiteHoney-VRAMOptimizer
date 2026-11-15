namespace WhiteHoney;

/// <summary>
/// The graphics settings that move VRAM the most, read from the game's dx12user.settings / user.settings
/// (read-only), on top of the defaults in the game's own bin\config files. Used to work out how large the game
/// really shows each texture and how much VRAM the game needs before mods.
/// </summary>
public sealed class GameSettings
{
    public string File = "";
    public int Width, Height;
    /// <summary>Low, Medium, High, Ultra or Ultra+ (worked out from the values the in-game preset sets), or Custom.</summary>
    public string TextureQuality = "Ultra";
    public bool RayTracing, PathTracing, FrameGen, HairWorks;
    /// <summary>The game never shows a texture larger than this (1024 on Low, 2048 up to High, 4096 on Ultra and Ultra+).</summary>
    public int MaxTextureSize = 4096;
    /// <summary>Mip levels the game drops from every texture (2 on Low, 1 on Medium).</summary>
    public int TextureDownscale;
    /// <summary>The game's pool for streamed textures, in MB (400 on Low ... 1200 on Ultra).</summary>
    public int TextureBudgetMb = 1200;

    static string DocsDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "The Witcher 3");

    /// <summary>
    /// The settings file the installed game uses: dx12user.settings for the DirectX 12 exe, user.settings for
    /// DirectX 11. When both exes (and both files) exist, the one saved last is the one the player runs.
    /// Without a settings file (game never started), the game's defaults are returned with no resolution.
    /// </summary>
    public static GameSettings? Load(string gameDir)
    {
        bool dx12 = System.IO.File.Exists(Path.Combine(gameDir, "bin", "x64_dx12", "witcher3.exe"));
        bool dx11 = System.IO.File.Exists(Path.Combine(gameDir, "bin", "x64", "witcher3.exe"));
        var candidates = new List<string>();
        if (dx12) candidates.Add(Path.Combine(DocsDir, "dx12user.settings"));
        if (dx11 || !dx12) candidates.Add(Path.Combine(DocsDir, "user.settings"));
        var file = candidates.Where(System.IO.File.Exists).OrderByDescending(System.IO.File.GetLastWriteTimeUtc).FirstOrDefault();
        return LoadFile(file, gameDir);
    }

    static void ReadIni(string path, Dictionary<string, string> values)
    {
        if (!System.IO.File.Exists(path)) return;
        string section = "";
        foreach (var raw in System.IO.File.ReadLines(path))
        {
            var l = raw.Trim();
            if (l.StartsWith('[') && l.EndsWith(']')) { section = l[1..^1]; continue; }
            int eq = l.IndexOf('=');
            if (eq <= 0 || l.StartsWith(';')) continue;
            // Only graphics sections are kept; the user's file also holds account data that is none of our business.
            if (!section.StartsWith("Rendering", StringComparison.OrdinalIgnoreCase) && !section.Equals("Viewport", StringComparison.OrdinalIgnoreCase)
                && !section.Equals("PostProcess", StringComparison.OrdinalIgnoreCase)) continue;
            values[$"{section}/{l[..eq].Trim()}"] = l[(eq + 1)..].Trim().Trim('"').TrimEnd('f');
        }
    }

    public static GameSettings? LoadFile(string? path, string? gameDir = null)
    {
        if (path != null && !System.IO.File.Exists(path)) path = null;
        if (path == null && gameDir == null) return null;
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (gameDir != null)
            {
                ReadIni(Path.Combine(gameDir, "bin", "config", "base", "rendering.ini"), values);
                ReadIni(Path.Combine(gameDir, "bin", "config", "platform", "pc", "rendering.ini"), values);
            }
            if (path != null) ReadIni(path, values);
            string Str(string k) => values.GetValueOrDefault(k, "");
            bool On(string k) => Str(k).Equals("true", StringComparison.OrdinalIgnoreCase) || Str(k) == "1";
            double Num(string k, double def) => double.TryParse(Str(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : def;

            var s = new GameSettings { File = path ?? "" };
            var res = Str("Viewport/Resolution").Split('x');
            if (res.Length == 2 && int.TryParse(res[0], out var w) && int.TryParse(res[1], out var h)) { s.Width = w; s.Height = h; }
            s.RayTracing = On("Rendering/RT/EnableRT");
            s.PathTracing = On("Rendering/RT/PathTracer/PTEnable");
            s.FrameGen = Num("PostProcess/DLSSGMode", 0) > 0 || Num("PostProcess/FSRFramegen", 0) > 0 || On("PostProcess/XessFrameGeneration");
            s.HairWorks = Num("Rendering/HairWorksLevel", 0) > 0;
            s.MaxTextureSize = (int)Num("Rendering/MaxTextureSize", 4096);
            s.TextureDownscale = Math.Clamp((int)Num("Rendering/TextureDownscale", 0), 0, 4);
            s.TextureBudgetMb = (int)Num("Rendering/TextureMemoryBudget", 1200);
            double bias = Num("Rendering/TextureMipBias", 0);
            s.TextureQuality = (s.MaxTextureSize, s.TextureDownscale) switch
            {
                ( <= 1024, >= 2) => "Low",
                ( <= 2048, 1) => "Medium",
                ( <= 2048, 0) => "High",
                ( >= 4096, 0) => bias <= -1.5 ? "Ultra+" : "Ultra",
                _ => "Custom",
            };
            return s;
        }
        catch { return null; }
    }

    /// <summary>
    /// How many of a texture's largest mips the game leaves out at these settings: the Texture Quality preset drops
    /// TextureDownscale levels and never shows more than MaxTextureSize pixels on a side.
    /// </summary>
    public int SkippedMips(int maxDim)
    {
        int skip = TextureDownscale;
        while (MaxTextureSize > 0 && (maxDim >> skip) > MaxTextureSize) skip++;
        return skip;
    }

    /// <summary>Rule-of-thumb VRAM the game itself needs at these settings; verify with "3 - Measure VRAM in game".</summary>
    public long EstimatedVanillaBytes()
    {
        double mp = Width * (double)Height / 1e6;
        double gb = mp <= 2.1 ? 4.5 : mp <= 3.7 ? 5.2 : mp <= 5.0 ? 5.8 : mp <= 7.5 ? 6.5 : 7.2; // 1080p / 1440p / UW / 32:9 / 4K
        gb += TextureQuality switch { "Low" => -1.2, "Medium" => -0.9, "High" => -0.5, _ => 0 };
        gb += PathTracing ? 3.0 : RayTracing ? 2.0 : 0;
        if (FrameGen) gb += 0.5;
        if (HairWorks) gb += 0.3;
        return (long)(Math.Max(2.0, gb) * (1L << 30));
    }

    /// <summary>Setting changes that give mods more room, with the VRAM each frees by the same rule of thumb.</summary>
    public List<(string Change, long Frees)> RoomTips()
    {
        static long Gb(double v) => (long)(v * (1L << 30));
        var tips = new List<(string, long)>();
        switch (TextureQuality)
        {
            case "Ultra+":
            case "Ultra": tips.Add(("Texture Quality High (also caps every texture, mods included, at 2K)", Gb(0.5))); break;
            case "High": tips.Add(("Texture Quality Medium", Gb(0.4))); break;
        }
        if (PathTracing) tips.Add(("path tracing off (ray tracing can stay on)", Gb(1.0)));
        else if (RayTracing) tips.Add(("ray tracing off", Gb(2.0)));
        if (FrameGen) tips.Add(("frame generation off", Gb(0.5)));
        if (HairWorks) tips.Add(("HairWorks off", Gb(0.3)));
        return tips;
    }

    public string Describe() =>
        $"{(Width > 0 ? $"{Width}x{Height}" : "unknown resolution")}, {TextureQuality} textures"
        + (PathTracing ? ", path tracing" : RayTracing ? ", ray tracing" : "") + (FrameGen ? ", frame generation" : "") + (HairWorks ? ", HairWorks" : "");
}
