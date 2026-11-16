using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WhiteHoney;

/// <summary>Finds The Witcher 3: next to the tool, a folder given before, or the Steam, GOG and Epic installs.</summary>
public static class GameFinder
{
    /// <summary>The game folder has content\ plus the exe in bin\x64 (DirectX 11) or bin\x64_dx12.</summary>
    public static bool IsGame(string? d) => d != null && Directory.Exists(Path.Combine(d, "content"))
        && (File.Exists(Path.Combine(d, "bin", "x64_dx12", "witcher3.exe")) || File.Exists(Path.Combine(d, "bin", "x64", "witcher3.exe")));

    /// <param name="hint">A folder the player named for this run; when given, nothing else is tried.</param>
    /// <param name="saved">The folder the player gave earlier (from the tool's settings file).</param>
    public static string? Find(string? hint, string? saved = null)
    {
        if (hint != null) return IsGame(hint) ? Path.GetFullPath(hint) : null;

        // Installed as a mod: <game>/tools/White Honey/WhiteHoney.exe
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (IsGame(d.FullName)) return d.FullName;
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d != null; d = d.Parent)
            if (IsGame(d.FullName)) return d.FullName;
        // Unzipped somewhere else: the game folder the player gave us before.
        if (IsGame(saved)) return Path.GetFullPath(saved!);

        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var steamNames = new[] { "The Witcher 3", "The Witcher 3 Wild Hunt", "The Witcher 3 Wild Hunt GOTY" };
            try
            {
                var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                foreach (var root in new[] { steam, @"C:\Program Files (x86)\Steam" }.Where(r => r != null))
                {
                    var libraries = new List<string> { root! };
                    var vdf = Path.Combine(root!, "steamapps", "libraryfolders.vdf");
                    if (File.Exists(vdf))
                        foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                            libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                    foreach (var lib in libraries)
                        foreach (var name in steamNames) candidates.Add(Path.Combine(lib, "steamapps", "common", name));
                }
                // GOG: base game, Game of the Year Edition, Complete Edition.
                foreach (var id in new[] { "1207664663", "1495134320", "1640424747" })
                    foreach (var key in new[] { $@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\{id}", $@"HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com\Games\{id}" })
                        if (Registry.GetValue(key, "path", null) is string gog) candidates.Add(gog);
            }
            catch { }
            // Epic: the launcher's install manifests say where each game is, wherever it was installed.
            candidates.AddRange(EpicInstallLocations(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests")));
            foreach (var name in steamNames)
            {
                candidates.Add(Path.Combine(@"C:\Program Files\Epic Games", name));
                candidates.Add(Path.Combine(@"C:\GOG Games", name));
            }
        }
        var found = candidates.FirstOrDefault(IsGame);
        return found == null ? null : Path.GetFullPath(found);
    }

    static List<string> EpicInstallLocations(string manifestDir)
    {
        var found = new List<string>();
        try
        {
            foreach (var item in Directory.GetFiles(manifestDir, "*.item"))
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(item));
                    if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } dir) found.Add(dir);
                }
                catch { }
        }
        catch { }
        return found;
    }

    /// <summary>
    /// The game folder for a path the player picked or pasted: the folder itself, the game's exe, or any folder
    /// inside the game. Null when it isn't The Witcher 3.
    /// </summary>
    public static string? FromPath(string? picked)
    {
        if (string.IsNullOrEmpty(picked)) return null;
        try
        {
            for (var d = new DirectoryInfo(File.Exists(picked) ? Path.GetDirectoryName(picked)! : picked); d != null; d = d.Parent)
                if (IsGame(d.FullName)) return d.FullName;
        }
        catch { }
        return null;
    }
}
