using System.Text;

namespace WhiteHoney;

/// <summary>
/// Reader for REDengine 3 resource files (CR2W versions 162-164: .w2mesh, .xbm, .w2ent, .w2mi ...).
/// Only what a VRAM scan needs: the import table (which files this one points to), the buffer table
/// (how much data it brings along) and the top-level properties of a chunk.
/// </summary>
public sealed class Cr2wFile
{
    public sealed record Export(string ClassName, int DataSize, int DataOffset);
    public sealed record Prop(string Name, string Type, int Offset, int Size);

    readonly byte[] _d;
    public uint Version { get; }
    public List<string> Names { get; } = new();
    public List<(string Path, string Class, ushort Flags)> Imports { get; } = new();
    public List<Export> Exports { get; } = new();
    /// <summary>Deferred buffers (mesh geometry and so on): size in memory of each.</summary>
    public List<uint> BufferSizes { get; } = new();
    public string RootClass => Exports.Count > 0 ? Exports[0].ClassName : "";

    public static bool LooksLikeCr2w(byte[]? d) => d != null && d.Length >= 160 && d[0] == 'C' && d[1] == 'R' && d[2] == '2' && d[3] == 'W';

    public Cr2wFile(byte[] d)
    {
        _d = d;
        if (!LooksLikeCr2w(d)) throw new InvalidDataException("not a CR2W file");
        Version = U32(4);
        (int Off, int Count) Table(int i) => ((int)U32(40 + i * 12), (int)U32(44 + i * 12));

        var (so, sc) = Table(0);
        var (no, nc) = Table(1);
        for (int i = 0; i < nc; i++) Names.Add(CStr(so + (int)U32(no + i * 8), so + sc));
        var (io, ic) = Table(2);
        for (int i = 0; i < ic; i++)
            Imports.Add((CStr(so + (int)U32(io + i * 8), so + sc), Name(U16(io + i * 8 + 4)), U16(io + i * 8 + 6)));
        var (eo, ec) = Table(4);
        for (int i = 0; i < ec; i++)
            Exports.Add(new Export(Name(U16(eo + i * 24)), (int)U32(eo + i * 24 + 8), (int)U32(eo + i * 24 + 12)));
        var (bo, bc) = Table(5);
        for (int i = 0; i < bc; i++) BufferSizes.Add(U32(bo + i * 24 + 16));
    }

    uint U32(int o) => BitConverter.ToUInt32(_d, o);
    ushort U16(int o) => BitConverter.ToUInt16(_d, o);
    string Name(int i) => i >= 0 && i < Names.Count ? Names[i] : "";

    string CStr(int o, int end)
    {
        int e = o;
        while (e < end && _d[e] != 0) e++;
        return Encoding.Latin1.GetString(_d, o, e - o);
    }

    /// <summary>
    /// The properties of a compound value starting at <paramref name="offset"/> (a chunk's data, or a struct inside
    /// one). Layout: one zero byte, then { name u16, type u16, size u32 (counts itself), value } until name 0.
    /// </summary>
    public List<Prop> Props(int offset, int end)
    {
        var list = new List<Prop>();
        int p = offset + 1;
        while (p + 2 <= end)
        {
            int n = U16(p);
            if (n == 0) break;
            if (p + 8 > end) break;
            int size = (int)U32(p + 4);
            if (size < 4 || p + 4 + size > end) break;
            list.Add(new Prop(Name(n), Name(U16(p + 2)), p + 8, size - 4));
            p += 4 + size;
        }
        return list;
    }

    public List<Prop> RootProps() => Exports.Count == 0 ? new() : Props(Exports[0].DataOffset, Exports[0].DataOffset + Exports[0].DataSize);

    public uint ReadU32(Prop p) => p.Size >= 4 ? U32(p.Offset) : p.Size >= 2 ? U16(p.Offset) : p.Size >= 1 ? _d[p.Offset] : 0u;
    /// <summary>An enum or CName value: stored as an index into the name table.</summary>
    public string ReadName(Prop p) => p.Size >= 2 ? Name(U16(p.Offset)) : "";
    public ReadOnlySpan<byte> Bytes(Prop p) => _d.AsSpan(p.Offset, p.Size);
}

public sealed record MeshDetails(long Tris0, long Verts0, int LodCount, long GpuBytes);

public static class MeshReader
{
    /// <summary>
    /// Geometry of a cooked CMesh. cookedData.renderChunks is a packed list (count byte, then 37 bytes per chunk:
    /// ... numVertices u16 @27, numIndices u32 @29, ..., lodMask u8 @36); renderLODs has one distance per LOD;
    /// vertexBufferSize + indexBufferSize is what the renderer uploads.
    /// </summary>
    public static MeshDetails? Read(Cr2wFile c)
    {
        if (c.RootClass != "CMesh") return null;
        var root = c.RootProps();
        long tris0 = 0, verts0 = 0, gpu = 0, trisAll = 0;
        int lods = 0;
        bool packed = false;
        var cooked = root.FirstOrDefault(p => p.Name == "cookedData");
        if (cooked != null)
        {
            foreach (var p in c.Props(cooked.Offset, cooked.Offset + cooked.Size))
            {
                switch (p.Name)
                {
                    case "renderLODs": lods = (int)c.ReadU32(p); break;
                    case "vertexBufferSize":
                    case "indexBufferSize": gpu += c.ReadU32(p); break;
                    case "renderChunks":
                    {
                        var b = c.Bytes(p);
                        if (b.Length < 5) break;
                        int n = b[4];
                        if (n == 0 || (b.Length - 5) != n * 37) break;
                        packed = true;
                        // Most detailed LOD the mesh has: bit 0, except for far-distance proxies, which start at LOD 1.
                        int masks = 0;
                        for (int i = 0; i < n; i++) masks |= b[5 + i * 37 + 36];
                        int top = masks & -masks;
                        for (int i = 0; i < n; i++)
                        {
                            var ch = b.Slice(5 + i * 37, 37);
                            long idx = BitConverter.ToUInt32(ch[29..]);
                            if ((ch[36] & top) != 0 || top == 0) { tris0 += idx / 3; verts0 += BitConverter.ToUInt16(ch[27..]); }
                        }
                        break;
                    }
                }
            }
        }
        if (!packed)
        {
            // Unknown packed layout: fall back to the plain chunk list, which has no LOD masks (all LODs counted).
            var chunks = root.FirstOrDefault(p => p.Name == "chunks");
            if (chunks != null && chunks.Size >= 4)
            {
                int n = (int)c.ReadU32(chunks), at = chunks.Offset + 4, end = chunks.Offset + chunks.Size;
                for (int i = 0; i < n && at < end; i++)
                {
                    var props = c.Props(at, end);
                    foreach (var p in props)
                    {
                        if (p.Name == "numIndices") trisAll += c.ReadU32(p) / 3;
                        else if (p.Name == "numVertices") verts0 += c.ReadU32(p);
                    }
                    at = props.Count > 0 ? props[^1].Offset + props[^1].Size + 2 : at + 3;
                }
                tris0 = trisAll;
            }
        }
        if (gpu == 0) gpu = c.BufferSizes.Sum(s => (long)s);
        return new MeshDetails(tris0, verts0, Math.Max(lods, 1), gpu);
    }
}

public sealed record BitmapDetails(int Width, int Height, string Compression, string Group, int ResidentMip, uint CacheKey);

public static class BitmapReader
{
    /// <summary>The header of a cooked .xbm: its big mips live in texture.cache under <see cref="BitmapDetails.CacheKey"/>.</summary>
    public static BitmapDetails? Read(Cr2wFile c)
    {
        if (c.RootClass != "CBitmapTexture") return null;
        int w = 0, h = 0, resident = 0; uint key = 0; string comp = "", group = "";
        foreach (var p in c.RootProps())
            switch (p.Name)
            {
                case "width": w = (int)c.ReadU32(p); break;
                case "height": h = (int)c.ReadU32(p); break;
                case "compression": comp = c.ReadName(p); break;
                case "textureGroup": group = c.ReadName(p); break;
                case "residentMipIndex": resident = (int)c.ReadU32(p); break;
                case "textureCacheKey": key = c.ReadU32(p); break;
            }
        return new BitmapDetails(w, h, comp, group, resident, key);
    }
}
