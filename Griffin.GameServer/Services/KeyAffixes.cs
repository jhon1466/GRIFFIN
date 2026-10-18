using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The affixes (词条) of the public talent keys, as the 4.0.5136 client reads them (<c>KECBJKPEBJN</c>): <c>PublicSkillItem</c> field 4 = four 16-bit
    /// pairs, high byte = <c>DevelopProperty</c>, low byte = level; the value of an affix is the one of its level in <c>EntryPropertLevelData</c> (group =
    /// <c>EntryPropertData</c> column 2, level = column 3, value = column 4; level 3 of group 1 = 15, i.e. 1.5 %), never lower than the value of the
    /// minimum level of the rank of the key (<c>EntryPropertData</c> column 6, one number per rank). A property that the key also has in its base row
    /// (<c>TalentKeyData</c> column 5) keeps the larger of both. The properties a rank may take are <c>EntryData</c> column 4 and the level cap column 3
    /// (<c>EntryData</c> id = rank = <c>ItemData</c> column 9 of the key). A-75.
    /// </summary>
    public static class KeyAffixes
    {
        private static Dictionary<uint, (uint Group, IReadOnlyList<uint> MinLevels)>? properties;
        private static Dictionary<(uint Group, uint Level), uint>? values;
        private static Dictionary<uint, (uint MaxLevel, IReadOnlyList<uint> Properties)>? ranks;
        private static readonly object Gate = new();

        private static void Build(ITableService tables)
        {
            lock (Gate)
            {
                if (properties is not null)
                    return;

                var props = new Dictionary<uint, (uint, IReadOnlyList<uint>)>();
                foreach (EntryPropertData row in tables.GetTable<EntryPropertData>())
                    props[TableFields.U(row, 1)] = (TableFields.U(row, 2), TableFields.List(row, 6));

                var levels = new Dictionary<(uint, uint), uint>();
                foreach (EntryPropertLevelData row in tables.GetTable<EntryPropertLevelData>())
                    levels[(TableFields.U(row, 2), TableFields.U(row, 3))] = TableFields.U(row, 4);

                var byRank = new Dictionary<uint, (uint, IReadOnlyList<uint>)>();
                foreach (EntryData row in tables.GetTable<EntryData>())
                    byRank[TableFields.U(row, 1)] = (TableFields.U(row, 3), TableFields.List(row, 4));

                values = levels;
                ranks = byRank;
                properties = props;
            }
        }

        public static uint RankOf(ITableService tables, uint keyStcId) =>
            tables.GetTable<ItemData>().Where(row => row.Id == keyStcId).Select(row => TableFields.U(row, 9)).FirstOrDefault();

        /// <summary>Each pair names a property the rank allows, a level from 1 to the cap, and no property twice (at most four pairs).</summary>
        public static bool Valid(ITableService tables, ulong bits, uint keyStcId)
        {
            Build(tables);
            if (!ranks!.TryGetValue(RankOf(tables, keyStcId), out var rank))
                return bits == 0;

            HashSet<uint> seen = [];
            for (int slot = 0; slot < 4; slot++)
            {
                uint pair = (uint)((bits >> (slot * 16)) & 0xFFFF);
                uint property = pair >> 8, level = pair & 0xFF;
                if (property == 0 && level == 0)
                    continue;

                if (property == 0 || !rank.Properties.Contains(property) || level < 1 || level > rank.MaxLevel || !seen.Add(property))
                    return false;
            }

            return true;
        }

        private static IEnumerable<(uint Item, uint Count)> ParseCost(string text) =>
            text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split(':'))
                .Where(pair => pair.Length == 2 && uint.TryParse(pair[0], out _) && uint.TryParse(pair[1], out _))
                .Select(pair => (uint.Parse(pair[0]), uint.Parse(pair[1])));

        /// <summary>
        /// What it costs to go from one configuration to another (A-76; community wiki: a purple key unlocks one stat and a gold one two, paid with Universal
        /// Chain Keys and Transcription Conductors VI, new stats start at 0.5 % and are raised separately, evolving a stat costs Universal Chain Keys and
        /// gold): a new property pays <c>EntryPropertData</c> column 3 (one "item:count,..." list per rank, split by ';'); every level a property rises pays the
        /// <c>EntryPropertLevelData</c> row of that level, column 5 when the key already has that property in its base row and column 6 when it is a new stat.
        /// Lowering or resetting is free.
        /// </summary>
        public static Dictionary<uint, int> Cost(ITableService tables, ulong oldBits, ulong newBits, uint keyStcId, IReadOnlyDictionary<int, int> baseValues)
        {
            Build(tables);
            uint rank = RankOf(tables, keyStcId);
            var cost = new Dictionary<uint, int>();
            void Add(IEnumerable<(uint Item, uint Count)> list)
            {
                foreach ((uint item, uint count) in list)
                    cost[item] = cost.GetValueOrDefault(item) + (int)count;
            }

            Dictionary<uint, uint> before = [];
            for (int slot = 0; slot < 4; slot++)
            {
                uint pair = (uint)((oldBits >> (slot * 16)) & 0xFFFF);
                if ((pair >> 8) != 0)
                    before[pair >> 8] = pair & 0xFF;
            }

            for (int slot = 0; slot < 4; slot++)
            {
                uint pair = (uint)((newBits >> (slot * 16)) & 0xFFFF);
                uint property = pair >> 8, level = pair & 0xFF;
                if (property == 0)
                    continue;

                EntryPropertData? def = tables.GetTable<EntryPropertData>().FirstOrDefault(row => TableFields.U(row, 1) == property);
                if (def is null)
                    continue;

                uint known = before.GetValueOrDefault(property);
                if (known == 0)
                {
                    string[] perRank = TableFields.S(def, 3).Split(';', StringSplitOptions.RemoveEmptyEntries);
                    if (rank >= 1 && rank <= perRank.Length)
                        Add(ParseCost(perRank[rank - 1]));
                }

                uint group = TableFields.U(def, 2);
                bool isBase = baseValues.ContainsKey((int)property);
                for (uint step = Math.Max(known, 1) + (known == 0 ? 0u : 1u); step <= level; step++)
                {
                    if (known == 0 && step == 1)
                        continue;
                    EntryPropertLevelData? row = tables.GetTable<EntryPropertLevelData>().FirstOrDefault(r => TableFields.U(r, 2) == group && TableFields.U(r, 3) == step);
                    if (row is not null)
                        Add(ParseCost(TableFields.S(row, isBase ? 5 : 6)));
                }
            }

            return cost;
        }

        /// <summary>The affix part of a key: DevelopProperty -> value (the value of the level, at least the one of the minimum level of the rank).</summary>
        public static IReadOnlyDictionary<int, int> AffixValues(ITableService tables, ulong bits, uint keyStcId, IReadOnlyDictionary<int, int> baseValues)
        {
            Build(tables);
            uint rank = RankOf(tables, keyStcId);
            var result = new Dictionary<int, int>();
            for (int slot = 0; slot < 4; slot++)
            {
                uint pair = (uint)((bits >> (slot * 16)) & 0xFFFF);
                uint property = pair >> 8, level = pair & 0xFF;
                if (property == 0 || !properties!.TryGetValue(property, out var def))
                    continue;

                uint minLevel = rank >= 1 && rank <= def.MinLevels.Count ? def.MinLevels[(int)rank - 1] : 1;
                uint atLevel = values!.GetValueOrDefault((def.Group, level));
                uint atMin = values.GetValueOrDefault((def.Group, minLevel));
                baseValues.TryGetValue((int)property, out int baseValue);
                result[(int)property] = (int)Math.Max(Math.Max(atLevel, atMin), (uint)baseValue);
            }

            return result;
        }
    }
}
