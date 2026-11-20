using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The plugins (显像 chips of the remoulding core) of each account (A-231). Every account starts with the fixed list of
    /// <see cref="Handlers.GunHandler.PluginCatalog"/> (so the plugins already equipped stay valid); on top of it the account keeps the plugins it gained
    /// (an instance item 98xxxx granted by GM Tools, a reward or a command becomes a real plugin, announced with SC_AddGunRemouldingPlugin), the
    /// plugins it dismantled (PluginData column 6 pays "49:n" Data Links) and the locks it set.
    /// </summary>
    public static class PluginInventory
    {
        private const string Key = "remoulding.plugins.inventory.v1";

        public sealed class Owned
        {
            public ulong Id { get; set; }
            public uint Stc { get; set; }
            public uint Row { get; set; }
            public uint[] Entries { get; set; } = [];
        }

        public sealed class State
        {
            public List<Owned> Gained { get; set; } = [];
            public HashSet<ulong> Removed { get; set; } = [];
            public HashSet<ulong> Locked { get; set; } = [];
        }

        private static readonly object Gate = new();
        private static long nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() << 12;

        public static State Load(IAccountStateService accountState, uint uid) => accountState.Get<State>(uid, Key) ?? new State();

        public static void Save(IAccountStateService accountState, uint uid, State state) => accountState.Set(uid, Key, state);

        /// <summary>The PluginData rows that a plugin instance item stands for (98 + grade + class + variant).</summary>
        private static Dictionary<uint, List<PluginData>>? rowsByItem;

        public static IReadOnlyDictionary<uint, List<PluginData>> RowsByItem(ITableService tables)
        {
            if (rowsByItem is not null)
                return rowsByItem;
            HashSet<uint> items = tables.GetTable<ItemData>().Select(r => r.Id).ToHashSet();
            Dictionary<uint, List<PluginData>> map = [];
            foreach (PluginData row in tables.GetTable<PluginData>())
            {
                uint baseId = 980_000u + TableFields.U(row, 4) * 1000 + TableFields.U(row, 3) * 100;
                foreach (uint item in new[] { baseId + 1, baseId + 2 }.Where(items.Contains))
                {
                    if (!map.TryGetValue(item, out List<PluginData>? list))
                        map[item] = list = [];
                    list.Add(row);
                }
            }
            return rowsByItem = map;
        }

        public static bool IsPluginItem(ITableService tables, uint item) => RowsByItem(tables).ContainsKey(item);

        /// <summary>The effect entries of a PluginData row (columns 7-9 -> EntryLibraryData column 5 -> FeaturesEntryData), as the catalog draws them.</summary>
        private static uint[] EntriesOf(ITableService tables, PluginData row)
        {
            var library = tables.GetTable<EntryLibraryData>().ToDictionary(r => r.Id, r => TableFields.U(r, 5));
            var features = tables.GetTable<FeaturesEntryData>().Select(r => r.Id).ToHashSet();
            return new[] { 7, 8, 9 }.Select(c => TableFields.U(row, c)).Where(id => id != 0 && library.ContainsKey(id))
                .Select(id => library[id]).Where(features.Contains).ToArray();
        }

        /// <summary>
        /// The effects of a new chip, as the official chips have them (the 50+ chips of the captured catalog): item 98 G C 0V (G = grade, C = class)
        /// has one main effect of its class at level G - 2 (FeaturesEntryData id 1 C xxx LL) and one secondary effect of any class at level 1
        /// (2 x xxx 01), two from grade 5 on. The PluginData rows of most items are placeholders without effects.
        /// </summary>
        public static uint[] RollEntries(ITableService tables, uint item)
        {
            uint grade = item / 1000 % 10, cls = item / 100 % 10;
            List<uint> ids = tables.GetTable<FeaturesEntryData>().Select(r => r.Id).ToList();
            List<uint> mains = ids.Where(id => id / 1_000_000 == 1 && id / 100_000 % 10 == cls && id % 100 == Math.Max(1, grade - 2)).ToList();
            if (mains.Count == 0)
                mains = ids.Where(id => id / 1_000_000 == 1 && id / 100_000 % 10 == cls).ToList();
            List<uint> seconds = ids.Where(id => id / 1_000_000 == 2 && id % 100 == 1).ToList();
            List<uint> entries = [];
            if (mains.Count > 0)
                entries.Add(mains[Random.Shared.Next(mains.Count)]);
            for (int n = grade >= 5 ? 2 : 1; n > 0 && seconds.Count > 0; n--)
            {
                uint pick = seconds[Random.Shared.Next(seconds.Count)];
                seconds.RemoveAll(id => id / 100 == pick / 100);
                entries.Add(pick);
            }
            return entries.ToArray();
        }

        /// <summary>The account's plugins: the fixed list without the dismantled ones, plus the gained ones, with their locks.</summary>
        public static SC_GunRemouldingPlugins Catalog(ITableService tables, IAccountStateService accountState, uint uid)
        {
            State state = Load(accountState, uid);
            SC_GunRemouldingPlugins all = Handlers.GunHandler.PluginCatalog(tables);
            SC_GunRemouldingPlugins mine = new();
            foreach (GunRemouldingPlugin plugin in all.RemouldingPlugins.Where(p => !state.Removed.Contains(p.Id)))
                mine.RemouldingPlugins.Add(plugin);
            foreach (Owned owned in state.Gained)
                mine.RemouldingPlugins.Add(ToProto(owned));
            foreach (GunRemouldingPlugin plugin in mine.RemouldingPlugins)
                plugin.MDFIDAFPBJN = state.Locked.Contains(plugin.Id);
            return mine;
        }

        public static GunRemouldingPlugin ToProto(Owned owned)
        {
            GunRemouldingPlugin plugin = new() { Id = owned.Id, StcId = owned.Stc };
            plugin.ACEIFDEGLMH.AddRange(owned.Entries);
            return plugin;
        }

        /// <summary>Creates <paramref name="count"/> plugins of an instance item (a PluginData row of that item, drawn at random) and announces them.</summary>
        public static IReadOnlyList<ulong> Create(ITableService tables, IAccountStateService accountState, uint uid, uint item, int count, List<IMessage> messages)
        {
            if (!RowsByItem(tables).TryGetValue(item, out List<PluginData>? rows))
                return [];
            List<PluginData> usable = rows.Where(r => EntriesOf(tables, r).Length > 0).ToList();
            if (usable.Count == 0)
                usable = rows;
            List<ulong> ids = [];
            lock (Gate)
            {
                State state = Load(accountState, uid);
                for (int i = 0; i < count; i++)
                {
                    PluginData row = usable[Random.Shared.Next(usable.Count)];
                    Owned owned = new() { Id = (ulong)Interlocked.Increment(ref nextId), Stc = item, Row = row.Id, Entries = RollEntries(tables, item) };
                    state.Gained.Add(owned);
                    ids.Add(owned.Id);
                    messages.Add(new SC_AddGunRemouldingPlugin { Plugin = ToProto(owned) });
                }
                Save(accountState, uid, state);
            }
            return ids;
        }

        /// <summary>The PluginData row of a plugin of the account (gained: stored; generated: id - base; captured: the first row of its item).</summary>
        public static PluginData? RowOf(ITableService tables, State state, GunRemouldingPlugin plugin)
        {
            if (state.Gained.FirstOrDefault(o => o.Id == plugin.Id) is { } owned)
                return tables.GetTable<PluginData>().FirstOrDefault(r => r.Id == owned.Row);
            const ulong Generated = 1_800_000_000_000_000_000UL;
            if (plugin.Id > Generated && plugin.Id - Generated < uint.MaxValue)
            {
                uint id = (uint)(plugin.Id - Generated);
                if (tables.GetTable<PluginData>().FirstOrDefault(r => r.Id == id) is { } generated)
                    return generated;
            }
            return RowsByItem(tables).TryGetValue(plugin.StcId, out List<PluginData>? rows) ? rows.FirstOrDefault() : null;
        }

        /// <summary>The reward of dismantling a plugin: PluginData column 6 ("item:count,...").</summary>
        public static IEnumerable<(uint Item, int Count)> DismantleReward(PluginData? row)
        {
            if (row is null)
                yield break;
            foreach (string part in TableFields.S(row, 6).Trim('\'').Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = part.Split(':');
                if (kv.Length == 2 && uint.TryParse(kv[0], out uint item) && int.TryParse(kv[1], out int n) && n > 0)
                    yield return (item, n);
            }
        }
    }
}
