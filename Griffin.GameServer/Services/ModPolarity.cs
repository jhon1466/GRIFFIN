using Griffin.Common.Table;
using Griffin.Database.Entities;

namespace Griffin.GameServer.Services
{
    /// <summary>What the account keeps of a mod besides its row: the refinement count, the polarity bits and the backup of the last polarization.</summary>
    public sealed class ModRefineState
    {
        /// <summary><c>GunWeaponMod</c> field 5 (<c>HGPBFMFAPEB</c>): refinement steps already bought.</summary>
        public uint Count { get; set; }

        /// <summary><c>GunWeaponMod</c> field 7 (<c>MJFMAOELLGO</c>): four 16-bit slots, low 12 bits = polarity permille, bit 12 = set.</summary>
        public ulong Polarity { get; set; }

        public bool HasBackup { get; set; }

        public ulong BackupPolarity { get; set; }

        public uint BackupCount { get; set; }

        /// <summary>Polarity bits rolled by the pending continuous polarization, one per round.</summary>
        public List<ulong> Candidates { get; set; } = [];

        public ulong CandidateBase { get; set; }
    }

    /// <summary>
    /// The amplification ("polarity", 增幅) and refinement ("精调") of weapon mods in client 4.0.5136 (A-78). Read from the client and the tables:
    /// an affix that is polarized is worth <c>ceil(value × (1 + permille / 1000))</c> (<c>LICHBCMONFI.GMNPLEDOPBM</c>); the permille a polarization
    /// rolls is drawn with the weights of <c>PolarityAffixData</c> (id = <c>WeaponModData</c> column 15; each entry is <c>permille:weight:level</c>, the weights
    /// are the percentages shown by <c>ModProbabilityShowData</c>: 100‰ .. 1500‰ for rank 4 and up to 2000‰ for rank 5); a refinement step raises an affix by one
    /// step of its <c>AffixGroupData</c> row (column 7) until its maximum (column 6) and then by one polarity level, and costs the
    /// <c>ModRefineData</c> row of the step (column 2 items, column 3 gold). The cost of a polarization is
    /// <c>GlobalSystemData</c> 296 (item 131002 × 1 per affix) and 301 (8000 gold per affix). Not known to the tables, hence assumed: which affixes of the mod
    /// are polarized (drawn at random among its affixes), that the refinement works base steps first, and that redoing restores the polarity and the count.
    /// </summary>
    public static class ModPolarity
    {
        public const string StateKey = "mod.refine";

        private static Dictionary<uint, List<(uint Permille, uint Weight, uint Level)>>? levels;
        private static readonly object Gate = new();

        private static Dictionary<string, ModRefineState> Load(IAccountStateService state, uint uid) =>
            state.Get<Dictionary<string, ModRefineState>>(uid, StateKey) ?? [];

        /// <summary>The message of a mod as the client reads it: affixes, power, refinement count and polarity.</summary>
        public static Griffin.Common.Proto.GunWeaponMod ToProto(ITableService tables, IAccountStateService state, uint uid, WeaponModEntity mod) =>
            Griffin.GameServer.Extensions.ProtoExtensions.ToProtoWeaponMod(mod, Get(state, uid, mod.Id), ModPower.EffectIdOf(tables, mod));

        /// <summary>The state of every mod of the account read ONCE: a list of hundreds of mods must not read the database once per mod (0.4-0.7 s per request, A-91).</summary>
        public static Dictionary<string, ModRefineState> All(IAccountStateService state, uint uid) => Load(state, uid);

        public static ModRefineState From(Dictionary<string, ModRefineState> all, uint modId) =>
            all.GetValueOrDefault(modId.ToString()) ?? new ModRefineState();

        public static Griffin.Common.Proto.GunWeaponMod ToProto(ITableService tables, Dictionary<string, ModRefineState> all, WeaponModEntity mod) =>
            Griffin.GameServer.Extensions.ProtoExtensions.ToProtoWeaponMod(mod, From(all, mod.Id), ModPower.EffectIdOf(tables, mod));

        public static ModRefineState Get(IAccountStateService state, uint uid, uint modId) =>
            Load(state, uid).GetValueOrDefault(modId.ToString()) ?? new ModRefineState();

        public static void Set(IAccountStateService state, uint uid, uint modId, ModRefineState value)
        {
            Dictionary<string, ModRefineState> all = Load(state, uid);
            if (value.Count == 0 && value.Polarity == 0 && !value.HasBackup && value.Candidates.Count == 0)
                all.Remove(modId.ToString());
            else
                all[modId.ToString()] = value;
            state.Set(uid, StateKey, all);
        }

        /// <summary>(item, count) of one polarized affix: <c>GlobalSystemData</c> field 296.</summary>
        public static (uint Item, uint Count) AmplifyItem(ITableService tables)
        {
            GlobalSystemData? row = tables.GetTable<GlobalSystemData>().FirstOrDefault();
            return row is not null && TableFields.Map(row, 296).FirstOrDefault() is { Key: not 0 } pair ? pair : (131002, 1);
        }

        /// <summary>Gold of one polarized affix: <c>GlobalSystemData</c> field 301 (item 2 -> 8000).</summary>
        public static uint AmplifyGold(ITableService tables)
        {
            GlobalSystemData? row = tables.GetTable<GlobalSystemData>().FirstOrDefault();
            return row is not null && TableFields.Map(row, 301).FirstOrDefault() is { Value: not 0 } pair ? pair.Value : 8000;
        }

        private static List<(uint Permille, uint Weight, uint Level)> Levels(ITableService tables, uint modStcId)
        {
            lock (Gate)
            {
                if (levels is null)
                {
                    levels = [];
                    foreach (PolarityAffixData row in tables.GetTable<PolarityAffixData>())
                    {
                        List<(uint, uint, uint)> list = [];
                        foreach (string entry in TableFields.S(row, 3).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            string[] parts = entry.Split(':');
                            if (parts.Length == 3 && uint.TryParse(parts[0], out uint permille) && uint.TryParse(parts[1], out uint weight) && uint.TryParse(parts[2], out uint level))
                                list.Add((permille, weight, level));
                        }

                        levels[TableFields.U(row, 1)] = list;
                    }
                }

                WeaponModData? mod = tables.GetTable<WeaponModData>().FirstOrDefault(row => row.Id == modStcId);
                return mod is not null && levels.TryGetValue(TableFields.U(mod, 15), out var found) ? found : [];
            }
        }

        public static bool CanPolarize(ITableService tables, uint modStcId) => Levels(tables, modStcId).Count > 0;

        public static uint Rank(ITableService tables, uint modStcId) =>
            tables.GetTable<WeaponModData>().Where(row => row.Id == modStcId).Select(row => TableFields.U(row, 3)).FirstOrDefault();

        /// <summary>Experience a mod is worth as a material (<c>WeaponModData</c> column 9).</summary>
        public static uint BaseExp(ITableService tables, uint modStcId) =>
            tables.GetTable<WeaponModData>().Where(row => row.Id == modStcId).Select(row => TableFields.U(row, 9)).FirstOrDefault();

        public static int AffixCount(ulong affixes)
        {
            int count = 0;
            for (int slot = 0; slot < 4; slot++)
                if (((affixes >> (slot * 16)) & 0xFF) != 0)
                    count++;
            return count;
        }

        public static uint PermilleOf(ulong polarity, int slot) => (uint)((polarity >> (slot * 16)) & 0xFFF);

        public static bool Polarized(ulong polarity) => polarity != 0;

        private static uint Draw(List<(uint Permille, uint Weight, uint Level)> table, Random random)
        {
            uint total = (uint)table.Sum(entry => entry.Weight);
            uint pick = (uint)random.Next((int)Math.Max(1, total));
            foreach (var entry in table)
            {
                if (pick < entry.Weight)
                    return entry.Permille;
                pick -= entry.Weight;
            }

            return table[^1].Permille;
        }

        /// <summary>Polarity bits for <paramref name="count"/> affixes of the mod chosen at random, each with a permille drawn by the weights of its table.</summary>
        public static ulong Roll(ITableService tables, uint modStcId, ulong affixes, int count, Random random)
        {
            var table = Levels(tables, modStcId);
            List<int> slots = Enumerable.Range(0, 4).Where(slot => ((affixes >> (slot * 16)) & 0xFF) != 0).OrderBy(_ => random.Next()).Take(count).ToList();
            ulong bits = 0;
            if (table.Count == 0)
                return bits;

            foreach (int slot in slots)
                bits |= (ulong)(0x1000 | (Draw(table, random) & 0xFFF)) << (slot * 16);
            return bits;
        }

        /// <summary>The value of an affix after its polarity (the client rounds up).</summary>
        public static int Effective(int value, uint permille) => (int)(((long)value * (1000 + permille) + 999) / 1000);

        /// <summary>(property, value) of every affix of a mod, polarity applied.</summary>
        public static IEnumerable<(int Property, int Value)> Values(ulong affixes, ulong polarity)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                int value = (int)((affixes >> (slot * 16)) & 0xFF);
                int property = (int)((affixes >> (slot * 16 + 8)) & 0xFF);
                if (value != 0 && property != 0)
                    yield return (property, Effective(value, PermilleOf(polarity, slot)));
            }
        }

        /// <summary>How many refinement steps an affix still takes: (steps to its maximum value, polarity levels left); null when the slot is not polarized.</summary>
        public static (int ValueSteps, int LevelSteps)? Remaining(ITableService tables, uint modStcId, ulong affixes, ulong polarity, int slot)
        {
            uint permille = PermilleOf(polarity, slot);
            if (permille == 0)
                return null;

            uint property = (uint)((affixes >> (slot * 16 + 8)) & 0xFF);
            uint value = (uint)((affixes >> (slot * 16)) & 0xFF);
            var range = ModAffixes.RangeOf(tables, modStcId, slot, property);
            var table = Levels(tables, modStcId);
            if (range is null || table.Count == 0)
                return null;

            int valueSteps = value >= range.Value.Max || range.Value.Step == 0 ? 0 : (int)((range.Value.Max - value + range.Value.Step - 1) / range.Value.Step);
            uint level = table.FirstOrDefault(entry => entry.Permille == permille).Level;
            return (valueSteps, (int)(table[^1].Level - level));
        }

        /// <summary>Applies <paramref name="steps"/> refinement steps to a slot: first the value steps, then the polarity levels. False when the slot cannot take that many.</summary>
        public static bool Refine(ITableService tables, uint modStcId, ref ulong affixes, ref ulong polarity, int slot, int steps)
        {
            if (Remaining(tables, modStcId, affixes, polarity, slot) is not { } left || steps > left.ValueSteps + left.LevelSteps)
                return false;

            uint property = (uint)((affixes >> (slot * 16 + 8)) & 0xFF);
            uint value = (uint)((affixes >> (slot * 16)) & 0xFF);
            var range = ModAffixes.RangeOf(tables, modStcId, slot, property)!.Value;
            var table = Levels(tables, modStcId);
            uint permille = PermilleOf(polarity, slot);
            int levelIndex = table.FindIndex(entry => entry.Permille == permille);
            for (int step = 0; step < steps; step++)
            {
                if (value < range.Max)
                    value = Math.Min(range.Max, value + range.Step);
                else
                    levelIndex = Math.Min(table.Count - 1, levelIndex + 1);
            }

            affixes = (affixes & ~(0xFFUL << (slot * 16))) | ((ulong)value << (slot * 16));
            polarity = (polarity & ~(0xFFFFUL << (slot * 16))) | ((ulong)(0x1000 | (table[levelIndex].Permille & 0xFFF)) << (slot * 16));
            return true;
        }

        /// <summary>Cost of the refinement steps <paramref name="from"/>+1 .. <paramref name="from"/>+<paramref name="steps"/> (<c>ModRefineData</c>: column 2 items, column 3 gold); null when a row is missing.</summary>
        public static Dictionary<uint, int>? RefineCost(ITableService tables, uint from, int steps)
        {
            Dictionary<uint, int> cost = [];
            List<ModRefineData> rows = tables.GetTable<ModRefineData>();
            for (uint index = from + 1; index <= from + steps; index++)
            {
                ModRefineData? row = rows.FirstOrDefault(r => TableFields.U(r, 1) == index);
                if (row is null)
                    return null;

                foreach (int column in new[] { 2, 3 })
                    foreach ((uint item, uint count) in TableFields.Map(row, column))
                        cost[item] = cost.GetValueOrDefault(item) + (int)count;
            }

            return cost;
        }
    }
}
