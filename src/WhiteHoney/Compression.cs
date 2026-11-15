using System.IO.Compression;

namespace WhiteHoney;

/// <summary>
/// The compression schemes REDengine 3 bundles and texture caches use. Unlike Cyberpunk's Oodle, all of them are
/// open formats, so nothing from the game is needed to read or write them.
/// </summary>
public static class Codec
{
    public const uint None = 0, Zlib = 1, Snappy = 2, Doboz = 3, Lz4 = 4, Lz4Hc = 5;

    /// <summary>Decompresses a bundle entry, or returns null when the scheme isn't supported (Doboz) or the data is damaged.</summary>
    public static byte[]? Decompress(uint scheme, byte[] src, int size)
    {
        try
        {
            switch (scheme)
            {
                case None: return src;
                case Zlib: return Inflate(src, size);
                case Snappy: return SnappyDecode(src, size);
                case Lz4:
                case Lz4Hc:
                {
                    var dst = new byte[size];
                    return Lz4Decode(src, dst) == size ? dst : null;
                }
                default: return null;
            }
        }
        catch { return null; }
    }

    public static byte[]? Inflate(byte[] src, int size) => Inflate(src, 0, src.Length, size);

    public static byte[]? Inflate(byte[] src, int offset, int count, int size)
    {
        var dst = new byte[size];
        using var z = new ZLibStream(new MemoryStream(src, offset, count), CompressionMode.Decompress);
        int got = 0;
        while (got < size)
        {
            int n = z.Read(dst, got, size - got);
            if (n <= 0) break;
            got += n;
        }
        return got == size ? dst : null;
    }

    public static byte[] Deflate(ReadOnlySpan<byte> src, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true)) z.Write(src);
        return ms.ToArray();
    }

    /// <summary>LZ4 block format (no frame header), as written by wcc_lite for cooked mod bundles.</summary>
    public static int Lz4Decode(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int s = 0, d = 0;
        while (s < src.Length)
        {
            int token = src[s++];
            int lit = token >> 4;
            if (lit == 15) { byte b; do { b = src[s++]; lit += b; } while (b == 255); }
            src.Slice(s, lit).CopyTo(dst[d..]);
            s += lit; d += lit;
            if (s >= src.Length) break;
            int offset = src[s] | src[s + 1] << 8;
            s += 2;
            int len = token & 15;
            if (len == 15) { byte b; do { b = src[s++]; len += b; } while (b == 255); }
            len += 4;
            int m = d - offset;
            if (m < 0) return -1;
            for (int i = 0; i < len; i++) dst[d++] = dst[m++];
        }
        return d;
    }

    static byte[]? SnappyDecode(byte[] src, int size)
    {
        int s = 0;
        long declared = 0;
        for (int shift = 0; ; shift += 7) { byte b = src[s++]; declared |= (long)(b & 0x7F) << shift; if ((b & 0x80) == 0) break; }
        if (declared != size) return null;
        var dst = new byte[size];
        int d = 0;
        while (s < src.Length)
        {
            int tag = src[s++];
            int len, offset;
            switch (tag & 3)
            {
                case 0:
                    len = tag >> 2;
                    if (len >= 60)
                    {
                        int n = len - 59;
                        len = 0;
                        for (int i = 0; i < n; i++) len |= src[s++] << (8 * i);
                    }
                    len++;
                    Buffer.BlockCopy(src, s, dst, d, len);
                    s += len; d += len;
                    continue;
                case 1: len = ((tag >> 2) & 7) + 4; offset = (tag >> 5) << 8 | src[s++]; break;
                case 2: len = (tag >> 2) + 1; offset = src[s] | src[s + 1] << 8; s += 2; break;
                default: len = (tag >> 2) + 1; offset = BitConverter.ToInt32(src, s); s += 4; break;
            }
            int m = d - offset;
            if (m < 0) return null;
            for (int i = 0; i < len; i++) dst[d++] = dst[m++];
        }
        return d == size ? dst : null;
    }
}
