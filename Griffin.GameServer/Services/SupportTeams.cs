using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The support teams ("备勤区": 20 rows of <c>SupportData</c>) and where the player put them (A-93). <c>SC_SupportTeams</c> lists the teams the account owns (all of
    /// them: nothing in the tables says how they are obtained), the room has <c>SupportConfigData</c> column 1 = 7 formations of 3 slots (<c>SupportSlotData</c>: type
    /// of the slot in column 2, column 5 = level the slot opens at, 999 = never), and <c>CS_SupportFormationModify</c> puts a team (0 = none) in a slot; every slot also keeps
    /// the order of its ammunition (<c>CS_SupportFormationAmmunitionSequence</c>). Assumed: a slot takes a team whose <c>SupportData</c> type (column 6) is the type of the slot.
    /// </summary>
    public static class SupportTeams
    {
        public const string StateKey = "support.slots";
        public const int Slots = 3;

        public sealed class SlotState
        {
            public uint Team { get; set; }

            public List<uint> Ammunition { get; set; } = [];
        }

        public const string LevelKey = "support.room.level";

        public static uint RoomLevel(IAccountStateService state, uint uid) => Math.Max(1, state.Get<uint?>(uid, LevelKey) ?? 1);

        /// <summary>Cost of the upgrade from <paramref name="level"/>: (item, amount) from <c>SupportLevelData</c> row <c>level</c> column 3, the item being <c>SupportConfigData</c> column 4; null at the last level.</summary>
        public static (uint Item, int Amount)? UpgradeCost(ITableService tables, uint level)
        {
            if (!tables.GetTable<SupportLevelData>().Any(row => TableFields.U(row, 1) == level + 1))
                return null;

            SupportLevelData? row = tables.GetTable<SupportLevelData>().FirstOrDefault(r => TableFields.U(r, 1) == level);
            uint item = tables.GetTable<SupportConfigData>().Select(r => TableFields.U(r, 4)).FirstOrDefault();
            return row is null || item == 0 ? null : (item, (int)TableFields.U(row, 3));
        }

        /// <summary>
        /// Cost of going from <paramref name="from"/> to <paramref name="target"/> in one request (the client lets the player pick the target level and shows the sum
        /// of the rows from..target-1, <c>OPLBICLDIEI.OLMONDJLAKF</c>); null when the target is not above the level or past the last one.
        /// </summary>
        public static (uint Item, int Amount)? UpgradeCost(ITableService tables, uint from, uint target)
        {
            uint last = tables.GetTable<SupportLevelData>().Select(row => TableFields.U(row, 1)).DefaultIfEmpty(0u).Max();
            uint item = tables.GetTable<SupportConfigData>().Select(r => TableFields.U(r, 4)).FirstOrDefault();
            if (target <= from || target > last || item == 0)
                return null;

            long amount = 0;
            for (uint level = from; level < target; level++)
            {
                SupportLevelData? row = tables.GetTable<SupportLevelData>().FirstOrDefault(r => TableFields.U(r, 1) == level);
                if (row is null)
                    return null;

                amount += TableFields.U(row, 3);
            }

            return (item, (int)Math.Min(int.MaxValue, amount));
        }

        /// <summary>One slot of the support unit of a battle: the team, the row of <c>SupportSkillGroupData</c> its formation stats reach, and the ammunition it fires.</summary>
        public sealed record BattleSlot(uint Team, uint SkillRow, uint Ammunition, uint Load);

        /// <summary>What <c>SC_StageInto</c> carries for the support formation of the battle (the client's own <c>MHMPCAEGFEC</c> builds the same message from its formation).</summary>
        public sealed record Battle(uint Formation, int[] Stats, IReadOnlyList<BattleSlot> Slots, uint Load, IReadOnlyList<uint> ChipRows);

        /// <summary>
        /// The six stats of a formation as the client shows them (<c>OPLBICLDIEI.OGNKNMDJEJH</c>): the base of <c>SupportConfigData</c> column 10 plus, for every room level up to the
        /// current one, the stat bonus of its <c>SupportLevelData</c> row (type 1 in column 4, [stat, value] in column 5). The base chips of the formation are not simulated.
        /// </summary>
        public static int[] Stats(ITableService tables, uint level)
        {
            int[] stats = new int[7];
            SupportConfigData? config = tables.GetTable<SupportConfigData>().FirstOrDefault();
            if (config is not null)
                foreach ((uint stat, uint value) in TableFields.Map(config, 10))
                    if (stat is >= 1 and <= 6)
                        stats[stat] = (int)value;

            foreach (SupportLevelData row in tables.GetTable<SupportLevelData>().Where(r => TableFields.U(r, 1) >= 1 && TableFields.U(r, 1) <= level))
            {
                IReadOnlyList<uint> bonus = TableFields.List(row, 5);
                if (TableFields.U(row, 4) == 1 && bonus.Count >= 2 && bonus[0] is >= 1 and <= 6)
                    stats[bonus[0]] += (int)bonus[1];
            }

            return stats;
        }

        /// <summary>The highest tier of the skill group of a team whose stat requirements (column 4: [stat, minimum]) the formation reaches (<c>FKHACFFKFAG</c>); null when none.</summary>
        public static SupportSkillGroupData? SkillRow(ITableService tables, uint team, int[] stats)
        {
            SupportData? data = tables.GetTable<SupportData>().FirstOrDefault(row => TableFields.U(row, 1) == team);
            if (data is null)
                return null;

            uint group = TableFields.U(data, 12);
            return tables.GetTable<SupportSkillGroupData>()
                .Where(row => TableFields.U(row, 2) == group && TableFields.Map(row, 4).All(req => req.Key is >= 1 and <= 6 && stats[req.Key] >= req.Value))
                .OrderByDescending(row => TableFields.U(row, 3)).FirstOrDefault();
        }

        /// <summary>The ammunition a slot fires: the first of the order the player saved, else the first free one of the team's ammunition type (<c>DKOFHJHEJKK</c>).</summary>
        public static uint AmmunitionOf(ITableService tables, uint team, SlotState slot)
        {
            if (slot.Ammunition.FirstOrDefault(id => id != 0) is var saved and not 0)
                return saved;

            SupportData? data = tables.GetTable<SupportData>().FirstOrDefault(row => TableFields.U(row, 1) == team);
            SupportAmmunitionByTypeData? type = data is null ? null : tables.GetTable<SupportAmmunitionByTypeData>().FirstOrDefault(row => TableFields.U(row, 1) == TableFields.U(data, 9));
            List<SupportAmmunitionData> ammunition = type is null ? [] : TableFields.List(type, 2)
                .Select(id => tables.GetTable<SupportAmmunitionData>().FirstOrDefault(row => TableFields.U(row, 1) == id)).OfType<SupportAmmunitionData>()
                .OrderBy(row => TableFields.U(row, 4)).ToList();
            SupportAmmunitionData? free = ammunition.FirstOrDefault(row => TableFields.U(row, 5) == 0) ?? ammunition.FirstOrDefault();
            return free is null ? 0 : TableFields.U(free, 1);
        }

        /// <summary>The support unit of the formation chosen for battle; null when none of its slots holds a team (or <c>GRIFFIN_NO_SUPPORT=1</c>).</summary>
        public static Battle? ForBattle(ITableService tables, IAccountStateService state, uint uid)
        {
            if (Environment.GetEnvironmentVariable("GRIFFIN_NO_SUPPORT") == "1")
                return null;

            uint formation = SupportChips.BattleFormation(state, uid);
            int[] stats = Stats(tables, RoomLevel(state, uid));
            int[] chipTotals = SupportBaseChips.Totals(tables, state, uid, formation);
            for (int stat = 1; stat <= 6; stat++)
                stats[stat] += chipTotals[stat];
            List<SlotState> slots = SlotsOf(state, uid, formation);
            List<BattleSlot> entries = [];
            uint load = 0;
            foreach (SlotState slot in slots.Take(Slots))
            {
                if (slot.Team == 0 || SkillRow(tables, slot.Team, stats) is not { } skill)
                    continue;

                uint slotLoad = TableFields.U(skill, 14);
                foreach ((uint stat, uint coefficient) in TableFields.Map(skill, 15))
                    if (stat is >= 1 and <= 6)
                        slotLoad += (uint)stats[stat] * coefficient / 1000;

                load += slotLoad;
                entries.Add(new BattleSlot(slot.Team, TableFields.U(skill, 1), AmmunitionOf(tables, slot.Team, slot), slotLoad));
            }

            return entries.Count == 0 ? null : new Battle(formation, stats, entries, load, SupportChips.ActiveBreakRows(tables, state, uid));
        }

        public static List<uint> Owned(ITableService tables) => tables.GetTable<SupportData>().Select(row => TableFields.U(row, 1)).ToList();

        public static uint Formations(ITableService tables)
        {
            uint count = tables.GetTable<SupportConfigData>().Select(row => TableFields.U(row, 1)).FirstOrDefault();
            return count > 0 ? count : 7;
        }

        public static List<SlotState> SlotsOf(IAccountStateService state, uint uid, uint formation)
        {
            List<SlotState> slots = (state.Get<Dictionary<string, List<SlotState>>>(uid, StateKey) ?? []).GetValueOrDefault(formation.ToString()) ?? [];
            while (slots.Count < Slots)
                slots.Add(new SlotState());
            return slots;
        }

        public static void Save(IAccountStateService state, uint uid, uint formation, List<SlotState> slots)
        {
            Dictionary<string, List<SlotState>> all = state.Get<Dictionary<string, List<SlotState>>>(uid, StateKey) ?? [];
            all[formation.ToString()] = slots;
            state.Set(uid, StateKey, all);
        }

        /// <summary>Slot type (<c>SupportSlotData</c> column 2) and whether the slot is open (column 5 absent).</summary>
        public static (uint Type, bool Open) SlotInfo(ITableService tables, int slot)
        {
            SupportSlotData? row = tables.GetTable<SupportSlotData>().FirstOrDefault(r => TableFields.U(r, 1) == slot);
            return row is null ? (0, false) : (TableFields.U(row, 2), TableFields.U(row, 5) == 0);
        }

        public static uint TypeOf(ITableService tables, uint team) =>
            tables.GetTable<SupportData>().Where(row => TableFields.U(row, 1) == team).Select(row => TableFields.U(row, 6)).FirstOrDefault();

        /// <summary>Puts the saved slots (team and ammunition order) in a formation message.</summary>
        public static void Fill(IAccountStateService state, uint uid, uint formation, OMBHFHECHOO message)
        {
            List<SlotState> slots = SlotsOf(state, uid, formation);
            for (int index = 0; index < message.CDLNJHGCDMK.Count && index < slots.Count; index++)
            {
                message.CDLNJHGCDMK[index].COCHLPMJPOJ = slots[index].Team;
                message.CDLNJHGCDMK[index].JIHKJNEPGBB.Clear();
                message.CDLNJHGCDMK[index].JIHKJNEPGBB.AddRange(slots[index].Ammunition);
            }
        }
    }
}
