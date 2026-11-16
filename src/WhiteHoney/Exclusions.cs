using System.Text.RegularExpressions;

namespace WhiteHoney;

/// <summary>Mods the player never wants trimmed: a text file next to the tool plus --exclude entries for one run.</summary>
public static class Exclusions
{
    public const string FileName = "White Honey - Excluded mods.txt";

    static readonly string[] Header =
    {
        "# Mods listed here are never trimmed by '2 - Apply budget fix'.",
        "# One mod per line: the folder name from your mods folder, e.g. modHDReworkedProject",
        "# (the name your mod manager shows for it works too).",
        "# A * matches anything, e.g. mod*Geralt*",
        "# Lines starting with # are ignored.",
        "",
    };

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>Entries of the file (comments and blank lines skipped); empty when it can't be read.</summary>
    public static List<string> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim().TrimStart('﻿').Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList()
                : new();
        }
        catch { return new(); }
    }

    /// <summary>Entries from --exclude "a;b;c".</summary>
    public static List<string> Parse(string? list) =>
        (list ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(e => e.Length > 0).ToList();

    /// <summary>An entry equals the mod's folder name or its Vortex/MO2 name (ignoring case); * matches any run of characters.</summary>
    public static bool Matches(string entry, ModReport m) => Match(entry, m.Name) || (m.Package != null && Match(entry, m.Package));

    static bool Match(string entry, string name) =>
        Regex.IsMatch(name, "^" + Regex.Escape(entry).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Marks the matching mods and returns the entries that matched no installed mod.</summary>
    public static List<string> Apply(IEnumerable<ModReport> mods, IEnumerable<string> entries)
    {
        var list = mods.ToList();
        var unmatched = new List<string>();
        foreach (var e in entries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var hit = list.Where(m => Matches(e, m)).ToList();
            foreach (var m in hit) m.ExcludedFromFix = true;
            if (hit.Count == 0) unmatched.Add(e);
        }
        return unmatched;
    }

    /// <summary>Creates the file with a short explanation when it is missing. Never throws.</summary>
    public static void EnsureFile(string path)
    {
        try
        {
            if (File.Exists(path)) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllLines(path, Header);
        }
        catch { }
    }

    /// <summary>Adds entries to the file unless they are already in it. Returns false when the file can't be written.</summary>
    public static bool Append(string path, IEnumerable<string> names)
    {
        try
        {
            var have = Load(path);
            var add = names.Where(n => !have.Contains(n, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (add.Count == 0) return true;
            EnsureFile(path);
            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var nl = Environment.NewLine;
            File.AppendAllText(path, (text.Length > 0 && !text.EndsWith('\n') ? nl : "") + string.Join(nl, add) + nl);
            return true;
        }
        catch { return false; }
    }
}
