using System;
using System.IO;
using System.IO.Compression;

namespace SizeMap.Harness
{
    // Zero-dependency PNG writer. System.Drawing.Common is Windows-only on .NET 8 AND a package;
    // ImageSharp is a package. PNG itself is signature + IHDR + one zlib'd IDAT + IEND, and the
    // BCL ships the zlib framing, so neither is worth a dependency.
    //
    // px entries are 0xAARRGGBB (Direct2D B8G8R8A8 read as a little-endian int). The raster is
    // blitted fully opaque — alpha is never data in SizeMap — so the alpha byte is dropped and
    // colour type 2 (truecolour RGB) is exact, not lossy.
    internal static class Png
    {
        public static void Write(string path, int[] px, int w, int h)
        {
            byte[] raw = new byte[h * (1 + w * 3)];
            int o = 0;
            for (int y = 0; y < h; y++)
            {
                raw[o++] = 0;                                   // per-row filter: None
                for (int x = 0; x < w; x++)
                {
                    int c = px[y * w + x];
                    raw[o++] = (byte)(c >> 16); raw[o++] = (byte)(c >> 8); raw[o++] = (byte)c;
                }
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var fs = File.Create(path))
            {
                fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
                byte[] ihdr = new byte[13];
                Be(ihdr, 0, w); Be(ihdr, 4, h);
                ihdr[8] = 8; ihdr[9] = 2;                       // 8 bits/channel, truecolour RGB
                Chunk(fs, "IHDR", ihdr);
                Chunk(fs, "IDAT", Zlib(raw));
                Chunk(fs, "IEND", new byte[0]);
            }
        }

        // RFC1950 by hand rather than ZLibStream: the repo's PostToolUse gate compiles every .cs
        // in this tree against NinjaTrader's .NET Framework 4.8 reference set, where ZLibStream
        // does not exist. Ten lines is cheaper than an exemption. 0x78 0x01 satisfies the
        // (CMF<<8|FLG) % 31 == 0 check; FLEVEL is advisory, so Optimal underneath is legal.
        static byte[] Zlib(byte[] d)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x01);
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(d, 0, d.Length);
                uint a = 1, b = 0;
                for (int i = 0; i < d.Length; i++) { a = (a + d[i]) % 65521; b = (b + a) % 65521; }
                uint ad = (b << 16) | a;
                ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16));
                ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
                return ms.ToArray();
            }
        }

        static void Chunk(Stream s, string type, byte[] body)
        {
            byte[] len = new byte[4]; Be(len, 0, body.Length); s.Write(len, 0, 4);
            byte[] td = { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
            s.Write(td, 0, 4); s.Write(body, 0, body.Length);
            uint c = 0xFFFFFFFF; c = Crc(c, td); c = Crc(c, body); c ^= 0xFFFFFFFF;
            byte[] cb = new byte[4]; Be(cb, 0, (int)c); s.Write(cb, 0, 4);
        }

        // Bitwise reflected CRC-32, no 1 KB table: this runs three times per file, not per pixel.
        static uint Crc(uint c, byte[] d)
        {
            for (int i = 0; i < d.Length; i++)
            {
                c ^= d[i];
                for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)(-(int)(c & 1)));
            }
            return c;
        }

        static void Be(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }
    }
}
