using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Griffin.Common.Utils
{
    /// <summary>
    /// Old events played again. The client decides by itself whether an event, its stages, its shop, its banners or its tickets are
    /// open: it compares the dates written in its tables (bk_stc_pb.zip, downloaded from the CDN that Griffin Patcher sends to this
    /// server) with the server clock. To reopen an event the server therefore hands the client its own copy of the tables in which
    /// every date that falls inside the official period of the event (ActivityListData columns 4/5, a few days of margin) is moved
    /// forward by the same amount, so the event and everything of its period run again from today with their real durations and
    /// rules. The server reads the same copy, so both sides agree.
    /// <para>The tables keep their exact byte layout: a date is a 5-byte varint both before and after the shift (any value between
    /// 2^28 and 2^35), so the index at the head of each file (row offsets for lazy loading) stays valid. A value whose length would
    /// change is left alone. Only the top-level fields of each row are touched.</para>
    /// </summary>
    public static class ShiftedTables
    {
        public sealed record Shift(uint ActivityId, long NewOpen, long NewClose);

        private const long Margin = 3 * 86400;
        private const uint RowTag = 10;
        private static readonly object Gate = new();

        /// <summary>Version announced to the client while some event is shifted (null = the official tables).</summary>
        public static string? Version { get; private set; }

        public static string Dir => Path.Combine(StaticConfig.ResourceDir, "TablesShifted");

        public static string ZipPath => Path.Combine(StaticConfig.ResourceDir, "bk_stc_pb_shifted.zip");

        public static bool Active => Version is not null;

        /// <summary>Official dates (columns 4 and 5) of every ActivityListData row, read from the official table file.</summary>
        public static Dictionary<uint, (long Open, long Close)> OfficialActivityDates(string officialDir)
        {
            Dictionary<uint, (long, long)> result = [];
            byte[] file = File.ReadAllBytes(Path.Combine(officialDir, "ActivityListData.bytes"));
            foreach ((int start, int end) in Rows(file))
            {
                uint id = 0;
                long open = 0, close = 0;
                Walk(file, start, end, (field, pos, len, value) =>
                {
                    if (field == 1) id = (uint)value;
                    else if (field == 4) open = (long)value;
                    else if (field == 5) close = (long)value;
                });
                if (id != 0)
                    result[id] = (open, close);
            }

            return result;
        }

        /// <summary>Builds the shifted copy for the given events (none = back to the official tables).</summary>
        public static void Rebuild(string officialDir, string officialVersion, IReadOnlyCollection<Shift> shifts)
        {
            lock (Gate)
            {
                if (shifts.Count == 0)
                {
                    Version = null;
                    return;
                }

                Dictionary<uint, (long Open, long Close)> official = OfficialActivityDates(officialDir);
                List<(long From, long To, long Delta)> windows = [];
                Dictionary<uint, Shift> byId = [];
                foreach (Shift s in shifts)
                {
                    if (!official.TryGetValue(s.ActivityId, out var w) || w.Open < (1L << 28))
                        continue;
                    byId[s.ActivityId] = s;
                    windows.Add((w.Open - Margin, (w.Close > w.Open ? w.Close : w.Open) + Margin, s.NewOpen - w.Open));
                }

                string signature = string.Join(';', shifts.OrderBy(s => s.ActivityId).Select(s => $"{s.ActivityId}:{s.NewOpen}:{s.NewClose}"));
                uint hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(officialVersion + "|" + signature)), 0);
                string version = $"{officialVersion}{hash % 900 + 100}";

                string tmp = Dir + ".tmp";
                if (Directory.Exists(tmp))
                    Directory.Delete(tmp, recursive: true);
                Directory.CreateDirectory(tmp);

                foreach (string path in Directory.EnumerateFiles(officialDir))
                {
                    string name = Path.GetFileName(path);
                    if (!name.EndsWith(".bytes", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(path, Path.Combine(tmp, name));
                        continue;
                    }

                    byte[] file = File.ReadAllBytes(path);
                    bool activityList = name.Equals("ActivityListData.bytes", StringComparison.OrdinalIgnoreCase);
                    foreach ((int start, int end) in Rows(file))
                    {
                        Shift? own = null;
                        if (activityList)
                        {
                            uint id = 0;
                            Walk(file, start, end, (field, pos, len, value) => { if (field == 1) id = (uint)value; });
                            byId.TryGetValue(id, out own);
                        }

                        Walk(file, start, end, (field, pos, len, value) =>
                        {
                            if (value < (1UL << 28) || value >= (1UL << 35))
                                return;
                            long v = (long)value;
                            long next;
                            if (own is not null && field == 4)
                                next = own.NewOpen;
                            else if (own is not null && field == 5)
                                next = own.NewClose;
                            else
                            {
                                (long From, long To, long Delta) hit = windows.FirstOrDefault(w => v >= w.From && v <= w.To);
                                if (hit.Delta == 0 && hit.From == 0)
                                    return;
                                next = v + hit.Delta;
                            }

                            if (next < (1L << 28) || next >= (1L << 35))
                                return;
                            WriteVarint(file, pos, len, (ulong)next);
                        });
                    }

                    File.WriteAllBytes(Path.Combine(tmp, name), file);
                }

                File.WriteAllText(Path.Combine(tmp, "version.txt"), version);
                if (Directory.Exists(Dir))
                    Directory.Delete(Dir, recursive: true);
                Directory.Move(tmp, Dir);

                string zipTmp = ZipPath + ".tmp";
                if (File.Exists(zipTmp))
                    File.Delete(zipTmp);
                ZipFile.CreateFromDirectory(Dir, zipTmp, CompressionLevel.Optimal, includeBaseDirectory: false);
                File.Move(zipTmp, ZipPath, overwrite: true);
                Version = version;
            }
        }

        /// <summary>Byte ranges of the rows (field 1 of the wrapper that follows the header).</summary>
        private static IEnumerable<(int Start, int End)> Rows(byte[] file)
        {
            if (file.Length < 4)
                yield break;
            int i = 4 + (int)BitConverter.ToUInt32(file, 0);
            while (i < file.Length)
            {
                if (!TryVarint(file, ref i, out ulong tag))
                    yield break;
                switch ((int)(tag & 7))
                {
                    case 0:
                        if (!TryVarint(file, ref i, out _)) yield break;
                        break;
                    case 1:
                        i += 8;
                        break;
                    case 5:
                        i += 4;
                        break;
                    case 2:
                        if (!TryVarint(file, ref i, out ulong len) || i + (long)len > file.Length) yield break;
                        if (tag == RowTag)
                            yield return (i, i + (int)len);
                        i += (int)len;
                        break;
                    default:
                        yield break;
                }
            }
        }

        private delegate void VarintField(int field, int pos, int len, ulong value);

        private static void Walk(byte[] b, int i, int end, VarintField onVarint)
        {
            while (i < end)
            {
                if (!TryVarint(b, ref i, out ulong tag))
                    return;
                int field = (int)(tag >> 3);
                switch ((int)(tag & 7))
                {
                    case 0:
                        int pos = i;
                        if (!TryVarint(b, ref i, out ulong value))
                            return;
                        onVarint(field, pos, i - pos, value);
                        break;
                    case 1:
                        i += 8;
                        break;
                    case 5:
                        i += 4;
                        break;
                    case 2:
                        if (!TryVarint(b, ref i, out ulong len))
                            return;
                        i += (int)len;
                        break;
                    default:
                        return;
                }
            }
        }

        private static bool TryVarint(byte[] b, ref int i, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift < 64 && i < b.Length; shift += 7)
            {
                byte c = b[i++];
                value |= (ulong)(c & 0x7F) << shift;
                if ((c & 0x80) == 0)
                    return true;
            }

            return false;
        }

        /// <summary>Writes the value over the old one only when it takes exactly the same number of bytes.</summary>
        private static void WriteVarint(byte[] b, int pos, int len, ulong value)
        {
            Span<byte> tmp = stackalloc byte[10];
            int n = 0;
            ulong v = value;
            do
            {
                byte c = (byte)(v & 0x7F);
                v >>= 7;
                tmp[n++] = v != 0 ? (byte)(c | 0x80) : c;
            } while (v != 0);

            if (n == len)
                tmp[..n].CopyTo(b.AsSpan(pos, len));
        }
    }
}
