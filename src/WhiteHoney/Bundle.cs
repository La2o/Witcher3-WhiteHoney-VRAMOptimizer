using System.Text;

namespace WhiteHoney;

public sealed record BundleEntry(string Name, long Offset, uint Size, uint ZSize, uint Compression, uint Crc);

/// <summary>
/// A REDengine 3 bundle ("POTATO70"). Two layouts exist:
///   v3 (game 1.x - 4.04, and every mod cooked for them): 320-byte entries, 32-bit offsets;
///   v5 (Remastered 5.00): 304-byte entries, 64-bit offsets.
/// Both keep the file's depot path as plain text, so no hash list is needed.
/// </summary>
public sealed class Bundle : IDisposable
{
    public string Path { get; }
    public int Version { get; }
    public List<BundleEntry> Entries { get; } = new();
    readonly FileStream _fs;

    public Bundle(string path)
    {
        Path = path;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        try
        {
            var head = new byte[0x20];
            if (_fs.Read(head, 0, head.Length) != head.Length || Encoding.ASCII.GetString(head, 0, 8) != "POTATO70")
                throw new InvalidDataException("not a bundle (no POTATO70 header)");
            uint table = BitConverter.ToUInt32(head, 0x10);
            Version = BitConverter.ToUInt16(head, 0x14);
            if (table > _fs.Length - 0x20) throw new InvalidDataException("truncated bundle (file table runs past the end of the file)");
            // The header says how large the bundle should be (64-bit in v5, 32-bit before): a shorter file is a cut-off download.
            long stated = Version >= 5 ? BitConverter.ToInt64(head, 8) : BitConverter.ToUInt32(head, 8);
            if (stated > _fs.Length) throw new InvalidDataException($"truncated bundle ({_fs.Length:N0} of {stated:N0} bytes)");
            var data = new byte[table];
            _fs.ReadExactly(data);
            // The header version says which layout this is; if it lies, fall back to whichever layout parses cleanly.
            int first = Version >= 5 ? 304 : 320;
            if (!TryParse(data, first) && !TryParse(data, first == 304 ? 320 : 304))
                throw new InvalidDataException("corrupt bundle (file table unreadable)");
        }
        catch { _fs.Dispose(); throw; }
    }

    bool TryParse(byte[] data, int entrySize)
    {
        if (data.Length % entrySize != 0) return false;
        Entries.Clear();
        long min = 0x20 + data.Length, max = _fs.Length;
        for (int p = 0; p < data.Length; p += entrySize)
        {
            int len = Array.IndexOf(data, (byte)0, p, 256) - p;
            if (len <= 0) { Entries.Clear(); return false; }
            string name = Encoding.Latin1.GetString(data, p, len);
            long offset; uint size, zsize, crc, comp;
            if (entrySize == 304)
            {
                offset = BitConverter.ToInt64(data, p + 272);
                size = BitConverter.ToUInt32(data, p + 280);
                zsize = BitConverter.ToUInt32(data, p + 284);
                crc = BitConverter.ToUInt32(data, p + 288);
                comp = BitConverter.ToUInt32(data, p + 292);
            }
            else
            {
                size = BitConverter.ToUInt32(data, p + 276);
                zsize = BitConverter.ToUInt32(data, p + 280);
                offset = BitConverter.ToUInt32(data, p + 284);
                crc = BitConverter.ToUInt32(data, p + 312);
                comp = BitConverter.ToUInt32(data, p + 316);
            }
            if (comp > 5 || offset < min || offset + zsize > max) { Entries.Clear(); return false; }
            Entries.Add(new BundleEntry(name, offset, size, zsize, comp, crc));
        }
        return true;
    }

    public byte[] ReadRaw(BundleEntry e)
    {
        var buf = new byte[e.ZSize];
        lock (_fs)
        {
            _fs.Position = e.Offset;
            _fs.ReadExactly(buf);
        }
        return buf;
    }

    /// <summary>The file's content, or null when it can't be decompressed.</summary>
    public byte[]? Read(BundleEntry e) => Codec.Decompress(e.Compression, ReadRaw(e), (int)e.Size);

    public void Dispose() => _fs.Dispose();

    /// <summary>Depot paths compare case-insensitively with backslashes.</summary>
    public static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
}
