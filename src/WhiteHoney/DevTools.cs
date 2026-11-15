namespace WhiteHoney;

/// <summary>Developer self-tests: they check this tool's reading and writing against real game files.</summary>
public static class DevTools
{
    public static int DumpCache(string path, int count, string? filter)
    {
        using var c = new TextureCache(path);
        Console.WriteLine($"{path}: version {c.Version}, {c.Entries.Count:N0} textures, {c.MipOffsets.Length:N0} mip offsets");
        Console.WriteLine($"formats: {string.Join(", ", c.Entries.GroupBy(e => e.Format).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()} ({g.First().BitsPerPixel:0.#}bpp)"))}");
        Console.WriteLine($"extra field: {string.Join(", ", c.Entries.GroupBy(e => e.Extra).Select(g => $"{g.Key}x{g.Count()}"))}; alignment: {string.Join(", ", c.Entries.GroupBy(e => e.BaseAlignment).Select(g => $"{g.Key}x{g.Count()}"))}");
        Console.WriteLine($"cube flags: {string.Join(", ", c.Entries.GroupBy(e => e.CubeFlag).Select(g => $"{g.Key}x{g.Count()}"))}; slices: {string.Join(", ", c.Entries.GroupBy(e => e.Slices).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}");
        Console.WriteLine($"sizes: {string.Join(", ", c.Entries.GroupBy(e => Math.Max(e.Width, e.Height)).OrderByDescending(g => g.Key).Select(g => $"{g.Key}px x{g.Count()}"))}");
        foreach (var e in c.Entries.Where(e => filter == null || e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).Take(count))
        {
            Console.WriteLine($"{e.Hash:X8} {e.Name}");
            Console.WriteLine($"   {e.Width}x{e.Height} mips={e.Mips} slices={e.Slices} cube={e.CubeFlag} fmt={e.Format} extra={e.Extra} ts={e.Timestamp:X} align={e.BaseAlignment} zsize={e.ZSize} size={e.Size} mipOffsets={e.MipOffsetCount} bpp={e.BitsPerPixel:0.##}");
            var raw = c.ReadRaw(e);
            var blocks = c.Blocks(e, raw);
            if (blocks == null) { Console.WriteLine($"   !! unusual layout; first bytes {Convert.ToHexString(raw.AsSpan(0, Math.Min(32, raw.Length)))}"); continue; }
            Console.WriteLine("   " + string.Join(" | ", blocks.Select(b => $"{b.Size}/{b.ZSize}t{b.Tag}")));
        }
        return 0;
    }

    /// <summary>Checks that sampled textures split into mips as expected and that every block inflates to its stated size.</summary>
    public static int VerifyCache(string path, int samples)
    {
        using var c = new TextureCache(path);
        int step = Math.Max(1, c.Entries.Count / Math.Max(1, samples)), ok = 0, bad = 0, checkedCount = 0;
        var problems = new Dictionary<string, int>();
        void Problem(string what, TexEntry e)
        {
            bad++;
            problems[what] = problems.GetValueOrDefault(what) + 1;
            if (problems[what] <= 3) Console.WriteLine($"  {what}: {e.Name} ({e.Width}x{e.Height} mips={e.Mips} slices={e.Slices} cube={e.CubeFlag})");
        }
        for (int i = 0; i < c.Entries.Count; i += step)
        {
            var e = c.Entries[i];
            checkedCount++;
            var raw = c.ReadRaw(e);
            var mips = c.Blocks(e, raw);
            if (mips == null) { Problem("unusual layout", e); continue; }
            long total = mips.Sum(b => (long)b.Size);
            if (total != e.Size) { Problem($"sizes don't add up ({total} vs {e.Size})", e); continue; }
            if (mips.Where((b, k) => b.Tag != mips.Count - 1 - k).Any()) { Problem("unusual block numbering", e); continue; }
            // All but the last block hold one mip: each a quarter of the one before.
            if (mips.Take(mips.Count - 1).Where((b, k) => k > 0 && b.Size * 4 != mips[k - 1].Size).Any()) { Problem("a mip isn't a quarter of the one before", e); continue; }
            if (e.MipOffsetCount > 0 && e.Mips - e.MipOffsetCount != 6) { Problem($"small-mip block doesn't hold 6 mips ({e.Mips} mips, {e.MipOffsetCount + 1} blocks)", e); continue; }
            bool inflated = true;
            foreach (var b in mips)
                if (Codec.Inflate(raw, b.HeaderOffset + 9, (int)b.ZSize, (int)b.Size) == null) { inflated = false; break; }
            if (!inflated) { Problem("a block doesn't inflate", e); continue; }
            ok++;
        }
        Console.WriteLine($"{path}: checked {checkedCount:N0} of {c.Entries.Count:N0} textures: {ok:N0} ok, {bad:N0} with problems");
        foreach (var (k, v) in problems) Console.WriteLine($"  {v:N0} x {k}");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>Shrinks sampled 1K+ textures, writes them to a new cache, reads it back and re-checks every block.</summary>
    public static int VerifyShrink(string path, int samples)
    {
        var dir = Path.Combine(Path.GetTempPath(), "whitehoney-shrink-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var c = new TextureCache(path);
            var big = c.Entries.Where(e => Math.Max(e.Width, e.Height) >= 1024 && e.StreamMips >= 2).ToList();
            int step = Math.Max(1, big.Count / Math.Max(1, samples));
            var expected = new Dictionary<uint, (TexEntry Src, int Halvings, List<byte[]> Mips)>();
            int skipped = 0;
            using (var w = new TextureCacheWriter(dir, c.Version))
            {
                for (int i = 0; i < big.Count; i += step)
                {
                    var e = big[i];
                    int halvings = 1 + i / step % 2;
                    if (halvings >= e.StreamMips) halvings = 1;
                    var raw = c.ReadRaw(e);
                    var res = TextureShrinker.Shrink(c, e, raw, halvings, out var why);
                    if (res == null) { skipped++; if (skipped <= 5) Console.WriteLine($"  skipped {e.Name}: {why}"); continue; }
                    w.Add(res);
                    var mips = c.Blocks(e, raw)!;
                    expected[e.Hash] = (e, halvings, mips.Skip(halvings).Select(b => Codec.Inflate(raw, b.HeaderOffset + 9, (int)b.ZSize, (int)b.Size)!).ToList());
                }
                w.Write(Path.Combine(dir, "texture.cache"));
            }
            int ok = 0, bad = 0;
            using (var n = new TextureCache(Path.Combine(dir, "texture.cache")))
            {
                foreach (var e in n.Entries)
                {
                    var (src, halvings, want) = expected[e.Hash];
                    var raw = n.ReadRaw(e);
                    var mips = n.Blocks(e, raw);
                    bool good = mips != null && mips.Count == want.Count && e.Mips == src.Mips - halvings
                        && e.Width == Math.Max(1, src.Width >> halvings) && e.Height == Math.Max(1, src.Height >> halvings) && e.Name == src.Name
                        && e.Size == want.Sum(m => (long)m.Length) && e.Extra == src.Extra && e.Format == src.Format && e.Slices == src.Slices && e.CubeFlag == src.CubeFlag;
                    if (good)
                        for (int m = 0; m < want.Count && good; m++)
                        {
                            var b = mips![m];
                            var got = Codec.Inflate(raw, b.HeaderOffset + 9, (int)b.ZSize, (int)b.Size) ?? Array.Empty<byte>();
                            good = got.AsSpan().SequenceEqual(want[m]) && b.Tag == want.Count - 1 - m;
                        }
                    if (good) ok++; else { bad++; if (bad <= 5) Console.WriteLine($"  MISMATCH {e.Name}"); }
                }
                Console.WriteLine($"version {n.Version}: shrunk and re-read {n.Entries.Count:N0} textures: {ok:N0} identical to the source's smaller mips, {bad:N0} wrong, {skipped:N0} skipped");
            }
            return bad == 0 ? 0 : 1;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Runs the mesh and reference readers over the game's own files: every mesh should parse, and no vanilla file
    /// should have a hard reference to a file that doesn't exist (or the "missing reference" flag would cry wolf).
    /// </summary>
    public static int VanillaCheck(string game, int every)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bundles = new List<Bundle>();
        foreach (var dir in Scanner.BaseContentDirs(game))
            foreach (var f in Scanner.ContentFiles(dir, "*.bundle"))
            {
                var b = new Bundle(f);
                bundles.Add(b);
                foreach (var e in b.Entries) all.Add(Bundle.Normalize(e.Name));
            }
        Console.WriteLine($"{bundles.Count} bundles (versions {string.Join(",", bundles.Select(b => b.Version).Distinct())}), {all.Count:N0} files");
        int files = 0, cr2w = 0, unreadable = 0, meshes = 0, meshNoChunks = 0, hard = 0, missing = 0, soft = 0, softMissing = 0, n = 0;
        var versions = new Dictionary<uint, int>();
        var flagStats = new Dictionary<ushort, (int Count, int Missing)>();
        var compression = new Dictionary<uint, int>();
        var missingSamples = new List<string>();
        long tris = 0, gpu = 0;
        foreach (var b in bundles)
            foreach (var e in b.Entries)
            {
                compression[e.Compression] = compression.GetValueOrDefault(e.Compression) + 1;
                if (e.Name.EndsWith(".buffer", StringComparison.OrdinalIgnoreCase) || e.Size > 64 << 20) continue;
                if (n++ % every != 0) continue;
                files++;
                var data = b.Read(e);
                if (data == null) { unreadable++; continue; }
                if (!Cr2wFile.LooksLikeCr2w(data)) continue;
                Cr2wFile c;
                try { c = new Cr2wFile(data); } catch { unreadable++; continue; }
                cr2w++;
                versions[c.Version] = versions.GetValueOrDefault(c.Version) + 1;
                foreach (var imp in c.Imports)
                {
                    bool there = all.Contains(Bundle.Normalize(imp.Path));
                    var fs = flagStats.GetValueOrDefault(imp.Flags);
                    flagStats[imp.Flags] = (fs.Count + 1, fs.Missing + (there ? 0 : 1));
                    if ((imp.Flags & 4) != 0) { soft++; if (!there) softMissing++; }
                    else { hard++; if (!there) { missing++; if (missingSamples.Count < 12) missingSamples.Add($"{e.Name} -> {imp.Path} ({imp.Class}, flags {imp.Flags})"); } }
                }
                if (c.RootClass == "CMesh")
                {
                    var m = MeshReader.Read(c);
                    meshes++;
                    if (m == null || m.Tris0 == 0) { meshNoChunks++; if (meshNoChunks <= 8) Console.WriteLine($"  no triangles: {e.Name} ({e.Size} bytes, buffers {string.Join("+", c.BufferSizes)}, props {string.Join(",", c.RootProps().Select(p => p.Name + ":" + p.Size))})"); }
                    else { tris += m.Tris0; gpu += m.GpuBytes; }
                }
            }
        foreach (var b in bundles) b.Dispose();
        Console.WriteLine($"read {files:N0} files: {cr2w:N0} CR2W (versions {string.Join(", ", versions.Select(v => $"{v.Key}x{v.Value}"))}), {unreadable:N0} unreadable");
        Console.WriteLine($"compression schemes: {string.Join(", ", compression.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}"))}");
        Console.WriteLine($"meshes: {meshes:N0}, without triangles read: {meshNoChunks:N0}; LOD0 triangles {tris:N0}, geometry {Report.Size(gpu)}");
        Console.WriteLine($"hard references: {hard:N0}, pointing nowhere: {missing:N0}; soft references: {soft:N0}, pointing nowhere: {softMissing:N0}");
        Console.WriteLine($"by import flags: {string.Join(", ", flagStats.OrderBy(k => k.Key).Select(k => $"{k.Key}: {k.Value.Count:N0} ({k.Value.Missing:N0} missing)"))}");
        foreach (var s in missingSamples) Console.WriteLine("  " + s);
        return 0;
    }
}
