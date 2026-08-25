using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

// Generates the multi-resolution .ico (padlock on a rounded blue tile) used by
// the app and the installer. Pure BCL: SDF anti-aliased rendering, BMP (DIB)
// frames for the classic sizes plus one PNG-encoded 256px frame.
//
//   dotnet run --project src/RAM.IconGen -c Release -- <out.ico>
//
// All geometry lives in a 256x256 design space and is scaled per frame, so
// every size is a fresh render (no blurry downscaling).

internal static class IconGen
{
    private static readonly int[] Sizes = { 16, 24, 32, 48, 64, 128, 256 };

    private static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "ram.ico";
        string full = Path.GetFullPath(outPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // optional second arg: also write one PNG per size (handy for previews)
        string pngDir = args.Length > 1 ? Path.GetFullPath(args[1]) : "";
        if (pngDir.Length > 0) Directory.CreateDirectory(pngDir);

        var frames = new List<(int size, byte[] data)>();
        foreach (int s in Sizes)
        {
            byte[] rgba = RenderRgba(s);
            frames.Add((s, s == 256 ? Png(rgba, s) : Dib(rgba, s)));
            if (pngDir.Length > 0)
                File.WriteAllBytes(Path.Combine(pngDir, $"ram-{s}.png"), Png(rgba, s));
        }

        File.WriteAllBytes(full, PackIco(frames));
        Console.WriteLine($"wrote {full} ({new FileInfo(full).Length} bytes, {Sizes.Length} sizes)");
    }

    // ---------------------------------------------------------------- render

    private static byte[] RenderRgba(int s)
    {
        var rgba = new byte[s * s * 4];
        float k = s / 256f; // design-space px -> target px
        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                (byte r, byte g, byte b, float a) = Pixel((x + 0.5f) / k, (y + 0.5f) / k);
                int i = (y * s + x) * 4;
                rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b;
                rgba[i + 3] = (byte)(a * 255f + 0.5f);
            }
        }
        return rgba;
    }

    private static (byte r, byte g, byte b, float a) Pixel(float px, float py)
    {
        // rounded tile (fully transparent outside the corners)
        float sdTile = SdRoundRect(px, py, 128, 128, 118, 118, 52);
        float covTile = Cov(sdTile);
        if (covTile <= 0f) return (0, 0, 0, 0);

        (float tr, float tg, float tb) = TileColor(py);
        float r = tr, g = tg, b = tb;

        // subtle darker inner edge for definition at large sizes
        float covRing = Cov(Math.Max(sdTile, -SdRoundRect(px, py, 128, 128, 113, 113, 47))) * 0.45f;
        if (covRing > 0f) { r = L(r, 0x06, covRing); g = L(g, 0x2B, covRing); b = L(b, 0x5E, covRing); }

        // padlock: a thin, short shackle arc on a large body. The proportions
        // are what sell it — thick ring + compact mass reads as a kettlebell;
        // thin arc (9% thickness, ~75% of body width, legs diving into the
        // body top) reads as a lock.
        float sdShackle = Math.Max(
            SdRoundRect(px, py, 128, 96, 44, 22, 20),
            -Math.Max(SdRoundRect(px, py, 128, 98, 36, 14, 14), py - 116f));
        float sdBody = SdRoundRect(px, py, 128, 160, 58, 44, 24);
        float covLock = Cov(Math.Min(sdShackle, sdBody));
        if (covLock > 0f) { r = L(r, 255, covLock); g = L(g, 255, covLock); b = L(b, 255, covLock); }

        // small keyhole dot + stem cut into the body (navy, reads at any size)
        float sdKey = Math.Min(
            SdCircle(px, py, 128, 162, 8),
            SdTriangle(px, py, 124, 171, 132, 171, 128, 183));
        float covKey = Cov(sdKey);
        if (covKey > 0f) { r = L(r, 0x0B, covKey); g = L(g, 0x4E, covKey); b = L(b, 0x9E, covKey); }

        return ((byte)(r + 0.5f), (byte)(g + 0.5f), (byte)(b + 0.5f), covTile);
    }

    private static (float r, float g, float b) TileColor(float py)
    {
        float t = Math.Clamp((py - 10f) / 236f, 0f, 1f); // 10..246 in design space
        return (Lerp(0x2F, 0x0A, t), Lerp(0xA9, 0x62, t), Lerp(0xFF, 0xC8, t));
    }

    // coverage from a signed distance: soft 1px anti-aliased edge
    private static float Cov(float d) => Math.Clamp(0.5f - d, 0f, 1f);
    private static float L(float from, float to, float t) => from + (to - from) * t;
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static float SdCircle(float px, float py, float cx, float cy, float r)
    {
        float dx = px - cx, dy = py - cy;
        return (float)Math.Sqrt(dx * dx + dy * dy) - r;
    }

    private static float SdRoundRect(float px, float py, float cx, float cy, float hx, float hy, float r)
    {
        float dx = Math.Abs(px - cx) - (hx - r);
        float dy = Math.Abs(py - cy) - (hy - r);
        float ax = Math.Max(dx, 0f), ay = Math.Max(dy, 0f);
        return (float)Math.Sqrt(ax * ax + ay * ay) + Math.Min(Math.Max(dx, dy), 0f) - r;
    }

    // Inigo Quilez triangle SDF (closest-point projection, any orientation)
    private static float SdTriangle(float px, float py, float ax, float ay, float bx, float by, float cx, float cy)
    {
        float e0x = bx - ax, e0y = by - ay;
        float e1x = cx - bx, e1y = cy - by;
        float e2x = ax - cx, e2y = ay - cy;
        float v0x = px - ax, v0y = py - ay;
        float v1x = px - bx, v1y = py - by;
        float v2x = px - cx, v2y = py - cy;

        float l0 = e0x * e0x + e0y * e0y, l1 = e1x * e1x + e1y * e1y, l2 = e2x * e2x + e2y * e2y;
        float t0 = l0 == 0 ? 0 : Math.Clamp((v0x * e0x + v0y * e0y) / l0, 0f, 1f);
        float t1 = l1 == 0 ? 0 : Math.Clamp((v1x * e1x + v1y * e1y) / l1, 0f, 1f);
        float t2 = l2 == 0 ? 0 : Math.Clamp((v2x * e2x + v2y * e2y) / l2, 0f, 1f);

        float pq0x = v0x - e0x * t0, pq0y = v0y - e0y * t0;
        float pq1x = v1x - e1x * t1, pq1y = v1y - e1y * t1;
        float pq2x = v2x - e2x * t2, pq2y = v2y - e2y * t2;
        float d0 = pq0x * pq0x + pq0y * pq0y;
        float d1 = pq1x * pq1x + pq1y * pq1y;
        float d2 = pq2x * pq2x + pq2y * pq2y;

        float s = Math.Sign(e0x * e2y - e0y * e2x);
        float s0 = s * (v0x * e0y - v0y * e0x);
        float s1 = s * (v1x * e1y - v1y * e1x);
        float s2 = s * (v2x * e2y - v2y * e2x);
        float dx = Math.Min(d0, Math.Min(d1, d2));
        float sy = Math.Min(s0, Math.Min(s1, s2));
        return (float)(-Math.Sqrt(dx) * Math.Sign(sy));
    }

    // ---------------------------------------------------------- DIB (BMP) frame

    private static byte[] Dib(byte[] rgba, int s)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(40);                       // BITMAPINFOHEADER size
        bw.Write(s);                        // width
        bw.Write(s * 2);                    // height (XOR + AND)
        bw.Write((short)1);                 // planes
        bw.Write((short)32);                // bit count (alpha channel is honored on Vista+)
        bw.Write(0);                        // BI_RGB
        bw.Write(s * s * 4);                // image size
        bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
        for (int y = s - 1; y >= 0; y--)    // XOR bitmap, bottom-up, BGRA order
        {
            for (int x = 0; x < s; x++)
            {
                int i = (y * s + x) * 4;
                bw.Write(rgba[i + 2]);      // B
                bw.Write(rgba[i + 1]);      // G
                bw.Write(rgba[i]);          // R
                bw.Write(rgba[i + 3]);      // A
            }
        }
        bw.Write(new byte[((s + 31) / 32) * 4 * s]); // AND mask, all opaque
        return ms.ToArray();
    }

    // ---------------------------------------------------------- PNG frame (256)

    private static byte[] Png(byte[] rgba, int s)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, s); WriteBE(ihdr, 4, s);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(ms, "IHDR", ihdr);

        // raw scanlines with filter byte 0, wrapped in a zlib stream
        var raw = new byte[s * (s * 4 + 1)];
        int o = 0;
        for (int y = 0; y < s; y++)
        {
            raw[o++] = 0;
            Buffer.BlockCopy(rgba, y * s * 4, raw, o, s * 4);
            o += s * 4;
        }
        var z = new MemoryStream();
        using (var ds = new DeflateStream(z, CompressionLevel.Optimal, true))
            ds.Write(raw, 0, raw.Length);
        var idat = new MemoryStream();
        idat.WriteByte(0x78); idat.WriteByte(0x9C); // zlib header
        idat.Write(z.ToArray(), 0, (int)z.Length);
        var ad = Adler32(raw);
        byte[] be = new byte[4];
        WriteBE(be, 0, ad);
        idat.Write(be, 0, 4);
        WriteChunk(ms, "IDAT", idat.ToArray());
        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        byte[] t = Encoding.ASCII.GetBytes(type);
        byte[] head = new byte[4];
        WriteBE(head, 0, data.Length);
        s.Write(head, 0, 4);
        s.Write(t, 0, 4);
        s.Write(data, 0, data.Length);
        WriteBE(head, 0, Crc32(Crc32(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF);
        s.Write(head, 0, 4);
    }

    private static uint[]? _crcTable;
    private static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        _crcTable ??= BuildCrcTable();
        foreach (byte b in data)
            crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        return (b << 16) | a;
    }

    private static void WriteBE(byte[] buf, int at, uint v)
    {
        buf[at] = (byte)(v >> 24); buf[at + 1] = (byte)(v >> 16);
        buf[at + 2] = (byte)(v >> 8); buf[at + 3] = (byte)v;
    }

    private static void WriteBE(byte[] buf, int at, int v) => WriteBE(buf, at, (uint)v);

    // ------------------------------------------------------------- ICO container

    private static byte[] PackIco(List<(int size, byte[] data)> frames)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)frames.Count);

        int offset = 6 + 16 * frames.Count;
        foreach (var (size, data) in frames)
        {
            bw.Write((byte)(size == 256 ? 0 : size));
            bw.Write((byte)(size == 256 ? 0 : size));
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(data.Length); bw.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in frames)
            bw.Write(data);
        return ms.ToArray();
    }
}
