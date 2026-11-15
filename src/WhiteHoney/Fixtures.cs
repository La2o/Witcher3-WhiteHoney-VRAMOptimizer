using System.Text;

namespace WhiteHoney;

/// <summary>Writes bundles for the developer fixtures (the game never reads these; the scanner does).</summary>
public static class BundleWriter
{
    public static void Write(string path, IReadOnlyList<(string Name, byte[] Data)> files, int version = 5, uint scheme = Codec.Zlib)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        int entrySize = version >= 5 ? 304 : 320, align = version >= 5 ? 16 : 4096;
        int table = files.Count * entrySize;
        using var fs = File.Create(path);
        var head = new byte[0x20];
        Encoding.ASCII.GetBytes("POTATO70").CopyTo(head, 0);
        var blobs = files.Select(f => scheme switch { Codec.Zlib => Codec.Deflate(f.Data), Codec.Lz4 => Lz4Literal(f.Data), _ => f.Data }).ToList();
        var offsets = new List<long>();
        long pos = 0x20 + table;
        foreach (var b in blobs) { pos = (pos + align - 1) / align * align; offsets.Add(pos); pos += b.Length; }
        if (version >= 5)
        {
            BitConverter.TryWriteBytes(head.AsSpan(8), pos);
            BitConverter.TryWriteBytes(head.AsSpan(0x10), (uint)table);
            BitConverter.TryWriteBytes(head.AsSpan(0x14), (ushort)5);
            BitConverter.TryWriteBytes(head.AsSpan(0x16), (uint)(table + 0x20));
        }
        else
        {
            BitConverter.TryWriteBytes(head.AsSpan(8), (uint)pos);
            BitConverter.TryWriteBytes(head.AsSpan(0x10), (uint)table);
            BitConverter.TryWriteBytes(head.AsSpan(0x14), 0x00010003u);
            for (int i = 0x19; i < 0x20; i++) head[i] = 0x13;
        }
        fs.Write(head);
        for (int i = 0; i < files.Count; i++)
        {
            var e = new byte[entrySize];
            Encoding.Latin1.GetBytes(files[i].Name).CopyTo(e, 0);
            uint crc = (uint)TextureCache.Fnv1a64(files[i].Data);
            if (version >= 5)
            {
                BitConverter.TryWriteBytes(e.AsSpan(272), offsets[i]);
                BitConverter.TryWriteBytes(e.AsSpan(280), (uint)files[i].Data.Length);
                BitConverter.TryWriteBytes(e.AsSpan(284), (uint)blobs[i].Length);
                BitConverter.TryWriteBytes(e.AsSpan(288), crc);
                BitConverter.TryWriteBytes(e.AsSpan(292), scheme);
            }
            else
            {
                BitConverter.TryWriteBytes(e.AsSpan(276), (uint)files[i].Data.Length);
                BitConverter.TryWriteBytes(e.AsSpan(280), (uint)blobs[i].Length);
                BitConverter.TryWriteBytes(e.AsSpan(284), (uint)offsets[i]);
                BitConverter.TryWriteBytes(e.AsSpan(312), crc);
                BitConverter.TryWriteBytes(e.AsSpan(316), scheme);
            }
            fs.Write(e);
        }
        for (int i = 0; i < files.Count; i++)
        {
            fs.Write(new byte[offsets[i] - fs.Position]);
            fs.Write(blobs[i]);
        }
    }

    /// <summary>A valid LZ4 block made of literals only (enough to exercise the reader).</summary>
    static byte[] Lz4Literal(byte[] data)
    {
        using var ms = new MemoryStream();
        int n = data.Length;
        ms.WriteByte((byte)(Math.Min(n, 15) << 4));
        if (n >= 15) { int rest = n - 15; while (rest >= 255) { ms.WriteByte(255); rest -= 255; } ms.WriteByte((byte)rest); }
        ms.Write(data);
        return ms.ToArray();
    }
}

/// <summary>
/// Developer fixtures: fake mods built from real game files, one per thing the scanner must recognise.
/// Scan them with: --mod-root &lt;dir&gt; --mo2 &lt;dir&gt;\_mo2
/// </summary>
public static class Fixtures
{
    public static uint Fnv1a32(string s)
    {
        uint h = 0x811c9dc5;
        foreach (var ch in s) { h ^= (byte)ch; h *= 0x01000193; }
        return h;
    }

    sealed class Game : IDisposable
    {
        public readonly List<TextureCache> Caches = new();
        public readonly List<Bundle> Bundles = new();
        public Game(string game)
        {
            foreach (var dir in Scanner.BaseContentDirs(game))
            {
                foreach (var f in Scanner.ContentFiles(dir, "texture.cache")) Caches.Add(new TextureCache(f));
                foreach (var f in Scanner.ContentFiles(dir, "*.bundle")) Bundles.Add(new Bundle(f));
            }
        }
        public IEnumerable<(TextureCache C, TexEntry E)> Textures => Caches.SelectMany(c => c.Entries.Select(e => (c, e)));
        public IEnumerable<(Bundle B, BundleEntry E)> Files => Bundles.SelectMany(b => b.Entries.Select(e => (b, e)));
        public void Dispose() { foreach (var c in Caches) c.Dispose(); foreach (var b in Bundles) b.Dispose(); }
    }

    static TexWrite Copy(TextureCache c, TexEntry e, uint? hash = null, string? name = null)
    {
        var n = new TexEntry
        {
            Hash = hash ?? e.Hash, Name = name ?? e.Name, ZSize = e.ZSize, Size = e.Size, BaseAlignment = e.BaseAlignment, Width = e.Width, Height = e.Height,
            Mips = e.Mips, Slices = e.Slices, MipOffsetCount = e.MipOffsetCount, Extra = e.Extra, Timestamp = e.Timestamp, Format = e.Format, CubeFlag = e.CubeFlag,
        };
        return new TexWrite(n, c.ReadRaw(e), c.MipOffsets.AsSpan(e.MipOffsetIndex, e.MipOffsetCount).ToArray());
    }

    static uint _version = 7;

    static void WriteCache(string modDir, IEnumerable<TexWrite> textures)
    {
        var content = Path.Combine(modDir, "content");
        using var w = new TextureCacheWriter(content, _version);
        foreach (var t in textures) w.Add(t);
        w.Write(Path.Combine(content, "texture.cache"));
    }

    /// <summary>Replaces one import path inside a CR2W with another of the same length (offsets stay valid).</summary>
    static byte[] PatchImport(byte[] cr2w, string from, string to)
    {
        if (from.Length != to.Length) throw new ArgumentException("paths must have the same length");
        var data = (byte[])cr2w.Clone();
        var a = Encoding.Latin1.GetBytes(from);
        int at = data.AsSpan().IndexOf(a);
        if (at < 0) throw new InvalidOperationException("import not found");
        Encoding.Latin1.GetBytes(to).CopyTo(data, at);
        return data;
    }

    static string SameLength(string path, string prefix)
    {
        // "fixture\....xbm" with the original's length and extension.
        var ext = Path.GetExtension(path);
        int fill = path.Length - prefix.Length - ext.Length;
        return fill > 0 ? prefix + new string('x', fill) + ext : path;
    }

    public static int Make(string game, string outDir)
    {
        if (Directory.Exists(outDir) && Directory.EnumerateFileSystemEntries(outDir).Any())
        { Console.Error.WriteLine($"{outDir} is not empty; pick a new folder."); return 1; }
        var mods = Path.Combine(outDir, "mods");
        Directory.CreateDirectory(mods);
        using var g = new Game(game);
        _version = g.Caches.Select(c => c.Version).DefaultIfEmpty(7u).Max();

        bool Plain(TexEntry e) => e.Faces == 1 && e.StreamMips >= 2;
        var small = g.Textures.Where(t => Plain(t.E) && Math.Max(t.E.Width, t.E.Height) == 1024 && t.E.Name.StartsWith("characters")).Take(40).ToList();
        var big = g.Textures.Where(t => Plain(t.E) && Math.Max(t.E.Width, t.E.Height) >= 4096).Take(40).ToList();
        if (big.Count < 12) big = g.Textures.Where(t => Plain(t.E) && Math.Max(t.E.Width, t.E.Height) >= 2048).Take(40).ToList();
        if (small.Count < 30 || big.Count < 12) { Console.Error.WriteLine("Not enough suitable textures in the game's cache."); return 1; }
        Console.WriteLine($"Using {small.Count} 1K and {big.Count} large vanilla textures ({big[0].E.Width}px).");

        // 1. Heavy retexture: 1K game textures replaced by 4K data.
        WriteCache(Path.Combine(mods, "modHDFaces"), Enumerable.Range(0, 10).Select(i => Copy(big[i].C, big[i].E, small[i].E.Hash, small[i].E.Name)));
        // 2. Conflict: both replace the same textures; the first in load order wins.
        WriteCache(Path.Combine(mods, "modAAA_Winner"), Enumerable.Range(10, 3).Select(i => Copy(big[i % big.Count].C, big[i % big.Count].E, small[i].E.Hash, small[i].E.Name)));
        WriteCache(Path.Combine(mods, "modZZZ_Loser"), Enumerable.Range(10, 3).Select(i => Copy(small[i].C, small[i].E)));
        // 3. Identical to vanilla.
        WriteCache(Path.Combine(mods, "modVanillaCopy"), Enumerable.Range(13, 3).Select(i => Copy(small[i].C, small[i].E)));
        // 4. Priority from mods.settings: sorts last by name, loads first by priority.
        WriteCache(Path.Combine(mods, "modZZZ_Priority"), new[] { Copy(big[0].C, big[0].E, small[16].E.Hash, small[16].E.Name) });
        WriteCache(Path.Combine(mods, "modMMM_LosesToPriority"), new[] { Copy(small[16].C, small[16].E) });
        // 5. Disabled in mods.settings, and a folder the game ignores.
        WriteCache(Path.Combine(mods, "modDisabled"), new[] { Copy(big[1].C, big[1].E, small[17].E.Hash, small[17].E.Name) });
        WriteCache(Path.Combine(mods, "notAMod"), new[] { Copy(big[1].C, big[1].E, small[18].E.Hash, small[18].E.Name) });
        File.WriteAllText(Path.Combine(outDir, "mods.settings"), "[modZZZ_Priority]\r\nEnabled=1\r\nPriority=1\r\n[modDisabled]\r\nEnabled=0\r\nPriority=2\r\n");

        // 6. New item: new textures and a new mesh, named by an item definition (so they count as referenced);
        //    one leftover texture nothing points at.
        var meshes = g.Files.Where(f => f.E.Name.EndsWith(".w2mesh", StringComparison.OrdinalIgnoreCase) && f.E.Size < 4 << 20).ToList();
        var parsed = new List<(string Name, byte[] Data, MeshDetails M, Cr2wFile C)>();
        foreach (var (b, e) in meshes.Where((_, i) => i % 23 == 0))
        {
            var d = b.Read(e);
            if (!Cr2wFile.LooksLikeCr2w(d)) continue;
            var c = new Cr2wFile(d!);
            if (MeshReader.Read(c) is { Tris0: > 0 } m) parsed.Add((e.Name, d!, m, c));
            if (parsed.Count >= 400) break;
        }
        var light = parsed.Where(p => p.M.Tris0 is > 200 and < 3000).First();
        var dense = parsed.OrderByDescending(p => p.M.Tris0).First();
        Console.WriteLine($"Meshes: light {light.Name} ({light.M.Tris0:N0} tris), dense {dense.Name} ({dense.M.Tris0:N0} tris).");
        string armor = @"dlc\fixture\armor\";
        var newTex = Enumerable.Range(0, 4).Select(i => $"{armor}armor_{i}.xbm").ToList();
        WriteCache(Path.Combine(mods, "modNewArmor"), newTex.Select((n, i) => Copy(big[i + 2].C, big[i + 2].E, Fnv1a32(n), n))
            .Append(Copy(big[7].C, big[7].E, Fnv1a32(armor + "leftover.xbm"), armor + "leftover.xbm")));
        var itemXml = $"<redxml><items><item name=\"Fixture armor\" equip_template=\"{armor}armor.w2mesh\"/>{string.Concat(newTex.Select(n => $"<tex path=\"{n}\"/>"))}</items></redxml>";
        BundleWriter.Write(Path.Combine(mods, "modNewArmor", "content", "blob0.bundle"), new (string, byte[])[]
        {
            (armor + "armor.w2mesh", dense.Data),
            (@"gameplay\items\def_fixture_armor.xml", Encoding.UTF8.GetBytes(itemXml)),
        });

        // 7. A replaced game file that pulls in a new texture: the texture then loads in normal play.
        var material = g.Files.Where(f => f.E.Name.EndsWith(".w2mi", StringComparison.OrdinalIgnoreCase)).Select(f => (f.E.Name, Data: f.B.Read(f.E)))
            .Where(f => Cr2wFile.LooksLikeCr2w(f.Data)).Select(f => (f.Name, f.Data, C: new Cr2wFile(f.Data!)))
            .First(f => f.C.Imports.Any(i => i.Path.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase) && i.Path.Length > 24));
        var oldImport = material.C.Imports.First(i => i.Path.EndsWith(".xbm", StringComparison.OrdinalIgnoreCase) && i.Path.Length > 24).Path;
        var newImport = SameLength(oldImport, @"fixture\pulled_in_");
        BundleWriter.Write(Path.Combine(mods, "modRetexMaterial", "content", "blob0.bundle"), new[] { (material.Name, PatchImport(material.Data!, oldImport, newImport)) }, version: 3, scheme: Codec.Lz4);
        WriteCache(Path.Combine(mods, "modRetexMaterial"), new[] { Copy(big[8].C, big[8].E, Fnv1a32(newImport), newImport) });

        // 8. Dense mesh replacing a light one, in an old-format (v3) bundle.
        BundleWriter.Write(Path.Combine(mods, "modDenseMesh", "content", "blob0.bundle"), new[] { (light.Name, dense.Data) }, version: 3, scheme: Codec.Zlib);

        // 9. Missing requirement: a replaced mesh that points at a material nobody has.
        var withImport = parsed.First(p => p.C.Imports.Any(i => i.Path.Length > 20) && p.Name != light.Name);
        var imp = withImport.C.Imports.First(i => i.Path.Length > 20).Path;
        BundleWriter.Write(Path.Combine(mods, "modMissingRequirement", "content", "blob0.bundle"),
            new[] { (withImport.Name, PatchImport(withImport.Data, imp, SameLength(imp, @"not_installed\"))) }, version: 5, scheme: Codec.None);

        // 10. Broken downloads: a texture cache and a bundle cut off halfway.
        var brokenDir = Path.Combine(mods, "modBrokenDownload", "content");
        Directory.CreateDirectory(brokenDir);
        var goodCache = File.ReadAllBytes(Path.Combine(mods, "modVanillaCopy", "content", "texture.cache"));
        File.WriteAllBytes(Path.Combine(brokenDir, "texture.cache"), goodCache[..(goodCache.Length / 2)]);
        var goodBundle = File.ReadAllBytes(Path.Combine(mods, "modDenseMesh", "content", "blob0.bundle"));
        File.WriteAllBytes(Path.Combine(brokenDir, "blob0.bundle"), goodBundle[..(goodBundle.Length / 2)]);

        // 11. DLC-style mod: new 4K textures that only load when the item is used.
        WriteCache(Path.Combine(outDir, "dlc", "dlcFixtureItems"), Enumerable.Range(0, 3).Select(i => Copy(big[i + 9].C, big[i + 9].E, Fnv1a32($@"dlc\fixtureitems\sword_{i}.xbm"), $@"dlc\fixtureitems\sword_{i}.xbm")));
        File.WriteAllText(Path.Combine(outDir, "dlc", "dlcFixtureItems", "content", "items.xml"),
            "<redxml>" + string.Concat(Enumerable.Range(0, 3).Select(i => $"<t p=\"dlc\\fixtureitems\\sword_{i}.xbm\"/>")) + "</redxml>");

        // 12. Vortex deployment manifest and a Mod Organizer 2 instance.
        File.WriteAllText(Path.Combine(outDir, "vortex.deployment.json"),
            "{\"files\":[{\"relPath\":\"mods\\\\modHDFaces\\\\content\\\\texture.cache\",\"source\":\"HD Faces Fixture-4242-1-0-1700000000\"}]}");
        var mo2 = Path.Combine(outDir, "_mo2");
        Directory.CreateDirectory(Path.Combine(mo2, "profiles", "Default"));
        File.WriteAllText(Path.Combine(mo2, "ModOrganizer.ini"), "[General]\r\ngameName=The Witcher 3\r\nselected_profile=@ByteArray(Default)\r\n");
        File.WriteAllText(Path.Combine(mo2, "profiles", "Default", "modlist.txt"), "+MO2 Texture Pack\r\n-MO2 Disabled Pack\r\n");
        WriteCache(Path.Combine(mo2, "mods", "MO2 Texture Pack", "mods", "modMo2Textures"), new[] { Copy(big[3].C, big[3].E, small[20].E.Hash, small[20].E.Name) });
        File.WriteAllText(Path.Combine(mo2, "mods", "MO2 Texture Pack", "meta.ini"), "[General]\r\nmodid=777\r\n");
        WriteCache(Path.Combine(mo2, "mods", "MO2 Disabled Pack", "mods", "modMo2Disabled"), new[] { Copy(big[4].C, big[4].E, small[21].E.Hash, small[21].E.Name) });

        Console.WriteLine($"Fixtures written to {outDir}");
        Console.WriteLine($"Scan with: --mod-root \"{outDir}\" --mo2 \"{mo2}\"");
        return 0;
    }

    /// <summary>
    /// A real test mod for the game: <paramref name="count"/> of the game's own 2K+ textures with their largest
    /// mips dropped, written the same way the budget fix writes them.
    /// </summary>
    public static int MakeIngameTest(string game, string modsDir, int count, string? filter, int halvings)
    {
        using var g = new Game(game);
        var picks = g.Textures.Where(t => Math.Max(t.E.Width, t.E.Height) >= 2048 && t.E.StreamMips >= halvings
            && (filter == null || filter.Split(',').Any(f => t.E.Name.Contains(f, StringComparison.OrdinalIgnoreCase)))).Take(count).ToList();
        var dir = Path.Combine(modsDir, "mod0000_" + Scanner.FixMarker + "_Test");
        var content = Path.Combine(dir, "content");
        long before = 0, after = 0; int skipped = 0;
        using (var w = new TextureCacheWriter(content, g.Caches.Select(c => c.Version).DefaultIfEmpty(7u).Max()))
        {
            foreach (var (c, e) in picks)
            {
                var res = TextureShrinker.Shrink(c, e, c.ReadRaw(e), halvings, out _);
                if (res == null) { skipped++; continue; }
                w.Add(res);
                before += e.Size; after += res.Entry.Size;
            }
            if (w.Count == 0) { Console.Error.WriteLine("No textures matched."); return 1; }
            w.Write(Path.Combine(content, "texture.cache"));
        }
        Console.WriteLine($"Wrote {picks.Count - skipped} texture(s) to {dir} ({Report.Size(before)} -> {Report.Size(after)} of texture data; {skipped} skipped).");
        return 0;
    }
}
