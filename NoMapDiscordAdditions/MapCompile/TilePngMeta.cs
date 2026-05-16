using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace NoMapDiscordAdditions.MapCompile
{
    /// <summary>
    /// Reads/writes a self-describing metadata block inside a tile PNG so a
    /// captured tile can be shared with another player and re-placed on their
    /// compile canvas at the exact same world position.
    ///
    /// The metadata is stored as a standard PNG <c>tEXt</c> chunk (keyword
    /// <see cref="Keyword"/>) holding a small JSON payload. tEXt is part of the
    /// PNG spec, so the file remains a perfectly valid image — Discord previews
    /// it, image editors open it, and decoders that don't care about text
    /// chunks ignore it. We inject/parse the chunk by raw byte manipulation
    /// (no encoder support needed) which keeps this independent of whichever
    /// path produced the PNG (ImageConversion or System.Drawing).
    /// </summary>
    public static class TilePngMeta
    {
        // PNG 8-byte signature.
        private static readonly byte[] Signature =
            { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        // Latin-1 keyword identifying our chunk. <=79 bytes, no spaces, per spec.
        private const string Keyword = "nmdaTile";

        // Bumped if the JSON shape changes incompatibly. Readers reject newer.
        public const int FormatVersion = 1;

        public class TileMeta
        {
            [JsonProperty("v")]   public int Version = FormatVersion;
            [JsonProperty("w")]   public long WorldUid;
            // World rect as [x, z] pairs (matches MapCompileTile.WorldMin/Max).
            [JsonProperty("min")] public float[] WorldMin;
            [JsonProperty("max")] public float[] WorldMax;
            [JsonProperty("pw")]  public int PixelWidth;
            [JsonProperty("ph")]  public int PixelHeight;
            [JsonProperty("src")] public string SourcePlayer;
            // true when absent (older shares) → treated as a complete tile.
            [JsonProperty("full")] public bool FullyMapped = true;
        }

        // ── Embed ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a copy of <paramref name="png"/> with our tEXt metadata chunk
        /// inserted just before IEND. Any pre-existing nmdaTile chunk is dropped
        /// first so re-sharing a previously-imported tile doesn't accumulate
        /// stale copies. Returns the original bytes unchanged on any failure.
        /// </summary>
        public static byte[] Embed(byte[] png, TileMeta meta)
        {
            if (png == null || png.Length < 8 || meta == null) return png;
            try
            {
                // EscapeNonAscii keeps the payload pure ASCII (player names can
                // contain accented chars) so it survives the Latin-1 tEXt chunk
                // and ASCII byte encoding without corruption.
                string json = JsonConvert.SerializeObject(meta,
                    new JsonSerializerSettings
                    {
                        StringEscapeHandling = StringEscapeHandling.EscapeNonAscii
                    });
                byte[] chunk = BuildTextChunk(Keyword, json);

                int iendStart = FindChunkStart(png, "IEND");
                if (iendStart < 0) return png;

                byte[] stripped = StripOurChunks(png);
                // FindChunkStart again on the stripped buffer (offsets shift).
                iendStart = FindChunkStart(stripped, "IEND");
                if (iendStart < 0) return png;

                var outBuf = new byte[stripped.Length + chunk.Length];
                Buffer.BlockCopy(stripped, 0, outBuf, 0, iendStart);
                Buffer.BlockCopy(chunk, 0, outBuf, iendStart, chunk.Length);
                Buffer.BlockCopy(stripped, iendStart, outBuf,
                    iendStart + chunk.Length, stripped.Length - iendStart);
                return outBuf;
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Tile metadata embed failed: {ex.Message}");
                return png;
            }
        }

        // ── Extract ──────────────────────────────────────────────────────────

        public static bool TryExtractFromFile(string path, out TileMeta meta)
        {
            meta = null;
            try
            {
                return TryExtract(File.ReadAllBytes(path), out meta);
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Read failed for {path}: {ex.Message}");
                return false;
            }
        }

        public static bool TryExtract(byte[] png, out TileMeta meta)
        {
            meta = null;
            if (png == null || png.Length < 8) return false;
            for (int i = 0; i < Signature.Length; i++)
                if (png[i] != Signature[i]) return false;

            try
            {
                int pos = 8;
                while (pos + 8 <= png.Length)
                {
                    int len = ReadBE32(png, pos);
                    string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                    int dataStart = pos + 8;
                    if (len < 0 || (long)dataStart + len + 4L > png.Length) break;

                    if (type == "tEXt")
                    {
                        // tEXt = keyword \0 text  (both Latin-1).
                        int sep = Array.IndexOf(png, (byte)0, dataStart, len);
                        if (sep > dataStart - 1)
                        {
                            string kw = Latin1(png, dataStart, sep - dataStart);
                            if (kw == Keyword)
                            {
                                string json = Latin1(png, sep + 1,
                                    dataStart + len - (sep + 1));
                                var m = JsonConvert.DeserializeObject<TileMeta>(json);
                                if (m != null && m.Version <= FormatVersion &&
                                    m.WorldMin != null && m.WorldMin.Length == 2 &&
                                    m.WorldMax != null && m.WorldMax.Length == 2)
                                {
                                    meta = m;
                                    return true;
                                }
                            }
                        }
                    }

                    if (type == "IEND") break;
                    pos = dataStart + len + 4; // skip data + CRC
                }
            }
            catch (Exception ex)
            {
                ModLog.Warn($"[NoMapDiscordAdditions] Tile metadata parse failed: {ex.Message}");
            }
            return false;
        }

        // ── Chunk plumbing ───────────────────────────────────────────────────

        private static byte[] BuildTextChunk(string keyword, string text)
        {
            byte[] kw = Encoding.ASCII.GetBytes(keyword);
            byte[] txt = Encoding.ASCII.GetBytes(text);
            byte[] data = new byte[kw.Length + 1 + txt.Length];
            Buffer.BlockCopy(kw, 0, data, 0, kw.Length);
            data[kw.Length] = 0;
            Buffer.BlockCopy(txt, 0, data, kw.Length + 1, txt.Length);

            byte[] typeBytes = Encoding.ASCII.GetBytes("tEXt");
            byte[] chunk = new byte[4 + 4 + data.Length + 4];
            WriteBE32(chunk, 0, data.Length);
            Buffer.BlockCopy(typeBytes, 0, chunk, 4, 4);
            Buffer.BlockCopy(data, 0, chunk, 8, data.Length);

            // CRC is over type + data.
            byte[] crcInput = new byte[4 + data.Length];
            Buffer.BlockCopy(typeBytes, 0, crcInput, 0, 4);
            Buffer.BlockCopy(data, 0, crcInput, 4, data.Length);
            WriteBE32(chunk, 8 + data.Length, (int)Crc32(crcInput));
            return chunk;
        }

        // Remove any existing nmdaTile tEXt chunks so Embed is idempotent.
        private static byte[] StripOurChunks(byte[] png)
        {
            var keep = new List<(int start, int end)>();
            int pos = 8;
            keep.Add((0, 8)); // signature
            while (pos + 8 <= png.Length)
            {
                int len = ReadBE32(png, pos);
                string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                int dataStart = pos + 8;
                if (len < 0 || dataStart + len + 4 > png.Length) break;
                int chunkEnd = dataStart + len + 4;

                bool drop = false;
                if (type == "tEXt")
                {
                    int sep = Array.IndexOf(png, (byte)0, dataStart, len);
                    if (sep >= dataStart &&
                        Latin1(png, dataStart, sep - dataStart) == Keyword)
                        drop = true;
                }
                if (!drop) keep.Add((pos, chunkEnd));

                if (type == "IEND") break;
                pos = chunkEnd;
            }

            int total = 0;
            foreach (var (s, e) in keep) total += e - s;
            var outBuf = new byte[total];
            int w = 0;
            foreach (var (s, e) in keep)
            {
                Buffer.BlockCopy(png, s, outBuf, w, e - s);
                w += e - s;
            }
            return outBuf;
        }

        private static int FindChunkStart(byte[] png, string type)
        {
            int pos = 8;
            while (pos + 8 <= png.Length)
            {
                int len = ReadBE32(png, pos);
                string t = Encoding.ASCII.GetString(png, pos + 4, 4);
                if (t == type) return pos;
                // Reject a corrupt/crafted length before it overflows the
                // cursor into a negative index (incoming share PNGs are
                // untrusted). 12 = 8-byte header + 4-byte CRC.
                if (len < 0 || (long)pos + 12L + len > png.Length) return -1;
                pos = pos + 12 + len;
            }
            return -1;
        }

        private static string Latin1(byte[] buf, int offset, int count)
        {
            if (count <= 0) return string.Empty;
            return Encoding.GetEncoding("ISO-8859-1").GetString(buf, offset, count);
        }

        private static int ReadBE32(byte[] b, int o) =>
            (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

        private static void WriteBE32(byte[] b, int o, int v)
        {
            b[o]     = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        // ── CRC-32 (PNG / zlib polynomial 0xEDB88320) ────────────────────────

        private static readonly uint[] _crcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        private static uint Crc32(byte[] data)
        {
            uint c = 0xFFFFFFFF;
            foreach (byte b in data)
                c = _crcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFF;
        }
    }
}
