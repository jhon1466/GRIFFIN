using Griffin.Common.Table;

namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>
    /// Client tables of the Darkov / Es modes read without the (stale) typed table classes: <c>Resources/Tables/&lt;Name&gt;.bytes</c> is a header followed by one
    /// length-delimited row per entry; every row is read by field number (A-145).
    /// </summary>
    public static class RawTables
    {
        private static readonly Dictionary<string, List<PbMsg>> Cache = [];

        public static IReadOnlyList<PbMsg> Rows(string name)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(name, out var cached))
                    return cached;
                var rows = new List<PbMsg>();
                string path = Path.Combine(TableService.ResourceDir, "Tables", name + ".bytes");
                if (File.Exists(path))
                {
                    byte[] data = File.ReadAllBytes(path);
                    int i = 4 + (int)BitConverter.ToUInt32(data, 0);
                    while (i < data.Length)
                    {
                        ulong tag = PbMsg.ReadVarint(data, ref i);
                        switch (tag & 7)
                        {
                            case 0: PbMsg.ReadVarint(data, ref i); break;
                            case 1: i += 8; break;
                            case 5: i += 4; break;
                            case 2:
                            {
                                int length = (int)PbMsg.ReadVarint(data, ref i);
                                if (tag >> 3 == 1)
                                    rows.Add(PbMsg.Parse(data.AsSpan(i, length)));
                                i += length;
                                break;
                            }
                        }
                    }
                }

                return Cache[name] = rows;
            }
        }

        /// <summary>The rows indexed by a numeric key column (first column by default).</summary>
        public static IReadOnlyDictionary<ulong, PbMsg> ById(string name, int column = 1)
        {
            string key = name + "#" + column;
            lock (Cache)
            {
                if (ByIdCache.TryGetValue(key, out var cached))
                    return cached;
                var map = new Dictionary<ulong, PbMsg>();
                foreach (PbMsg row in Rows(name))
                    map[row.U(column)] = row;
                return ByIdCache[key] = map;
            }
        }

        private static readonly Dictionary<string, Dictionary<ulong, PbMsg>> ByIdCache = [];

        /// <summary>A table cell such as <c>29508,483,10919</c> (the coordinates of a depot) as numbers.</summary>
        public static long[] Numbers(string text) =>
            text.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries).Select(p => long.TryParse(p, out long n) ? n : 0).ToArray();
    }
}
