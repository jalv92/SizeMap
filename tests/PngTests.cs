using System;
using System.IO;
using System.IO.Compression;
using SizeMap.Harness;
using Xunit;

namespace SizeMap.Tests
{
    // A hand-rolled PNG that "mostly works" is the worst kind of broken: an image viewer that
    // repairs a bad CRC or a short IDAT hides the bug until the day something stricter reads it.
    // So this decodes the file the pedantic way — every chunk CRC, the zlib Adler-32, and the
    // inflated scanlines byte-for-byte — rather than asserting the file is non-empty.
    public class PngTests
    {
        [Fact]
        public void Png_RoundTripsExactPixels_AndEveryChecksumHolds()
        {
            const int W = 5, H = 3;
            int[] px = new int[W * H];
            for (int i = 0; i < px.Length; i++)
                px[i] = unchecked((int)(0xFF000000u | (uint)(i * 0x0F1E2D)));   // spread all three channels

            string path = Path.Combine(Path.GetTempPath(), "sizemap-png-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Png.Write(path, px, W, H);
                byte[] f = File.ReadAllBytes(path);

                Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, Sub(f, 0, 8));

                byte[] ihdr = null, idat = null;
                bool sawEnd = false;
                int p = 8;
                while (p < f.Length)
                {
                    int len = Be(f, p);
                    string type = System.Text.Encoding.ASCII.GetString(f, p + 4, 4);
                    Assert.Equal(Crc(f, p + 4, 4 + len), (uint)Be(f, p + 8 + len));   // type+data, per spec
                    if (type == "IHDR") ihdr = Sub(f, p + 8, len);
                    else if (type == "IDAT") idat = Sub(f, p + 8, len);
                    else if (type == "IEND") { sawEnd = true; Assert.Equal(0, len); }
                    p += 12 + len;
                }
                Assert.Equal(f.Length, p);            // no trailing bytes
                Assert.True(sawEnd);

                Assert.Equal(13, ihdr.Length);
                Assert.Equal(W, Be(ihdr, 0));
                Assert.Equal(H, Be(ihdr, 4));
                Assert.Equal(8, ihdr[8]);             // bit depth
                Assert.Equal(2, ihdr[9]);             // colour type 2 = truecolour RGB
                Assert.Equal(0, ihdr[10]);            // deflate
                Assert.Equal(0, ihdr[11]);            // adaptive filtering
                Assert.Equal(0, ihdr[12]);            // no interlace

                Assert.Equal(0x78, idat[0]);
                Assert.Equal(0, (idat[0] * 256 + idat[1]) % 31);   // the FCHECK the spec requires

                byte[] raw;
                using (var ms = new MemoryStream(idat, 2, idat.Length - 6))
                using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                using (var outp = new MemoryStream())
                {
                    ds.CopyTo(outp);
                    raw = outp.ToArray();
                }

                byte[] want = new byte[H * (1 + W * 3)];
                int o = 0;
                for (int y = 0; y < H; y++)
                {
                    want[o++] = 0;
                    for (int x = 0; x < W; x++)
                    {
                        int c = px[y * W + x];
                        want[o++] = (byte)(c >> 16); want[o++] = (byte)(c >> 8); want[o++] = (byte)c;
                    }
                }
                Assert.Equal(want, raw);              // channel order AND the alpha byte being dropped

                uint a = 1, b = 0;
                for (int i = 0; i < raw.Length; i++) { a = (a + raw[i]) % 65521; b = (b + a) % 65521; }
                Assert.Equal((b << 16) | a, (uint)Be(idat, idat.Length - 4));
            }
            finally { File.Delete(path); }
        }

        static byte[] Sub(byte[] s, int off, int len)
        {
            byte[] d = new byte[len];
            Buffer.BlockCopy(s, off, d, 0, len);
            return d;
        }

        static int Be(byte[] b, int o)
        {
            return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
        }

        static uint Crc(byte[] d, int off, int len)
        {
            uint c = 0xFFFFFFFF;
            for (int i = off; i < off + len; i++)
            {
                c ^= d[i];
                for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)(-(int)(c & 1)));
            }
            return c ^ 0xFFFFFFFF;
        }
    }
}
