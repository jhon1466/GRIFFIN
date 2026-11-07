using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>The events of ActivityListData as the GM tools and the /evento command list them.</summary>
    public static class EventCatalog
    {
        public sealed record Entry(uint Id, uint Type, string Name, string Icon, long OfficialOpen, long OfficialClose, long? Open, long? Close);

        private static Dictionary<long, string>? names;
        private static readonly object Gate = new();

        /// <summary>Names of the column 3 text ids (LangPackageTableCnData), read once.</summary>
        private static string NameOf(ITableService tables, Google.Protobuf.IMessage row)
        {
            long textId = ((ActivityListData)row).Name?.Id ?? 0;
            lock (Gate)
            {
                if (names is null)
                {
                    HashSet<long> wanted = tables.GetTable<ActivityListData>()
                        .Select(r => r.Name?.Id ?? 0L).Where(i => i != 0).ToHashSet();
                    names = tables.GetTable<LangPackageTableCnData>().Where(r => wanted.Contains(r.Id))
                        .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Content);
                }

                return names.GetValueOrDefault(textId, string.Empty);
            }
        }

        public static List<Entry> All(ITableService tables, IEventOverrideService overrides)
        {
            IReadOnlyDictionary<uint, EventOverrideService.Window> opened = overrides.All();
            return tables.GetTable<ActivityListData>()
                .Where(r => TableFields.U(r, 1) != 0)
                .Select(r =>
                {
                    uint id = TableFields.U(r, 1);
                    opened.TryGetValue(id, out EventOverrideService.Window? w);
                    return new Entry(id, TableFields.U(r, 2), NameOf(tables, r), TableFields.S(r, 6),
                        (long)TableFields.U64(r, 4), (long)TableFields.U64(r, 5), w?.Open, w?.Close);
                })
                .OrderBy(e => e.OfficialOpen).ThenBy(e => e.Id)
                .ToList();
        }
    }
}
