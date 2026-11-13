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

                        // every date of the row inside an opened period, moved with it
                        List<(int Field, int Pos, int Len, long Next, long NewOpen, bool Early)> dates = [];
                        Walk(file, start, end, (field, pos, len, value) =>
                        {
                            if (value < (1UL << 28) || value >= (1UL << 35))
                                return;
                            long v = (long)value;
                            if (own is not null && field == 4)
                                dates.Add((field, pos, len, own.NewOpen, own.NewOpen, false));
                            else if (own is not null && field == 5)
                                dates.Add((field, pos, len, own.NewClose, own.NewOpen, false));
                            else
                            {
                                (long From, long To, long Delta) hit = windows.FirstOrDefault(w => v >= w.From && v <= w.To);
                                if (hit.Delta == 0 && hit.From == 0)
                                    return;
                                long newOpen = hit.From + Margin + hit.Delta;
                                dates.Add((field, pos, len, v + hit.Delta, newOpen, v < (hit.From + hit.To) / 2));
                            }
                        });

                        // The parts of a reopened event (its shop, challenge, daily food, minigames, later chapters...) opened days after
                        // the event officially: they open now as well, keeping their closing date (A-212). The opening date is the earliest
                        // one of the row (or ActivityListData column 4), or a lone date of the first half of the period.
                        List<(int Field, int Pos, int Len, long Next, long NewOpen, bool Early)> moved = dates.Where(d => own is null).ToList();
                        int opening = -1;
                        if (activityList)
                            opening = moved.FindIndex(d => d.Field == 4);
                        else if (moved.Count >= 2)
                            opening = moved.IndexOf(moved.MinBy(d => d.Next));
                        else if (moved.Count == 1 && moved[0].Early)
                            opening = 0;
                        foreach (var d in dates)
                        {
                            long next = d.Next;
                            if (own is null && opening >= 0 && moved[opening].Pos == d.Pos && next > d.NewOpen)
                                next = d.NewOpen;
                            if (next < (1L << 28) || next >= (1L << 35))
                                continue;
                            WriteVarint(file, d.Pos, d.Len, (ulong)next);
                        }
                    }

                    if (name.Equals("ActivityModuleData.bytes", StringComparison.OrdinalIgnoreCase))
                        PatchTpsShootSeasons(officialDir, file);
                    // TpsShoot stages open N minutes after the event (TpsshootMissionData column 8, client AGGHEDJPEFL): a reopened event opens them
                    // all at once, like its other parts (A-214). Zero is written as a padded varint of the same length (valid protobuf).
                    if (name.Equals("TpsshootMissionData.bytes", StringComparison.OrdinalIgnoreCase))
                        foreach ((int start, int end) in Rows(file))
                            Walk(file, start, end, (field, pos, len, value) =>
                            {
                                if (field == 8 && value != 0)
                                {
                                    for (int k = 0; k < len - 1; k++)
                                        file[pos + k] = 0x80;
                                    file[pos + len - 1] = 0;
                                }
                            });
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

        /// <summary>
        /// Old TpsShoot events (theme 901/1401/1801/3001 -> TpsshootBaseData 101..104) cannot open in client 4.0.5142: when its info arrives the
        /// client loads the skills of the season (TpsMissionSkillSeasonGroupBySeasonIdData, keyed by the same config id), and that table only
        /// has season 105, so it throws and the stage panel stays empty (TEXTTEXT). In the copy of the tables of a reopened event, every
        /// ActivityModuleData entry module 21001 -> config without a skill season points to the newest config that has one, so the mode opens
        /// with the stages and skills the client knows. Same varint length only, so the layout is unchanged (A-214).
        /// </summary>
        private static void PatchTpsShootSeasons(string officialDir, byte[] file)
        {
            const ulong TpsShootModule = 21001;
            string seasonsPath = Path.Combine(officialDir, "TpsMissionSkillSeasonGroupBySeasonIdData.bytes");
            if (!File.Exists(seasonsPath))
                return;
            byte[] seasonsFile = File.ReadAllBytes(seasonsPath);
            HashSet<ulong> seasons = [];
            foreach ((int start, int end) in Rows(seasonsFile))
                Walk(seasonsFile, start, end, (field, pos, len, value) => { if (field == 1) seasons.Add(value); });
            if (seasons.Count == 0)
                return;
            ulong newest = seasons.Max();

            foreach ((int start, int end) in Rows(file))
            {
                int i = start;
                while (i < end)
                {
                    if (!TryVarint(file, ref i, out ulong tag))
                        break;
                    int wire = (int)(tag & 7);
                    if (wire == 0) { if (!TryVarint(file, ref i, out _)) break; continue; }
                    if (wire == 1) { i += 8; continue; }
                    if (wire == 5) { i += 4; continue; }
                    if (wire != 2 || !TryVarint(file, ref i, out ulong length))
                        break;
                    int entryEnd = i + (int)length;
                    if ((tag >> 3) == 4)
                    {
                        ulong key = 0;
                        int valuePos = -1, valueLen = 0;
                        ulong value = 0;
                        Walk(file, i, entryEnd, (field, pos, len, v) =>
                        {
                            if (field == 1) key = v;
                            else if (field == 2) { value = v; valuePos = pos; valueLen = len; }
                        });
                        if (key == TpsShootModule && valuePos >= 0 && !seasons.Contains(value))
                        {
                            WriteVarint(file, valuePos, valueLen, newest);
                        }
                    }
                    i = entryEnd;
                }
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
