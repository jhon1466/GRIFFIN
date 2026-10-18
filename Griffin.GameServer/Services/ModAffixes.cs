using Griffin.Common.Table;
using Griffin.Database.Entities;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The affixes (词条) of the weapon mods. The client reads them from field 3 of <c>GunWeaponMod</c> (<c>NHHPFBAEDAG.CLAGALBCOOG</c>): four 16-bit
    /// pairs, low byte = value, high byte = <c>DevelopProperty</c> of the affix (0 = empty slot); a stat of the mod is that value as it is (the permille of
    /// a percentage property, e.g. 26 = 2.6 %). The tables that describe them (read by field number, client 1189260):
    /// <c>WeaponModData</c> column 11 = plan id; <c>AffixPlanIdData</c> columns 2..5 = the group of affixes of each of the four slots (0 = the mod has no such
    /// slot: rank 2 has one slot, rank 3 one or two, rank 4 two or three, rank 5 three or four); <c>AffixGroupByAffixGroupData</c> column 2 = the affixes of a
    /// group; <c>AffixGroupData</c> column 3 = the property, 4 = kind (1 flat, 2 percentage), 5 and 6 = lowest and highest value. ASSUMPTION A-74: the server
    /// that sold the mods is not known, so a mod gets, once, one affix per slot (no property repeated inside a mod when the group allows it) with a value
    /// drawn uniformly between the lowest and the highest of its row; the draw is seeded with the mod, so the same mod always gets the same affixes. The
    /// growth that levelling a mod may add (<c>ModUpgradeData</c> is a server table with no use in the client) is not simulated.
    /// </summary>
    public static class ModAffixes
    {
        private static Dictionary<uint, uint[]>? slotGroups;
        private static Dictionary<uint, uint[]>? groupAffixes;
        private static Dictionary<uint, (uint Property, uint Min, uint Max, uint Step)>? affixes;
        private static readonly object Gate = new();

        private static void Build(ITableService tables)
        {
            lock (Gate)
            {
                if (slotGroups is not null)
                    return;

                var plans = new Dictionary<uint, uint[]>();
                foreach (AffixPlanIdData plan in tables.GetTable<AffixPlanIdData>())
                    plans[TableFields.U(plan, 1)] = [TableFields.U(plan, 2), TableFields.U(plan, 3), TableFields.U(plan, 4), TableFields.U(plan, 5)];

                var groups = new Dictionary<uint, uint[]>();
                foreach (AffixGroupByAffixGroupData group in tables.GetTable<AffixGroupByAffixGroupData>())
                    groups[TableFields.U(group, 1)] = TableFields.List(group, 2).ToArray();

                var rows = new Dictionary<uint, (uint, uint, uint, uint)>();
                foreach (AffixGroupData row in tables.GetTable<AffixGroupData>())
                    rows[TableFields.U(row, 1)] = (TableFields.U(row, 3), TableFields.U(row, 5), TableFields.U(row, 6), TableFields.U(row, 7));

                groupAffixes = groups;
                affixes = rows;
                slotGroups = plans;
            }
        }

        /// <summary>(lowest, highest, step) of the affix with the given property in the group of a slot of the mod; null when the group has none.</summary>
        public static (uint Min, uint Max, uint Step)? RangeOf(ITableService tables, uint modStcId, int slot, uint property)
        {
            Build(tables);
            WeaponModData? mod = tables.GetTable<WeaponModData>().FirstOrDefault(row => row.Id == modStcId);
            if (mod is null || !slotGroups!.TryGetValue(TableFields.U(mod, 11), out uint[]? plan) || plan[slot] == 0
                || !groupAffixes!.TryGetValue(plan[slot], out uint[]? candidates))
                return null;

            foreach (uint id in candidates)
                if (affixes!.TryGetValue(id, out var row) && row.Property == property)
                    return (row.Min, row.Max, row.Step);
            return null;
        }

        /// <summary>The affix bits of a mod of the given table row, drawn with <paramref name="seed"/> (0 when its plan has no slot).</summary>
        public static ulong Roll(ITableService tables, uint modStcId, ulong seed)
        {
            Build(tables);
            WeaponModData? mod = tables.GetTable<WeaponModData>().FirstOrDefault(row => row.Id == modStcId);
            if (mod is null || !slotGroups!.TryGetValue(TableFields.U(mod, 11), out uint[]? plan))
                return 0;

            Random random = new(unchecked((int)(seed ^ (seed >> 32) ^ modStcId * 2654435761UL)));
            ulong bits = 0;
            HashSet<uint> used = [];
            for (int slot = 0; slot < 4; slot++)
            {
                if (plan[slot] == 0 || !groupAffixes!.TryGetValue(plan[slot], out uint[]? candidates) || candidates.Length == 0)
                    continue;

                List<uint> free = candidates.Where(id => affixes!.TryGetValue(id, out var row) && !used.Contains(row.Property)).ToList();
                uint chosen = free.Count > 0 ? free[random.Next(free.Count)] : candidates[random.Next(candidates.Length)];
                (uint property, uint min, uint max, uint step) = affixes![chosen];
                List<uint> tiers = Tiers(min, max, step);
                uint value = tiers[random.Next(tiers.Count)];
                if (property == 0 || property > 0xFF || value == 0 || value > 0xFF)
                    continue;

                used.Add(property);
                bits |= ((ulong)(property << 8) | value) << (slot * 16);
            }

            return bits;
        }

        /// <summary>
        /// The values an affix can start with, from the table of the community wiki ("配件强化基础数值": Attack 17, 19, 21, 23, 24; Attack % 2.6, 3.0, 3.4, 3.8;
        /// HP 38, 43, 48, 53, 54): the lowest value (<c>AffixGroupData</c> column 5), then steps of column 7 while they do not pass the highest (column 6), and the highest.
        /// </summary>
        public static List<uint> Tiers(uint min, uint max, uint step)
        {
            List<uint> tiers = [];
            for (uint value = min; value <= max && step > 0; value += step)
                tiers.Add(value);
            if (!tiers.Contains(max))
                tiers.Add(max);
            return tiers;
        }

        /// <summary>True when every pair of the bits is one of the tiers of its group (mods rolled before the tiers were known are rolled again).</summary>
        public static bool TiersValid(ITableService tables, uint modStcId, ulong bits)
        {
            Build(tables);
            WeaponModData? mod = tables.GetTable<WeaponModData>().FirstOrDefault(row => row.Id == modStcId);
            if (mod is null || !slotGroups!.TryGetValue(TableFields.U(mod, 11), out uint[]? plan))
                return true;

            for (int slot = 0; slot < 4; slot++)
            {
                uint pair = (uint)((bits >> (slot * 16)) & 0xFFFF);
                uint property = pair >> 8, value = pair & 0xFF;
                if (property == 0 || plan[slot] == 0 || !groupAffixes!.TryGetValue(plan[slot], out uint[]? candidates))
                    continue;

                bool ok = candidates.Any(id => affixes!.TryGetValue(id, out var row) && row.Property == property
                    && Tiers(row.Min, row.Max, row.Step).Contains(value));
                if (!ok)
                    return false;
            }

            return true;
        }

        /// <summary>Gives the affixes to every mod that still has none (mods created before this existed); true when something changed.</summary>
        public static bool Ensure(ITableService tables, IEnumerable<WeaponModEntity> mods)
        {
            bool changed = false;
            foreach (WeaponModEntity mod in mods)
            {
                if (mod.Field7 != 0 && TiersValid(tables, mod.WeaponModId, mod.Field7))
                    continue;

                ulong bits = Roll(tables, mod.WeaponModId, mod.Uid);
                if (bits != 0)
                {
                    mod.Field7 = bits;
                    changed = true;
                }
            }

            return changed;
        }
    }
}
