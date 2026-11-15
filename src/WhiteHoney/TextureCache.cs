using System.Text;

namespace WhiteHoney;

public sealed class TexEntry
{
    public uint Hash;
    public string Name = "";
    /// <summary>Where the texture's data starts in the cache file, in bytes (stored as a 4096-byte page number).</summary>
    public long Offset;
    /// <summary>Bytes on disk, all blocks and their headers included.</summary>
    public uint ZSize;
    /// <summary>Uncompressed size of every mip in the cache: what the renderer uploads when the texture is shown at full size.</summary>
    public uint Size;
    public uint BaseAlignment;
    public int Width, Height;
    /// <summary>All mips of the texture, down to 1x1.</summary>
    public int Mips;
    /// <summary>Array slices, or 6 for a cube map.</summary>
    public int Slices;
    /// <summary>
    /// The entry's slots in the mip offset table. The data is MipOffsetCount + 1 blocks: the largest mips one per
    /// block, then one last block with all the small ones together.
    /// </summary>
    public int MipOffsetIndex, MipOffsetCount;
    /// <summary>Version 7 (Remastered) keeps a second 16-bit value next to the mip count; copied as it is.</summary>
    public ushort Extra;
    public long Timestamp;
    /// <summary>Low byte: 7 = BC1, 8 = BC3, 0xFD = uncompressed RGBA ...</summary>
    public ushort Format;
    public ushort CubeFlag;

    public bool IsCube => CubeFlag != 0;
    public int Faces => Math.Max(1, Slices);
    /// <summary>Mips stored in a block of their own: the ones that can be dropped to shrink the texture.</summary>
    public int StreamMips => MipOffsetCount;
    public string Stamp => $"{Size}:{ZSize}:{Width}x{Height}:{Timestamp:X}";

    /// <summary>Bits per pixel, from the top mip's share of the data: 4 = BC1/BC4, 8 = BC3/BC5/BC7, 32 = uncompressed.</summary>
    public double BitsPerPixel
    {
        get
        {
            double pixels = 0;
            for (int i = 0; i < Math.Max(1, Mips); i++) pixels += Math.Max(1, Width >> i) * (double)Math.Max(1, Height >> i);
            pixels *= Faces;
            return pixels > 0 ? Size * 8.0 / pixels : 0;
        }
    }

    public bool Uncompressed => (Format & 0xFF) == 0xFD || BitsPerPixel >= 24;
    public string FormatName => (Format & 0xFF) switch
    {
        7 => "BC1", 8 => "BC3", 0xFD => "uncompressed",
        _ => BitsPerPixel switch { <= 0 => "?", < 6 => "BC4", < 12 => "BC5/BC7", < 24 => "16-bit", _ => "uncompressed" },
    };
}

/// <summary>One block of a texture: a 9-byte header (compressed size, size, blocks that follow) and zlib data.</summary>
public readonly record struct TexBlock(int HeaderOffset, uint ZSize, uint Size, byte Tag);

/// <summary>
/// texture.cache ("HCXT"; version 6 up to game 4.04, version 7 in Remastered): every texture's mips, zlib-compressed.
/// The index sits at the end of the file: mip offset table, names, 52-byte entries, 32-byte footer.
/// An entry's data is a run of blocks, each with a 9-byte header: the largest mip (all slices or cube faces
/// together), then the next ones at the offsets the mip table gives, and a last block holding every mip of
/// 32 px and smaller.
/// </summary>
public sealed class TextureCache : IDisposable
{
    public const int EntrySize = 52, FooterSize = 32, PageSize = 4096;
    public string Path { get; }
    public uint Version { get; }
    public List<TexEntry> Entries { get; } = new();
    public uint[] MipOffsets { get; } = Array.Empty<uint>();
    readonly FileStream _fs;

    public TextureCache(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        try
        {
            if (_fs.Length < FooterSize) throw new InvalidDataException("truncated texture cache (too small)");
            var foot = new byte[FooterSize];
            _fs.Position = _fs.Length - FooterSize;
            _fs.ReadExactly(foot);
            if (Encoding.ASCII.GetString(foot, 24, 4) != "HCXT") throw new InvalidDataException("not a texture cache, or truncated (no HCXT footer)");
            Version = BitConverter.ToUInt32(foot, 28);
            uint count = BitConverter.ToUInt32(foot, 12), namesSize = BitConverter.ToUInt32(foot, 16), mipCount = BitConverter.ToUInt32(foot, 20);
            long tables = (long)mipCount * 4 + namesSize + (long)count * EntrySize;
            if (tables > _fs.Length - FooterSize) throw new InvalidDataException("corrupt texture cache (index larger than the file)");
            var data = new byte[tables];
            _fs.Position = _fs.Length - FooterSize - tables;
            _fs.ReadExactly(data);

            MipOffsets = new uint[mipCount];
            Buffer.BlockCopy(data, 0, MipOffsets, 0, (int)mipCount * 4);
            int names = (int)mipCount * 4, entries = names + (int)namesSize;
            long dataEnd = _fs.Length - FooterSize - tables;
            for (int i = 0; i < count; i++)
            {
                int p = entries + i * EntrySize;
                var e = new TexEntry
                {
                    Hash = BitConverter.ToUInt32(data, p),
                    Offset = (long)BitConverter.ToUInt32(data, p + 8) * PageSize,
                    ZSize = BitConverter.ToUInt32(data, p + 12),
                    Size = BitConverter.ToUInt32(data, p + 16),
                    BaseAlignment = BitConverter.ToUInt32(data, p + 20),
                    Width = BitConverter.ToUInt16(data, p + 24),
                    Height = BitConverter.ToUInt16(data, p + 26),
                    Mips = BitConverter.ToUInt16(data, p + 28),
                    Slices = BitConverter.ToUInt16(data, p + 30),
                    MipOffsetIndex = BitConverter.ToInt32(data, p + 32),
                    MipOffsetCount = BitConverter.ToUInt16(data, p + 36),
                    Extra = BitConverter.ToUInt16(data, p + 38),
                    Timestamp = BitConverter.ToInt64(data, p + 40),
                    Format = BitConverter.ToUInt16(data, p + 48),
                    CubeFlag = BitConverter.ToUInt16(data, p + 50),
                };
                int n = BitConverter.ToInt32(data, p + 4);
                if (n >= 0 && n < namesSize)
                {
                    int end = Array.IndexOf(data, (byte)0, names + n, (int)namesSize - n);
                    if (end < 0) end = names + (int)namesSize;
                    e.Name = Encoding.Latin1.GetString(data, names + n, end - names - n);
                }
                if (e.Offset + e.ZSize > dataEnd) throw new InvalidDataException("truncated texture cache (texture data runs past the end of the file)");
                if (e.MipOffsetIndex < 0 || e.MipOffsetCount < 0 || (long)e.MipOffsetIndex + e.MipOffsetCount > mipCount)
                    throw new InvalidDataException("corrupt texture cache (bad mip table)");
                Entries.Add(e);
            }
        }
        catch { _fs.Dispose(); throw; }
    }

    /// <summary>The entry's data exactly as stored (headers and compressed blocks).</summary>
    public byte[] ReadRaw(TexEntry e)
    {
        var buf = new byte[e.ZSize];
        lock (_fs)
        {
            _fs.Position = e.Offset;
            _fs.ReadExactly(buf);
        }
        return buf;
    }

    /// <summary>
    /// Splits an entry's data into its blocks. The first starts at 0; block i (i &gt;= 1) starts at the i-th offset of
    /// the entry's mip table; each must end exactly where the next begins. Returns null when the data doesn't
    /// follow that layout, so callers can leave such a texture alone.
    /// </summary>
    public List<TexBlock>? Blocks(TexEntry e, byte[] raw)
    {
        var blocks = new List<TexBlock>();
        for (int m = 0; m <= e.MipOffsetCount; m++)
        {
            long p = m == 0 ? 0 : MipOffsets[e.MipOffsetIndex + m - 1];
            long end = m < e.MipOffsetCount ? MipOffsets[e.MipOffsetIndex + m] : raw.Length;
            if (p + 9 > end || end > raw.Length) return null;
            uint zsize = BitConverter.ToUInt32(raw, (int)p), size = BitConverter.ToUInt32(raw, (int)p + 4);
            if (zsize == 0 || p + 9 + zsize != end) return null;
            blocks.Add(new TexBlock((int)p, zsize, size, raw[(int)p + 8]));
        }
        return blocks;
    }

    public void Dispose() => _fs.Dispose();

    /// <summary>FNV-1a 64 over the index tables: the footer's checksum.</summary>
    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong h = 0xcbf29ce484222325;
        foreach (var b in data) { h ^= b; h *= 0x100000001b3; }
        return h;
    }
}

/// <summary>A texture to write: its index fields and its data, already in cache layout.</summary>
public sealed record TexWrite(TexEntry Entry, byte[] Data, uint[] MipOffsets);

/// <summary>Writes a texture.cache (version 6 or 7): data on 4096-byte pages, then the index and footer.</summary>
public sealed class TextureCacheWriter : IDisposable
{
    readonly string _tmp;
    readonly FileStream _fs;
    readonly List<(TexEntry Entry, uint Page, uint[] Mips)> _items = new();
    public int Count => _items.Count;

    readonly uint _version;

    public TextureCacheWriter(string dir, uint version)
    {
        _version = version;
        Directory.CreateDirectory(dir);
        _tmp = System.IO.Path.Combine(dir, $"WhiteHoney-{Guid.NewGuid():N}.whitehoney-tmp");
        _fs = new FileStream(_tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
    }

    public void Add(TexWrite t)
    {
        lock (_fs)
        {
            long pad = (TextureCache.PageSize - _fs.Position % TextureCache.PageSize) % TextureCache.PageSize;
            if (pad > 0) _fs.Write(new byte[pad]);
            uint page = checked((uint)(_fs.Position / TextureCache.PageSize));
            _fs.Write(t.Data);
            _items.Add((t.Entry, page, t.MipOffsets));
        }
    }

    /// <summary>Finishes the file and moves it into place.</summary>
    public void Write(string path)
    {
        long pad = (TextureCache.PageSize - _fs.Position % TextureCache.PageSize) % TextureCache.PageSize;
        if (pad > 0) _fs.Write(new byte[pad]);
        uint usedPages = checked((uint)(_fs.Position / TextureCache.PageSize));

        var items = _items; // in data order, like the game's own caches
        using var tables = new MemoryStream();
        using var w = new BinaryWriter(tables);
        var mipIndex = new List<int>();
        int mipTotal = 0;
        foreach (var it in items) { mipIndex.Add(mipTotal); foreach (var o in it.Mips) w.Write(o); mipTotal += it.Mips.Length; }
        var nameOffsets = new List<int>();
        long namesStart = tables.Position;
        foreach (var it in items)
        {
            nameOffsets.Add((int)(tables.Position - namesStart));
            w.Write(Encoding.Latin1.GetBytes(it.Entry.Name));
            w.Write((byte)0);
        }
        uint namesSize = (uint)(tables.Position - namesStart);
        for (int i = 0; i < items.Count; i++)
        {
            var e = items[i].Entry;
            w.Write(e.Hash); w.Write(nameOffsets[i]); w.Write(items[i].Page); w.Write(e.ZSize); w.Write(e.Size); w.Write(e.BaseAlignment);
            w.Write((ushort)e.Width); w.Write((ushort)e.Height); w.Write((ushort)e.Mips); w.Write((ushort)e.Slices);
            w.Write(mipIndex[i]); w.Write((ushort)items[i].Mips.Length); w.Write(e.Extra); w.Write(e.Timestamp); w.Write(e.Format); w.Write(e.CubeFlag);
        }
        w.Flush();
        var index = tables.ToArray();
        _fs.Write(index);
        var foot = new byte[TextureCache.FooterSize];
        BitConverter.TryWriteBytes(foot.AsSpan(0), TextureCache.Fnv1a64(index));
        BitConverter.TryWriteBytes(foot.AsSpan(8), usedPages);
        BitConverter.TryWriteBytes(foot.AsSpan(12), (uint)items.Count);
        BitConverter.TryWriteBytes(foot.AsSpan(16), namesSize);
        BitConverter.TryWriteBytes(foot.AsSpan(20), (uint)mipTotal);
        Encoding.ASCII.GetBytes("HCXT").CopyTo(foot, 24);
        BitConverter.TryWriteBytes(foot.AsSpan(28), _version);
        _fs.Write(foot);
        _fs.Dispose();
        File.Move(_tmp, path, overwrite: true);
    }

    public void Dispose()
    {
        _fs.Dispose();
        try { if (File.Exists(_tmp)) File.Delete(_tmp); } catch { }
    }
}

public static class TextureShrinker
{
    /// <summary>
    /// Drops the largest <paramref name="halvings"/> mips of a texture. The mips that remain are the mod's own smaller
    /// versions, copied byte for byte: nothing is decoded or re-encoded. Returns null (with a reason) when the
    /// texture's data doesn't follow the usual layout.
    /// </summary>
    public static TexWrite? Shrink(TextureCache cache, TexEntry e, byte[] raw, int halvings, out string why)
    {
        why = "";
        var blocks = cache.Blocks(e, raw);
        if (blocks == null) { why = "unusual layout"; return null; }
        if (halvings < 1 || halvings > e.MipOffsetCount) { why = "no smaller mip to fall back on"; return null; }
        // Each block's last header byte counts the blocks after it; anything else is a layout we don't know.
        for (int i = 0; i < blocks.Count; i++)
            if (blocks[i].Tag != blocks.Count - 1 - i) { why = "unusual block numbering"; return null; }

        int start = blocks[halvings].HeaderOffset;
        var data = raw.AsSpan(start).ToArray();
        var offsets = new uint[blocks.Count - halvings - 1];
        for (int i = 0; i < offsets.Length; i++) offsets[i] = (uint)(blocks[halvings + 1 + i].HeaderOffset - start);
        long dropped = blocks.Take(halvings).Sum(b => (long)b.Size);
        var n = new TexEntry
        {
            Hash = e.Hash, Name = e.Name, ZSize = (uint)data.Length, Size = (uint)(e.Size - dropped), BaseAlignment = e.BaseAlignment,
            Width = Math.Max(1, e.Width >> halvings), Height = Math.Max(1, e.Height >> halvings), Mips = e.Mips - halvings, Slices = e.Slices,
            MipOffsetCount = offsets.Length, Extra = e.Extra, Timestamp = e.Timestamp, Format = e.Format, CubeFlag = e.CubeFlag,
        };
        return new TexWrite(n, data, offsets);
    }
}
