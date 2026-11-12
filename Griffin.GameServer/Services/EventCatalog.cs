using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>The events of ActivityListData as the GM tools and the /evento command list them.</summary>
    public static class EventCatalog
    {
        public sealed record Entry(uint Id, uint Type, string Name, string Icon, long OfficialOpen, long OfficialClose, long? Open, long? Close, string NameEn, string NameEs);

        /// <summary>English/Spanish names from the global client's tables (the CN client ships them empty), by event id.</summary>
        private static readonly Lazy<Dictionary<uint, Dictionary<string, string>>> Translated = new(() =>
        {
            using Stream? stream = typeof(EventCatalog).Assembly.GetManifestResourceStream("event-names.json");
            return stream is null ? [] : System.Text.Json.JsonSerializer.Deserialize<Dictionary<uint, Dictionary<string, string>>>(stream) ?? [];
        });

        private static string Translation(uint id, string language) =>
            Translated.Value.TryGetValue(id, out var names) ? names.GetValueOrDefault(language, string.Empty) : string.Empty;

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
            // official dates from the official file: the server may be reading the shifted copy (ShiftedTables)
            Dictionary<uint, (long Open, long Close)> official = Griffin.Common.Utils.ShiftedTables.OfficialActivityDates(
                Path.Combine(Griffin.Common.Utils.StaticConfig.ResourceDir, "Tables"));
            return tables.GetTable<ActivityListData>()
                .Where(r => TableFields.U(r, 1) != 0)
                .Select(r =>
                {
                    uint id = TableFields.U(r, 1);
                    opened.TryGetValue(id, out EventOverrideService.Window? w);
                    return new Entry(id, TableFields.U(r, 2), NameOf(tables, r), TableFields.S(r, 6),
                        official.GetValueOrDefault(id).Open, official.GetValueOrDefault(id).Close, w?.Open, w?.Close,
                        Translation(id, "en"), Translation(id, "es"));
                })
                .OrderBy(e => e.OfficialOpen).ThenBy(e => e.Id)
                .ToList();
        }
    }
}
