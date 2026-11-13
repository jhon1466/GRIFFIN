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
        /// <summary>Bump when the fixes applied to the served copy change (A-214: TpsShoot seasons and stage delays).</summary>
        private const string PatchRevision = "4";

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
                // PatchRevision changes whenever the served tables get a new fix, so a client that cached the previous copy downloads it again
                uint hash = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(officialVersion + "|" + signature + "|" + PatchRevision + "|" + (File.Exists(HiddenPath) ? File.ReadAllText(HiddenPath) : ""))), 0);
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
                        List<(int Field, int Pos, int Len, long Value)> later = [];
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
                                {
                                    later.Add((field, pos, len, v));
                                    return;
                                }
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
                        // A date of the same row past the opened period (a part that officially closed weeks after the event, e.g. a
                        // PlanData close in a later month) stayed in the past and the part showed closed: it moves with the row (A-216).
                        if (own is null && moved.Count > 0)
                        {
                            long delta = moved[0].NewOpen - (windows.First(w => moved[0].Next - w.Delta >= w.From && moved[0].Next - w.Delta <= w.To).From + Margin);
                            long rowStart = moved.Min(d => d.Next) - delta;
                            foreach (var l in later.Where(l => l.Value > rowStart))
                            {
                                long next = l.Value + delta;
                                if (next >= (1L << 28) && next < (1L << 35))
                                    WriteVarint(file, l.Pos, l.Len, (ulong)next);
                            }
                        }
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
                    {
                        PatchTpsShootSeasons(officialDir, file);
                        PatchRichManConfigs(officialDir, file);
                        HideSubmodules(file);
                    }
                    // RichMan stages open N minutes after the event (MissionData column 5): a reopened event opens them all at once (A-215)
                    if (name.Equals("MissionData.bytes", StringComparison.OrdinalIgnoreCase))
                        ZeroField(file, 5, RichManMissions(officialDir));
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

        /// <summary>
        /// RichMan (开拓之路, module 21003) of theme 901 points to RichmanConfigData 1001, the only one of its series without column 7 (board data):
        /// the client opens the stage list but the stage panel stays "TEXTTITLE" and its manager throws. In the served copy such entries point to
        /// the newest 1xxx config that has column 7 (1004), so the board can be played (A-215).
        /// </summary>
        private static void PatchRichManConfigs(string officialDir, byte[] file)
        {
            string path = Path.Combine(officialDir, "RichmanConfigData.bytes");
            if (!File.Exists(path))
                return;
            byte[] configs = File.ReadAllBytes(path);
            HashSet<ulong> all = [], complete = [];
            foreach ((int start, int end) in Rows(configs))
            {
                ulong id = 0;
                bool board = false;
                Walk(configs, start, end, (field, pos, len, value) => { if (field == 1) id = value; if (field == 7) board = true; });
                all.Add(id);
                if (board)
                    complete.Add(id);
            }
            List<ulong> series = complete.Where(id => id is >= 1000 and < 2000).ToList();
            if (series.Count == 0)
                return;
            ulong target = series.Max();
            PatchModuleValues(file, 21003, value => all.Contains(value) && !complete.Contains(value) && value is >= 1000 and < 2000, target);
        }

        private static string HiddenPath => Path.Combine(StaticConfig.ResourceDir, "hidden-submodules.json");

        /// <summary>
        /// Submodules hidden from a theme in the served copy (Resources/hidden-submodules.json: {"2201101": [24001, ...]}): their key in
        /// ActivityModuleData column 4 becomes an unused id of the same varint length, so the theme main panel skips binding them. Used to
        /// get past theme panels of old events whose prefab in client 4.0.5142 lacks a button and freezes on open (A-217).
        /// </summary>
        private static void HideSubmodules(byte[] file)
        {
            Dictionary<ulong, HashSet<ulong>> hidden;
            try
            {
                hidden = File.Exists(HiddenPath)
                    ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<ulong, HashSet<ulong>>>(File.ReadAllText(HiddenPath)) ?? []
                    : [];
            }
            catch (Exception) { return; }
            if (hidden.Count == 0)
                return;
            foreach ((int start, int end) in Rows(file))
            {
                ulong id = 0;
                Walk(file, start, end, (f, pos, len, value) => { if (f == 1) id = value; });
                if (!hidden.TryGetValue(id, out HashSet<ulong>? keys))
                    continue;
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
                    if ((tag >> 3) is 4 or 10)
                        Walk(file, i, entryEnd, (field, pos, len, v) =>
                        {
                            if (field == 1 && keys.Contains(v))
                                WriteVarint(file, pos, len, len == 3 ? 2_000_000UL : len == 2 ? 16_000UL : 120UL);
                        });
                    i = entryEnd;
                }
            }
        }

        /// <summary>MissionData ids listed by MissionBySeasonData (the RichMan stages).</summary>
        private static HashSet<ulong> RichManMissions(string officialDir)
        {
            HashSet<ulong> ids = [];
            string path = Path.Combine(officialDir, "MissionBySeasonData.bytes");
            if (!File.Exists(path))
                return ids;
            byte[] seasons = File.ReadAllBytes(path);
            foreach ((int start, int end) in Rows(seasons))
            {
                int i = start;
                while (i < end)
                {
                    if (!TryVarint(seasons, ref i, out ulong tag))
                        break;
                    int wire = (int)(tag & 7);
                    if (wire == 0) { if (!TryVarint(seasons, ref i, out _)) break; continue; }
                    if (wire == 1) { i += 8; continue; }
                    if (wire == 5) { i += 4; continue; }
                    if (wire != 2 || !TryVarint(seasons, ref i, out ulong length))
                        break;
                    int stop = i + (int)length;
                    if ((tag >> 3) == 2)
                        while (i < stop && TryVarint(seasons, ref i, out ulong id))
                            ids.Add(id);
                    i = stop;
                }
            }
            return ids;
        }

        /// <summary>Writes 0 (padded varint, same length) into top-level <paramref name="field"/> of the rows whose id is in <paramref name="rows"/>.</summary>
        private static void ZeroField(byte[] file, int field, HashSet<ulong> rows)
        {
            if (rows.Count == 0)
                return;
            foreach ((int start, int end) in Rows(file))
            {
                ulong id = 0;
                Walk(file, start, end, (f, pos, len, value) => { if (f == 1) id = value; });
                if (!rows.Contains(id))
                    continue;
                Walk(file, start, end, (f, pos, len, value) =>
                {
                    if (f != field || value == 0)
                        return;
                    for (int k = 0; k < len - 1; k++)
                        file[pos + k] = 0x80;
                    file[pos + len - 1] = 0;
                });
            }
        }

        /// <summary>In every ActivityModuleData row, column 4 entries for <paramref name="module"/> whose value matches get <paramref name="target"/>.</summary>
        private static void PatchModuleValues(byte[] file, ulong module, Func<ulong, bool> replace, ulong target)
        {
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
                        ulong key = 0, value = 0;
                        int valuePos = -1, valueLen = 0;
                        Walk(file, i, entryEnd, (field, pos, len, v) =>
                        {
                            if (field == 1) key = v;
                            else if (field == 2) { value = v; valuePos = pos; valueLen = len; }
                        });
                        if (key == module && valuePos >= 0 && replace(value))
                            WriteVarint(file, valuePos, valueLen, target);
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
