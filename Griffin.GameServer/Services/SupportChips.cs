using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The core chips (核心芯片) of the support room and the stats they give. Read from client 1189260 and the 4.0.5136 client (A-77):
    /// <c>SupportChipCoreChipData</c> (63 chips, column 4 = chip id used to find its breaks), <c>SupportChipCoreChipBreakByCoreChipData</c> (chip id ->
    /// the six break rows packed as bytes), <c>SupportChipCoreChipBreakData</c> column 6 = the <c>PropertyData</c> row of a break. A support
    /// formation lists its core chips (<c>SC_SupportFormationModifyCoreChip</c>, field 2) and the formation selected for battle
    /// (<c>CS_SupportFormationSetBattleFormationId</c>) adds the rows of its chips to every character (client <c>FJLGHAHLHML.DLAFCIPLJFP</c>,
    /// flat and as percentage). The account owns every core chip at break 0 (like the mods; the chip-collection stage that drops them in the real game is
    /// not simulated); breaking and decomposing stay refused because the table does not give their cost.
    /// </summary>
    public static class SupportChips
    {
        public const string ChipsKey = "support.corechips";
        public const string FormationKey = "support.formation.core";
        public const string BattleKey = "support.battle";

        /// <summary>Core chip slots of a formation (<c>SupportChipCoreSlotData</c> has three rows).</summary>
        public const int FormationSlots = 3;

        public sealed class ChipState
        {
            public uint Id { get; set; }

            public uint StcId { get; set; }

            public bool Locked { get; set; }

            public uint BreakTimes { get; set; }
        }

        /// <summary>Copies of every core chip the account owns: the chip itself and the five that the five breaks consume (A-78).</summary>
        public const int CopiesPerChip = 6;

        private const string CopiesFlagKey = "support.corechips.copies";

        public static List<ChipState> Chips(ITableService tables, IAccountStateService state, uint uid)
        {
            List<ChipState>? chips = state.Get<List<ChipState>>(uid, ChipsKey);
            // the copies are given once: afterwards breaking and decomposing remove chips for good
            if (chips is { Count: > 0 } && state.Get<bool?>(uid, CopiesFlagKey) == true)
                return chips;

            List<uint> stcIds = tables.GetTable<SupportChipCoreChipData>().Select(row => TableFields.U(row, 1)).OrderBy(id => id).ToList();
            chips ??= stcIds.Select((stc, index) => new ChipState { Id = (uint)index + 1, StcId = stc }).ToList();
            // chips saved before the copies existed keep their ids; the copies take the ids after them
            uint next = chips.Count == 0 ? 1 : chips.Max(chip => chip.Id) + 1;
            foreach (uint stc in stcIds)
            {
                int owned = chips.Count(chip => chip.StcId == stc);
                for (; owned < CopiesPerChip; owned++)
                    chips.Add(new ChipState { Id = next++, StcId = stc });
            }

            state.Set(uid, ChipsKey, chips);
            state.Set<bool?>(uid, CopiesFlagKey, true);
            return chips;
        }

        public static void Save(IAccountStateService state, uint uid, List<ChipState> chips) => state.Set(uid, ChipsKey, chips);

        /// <summary>True when the chip is placed in any formation.</summary>
        public static bool InFormation(IAccountStateService state, uint uid, uint chipId) =>
            (state.Get<Dictionary<string, List<uint>>>(uid, FormationKey) ?? []).Values.Any(list => list.Contains(chipId));

        public static void RemoveFromFormations(IAccountStateService state, uint uid, ICollection<uint> chipIds)
        {
            Dictionary<string, List<uint>> all = state.Get<Dictionary<string, List<uint>>>(uid, FormationKey) ?? [];
            foreach (List<uint> list in all.Values)
                list.RemoveAll(chipIds.Contains);
            state.Set(uid, FormationKey, all);
        }

        /// <summary>Highest break of a chip: its <c>SupportChipCoreChipBreakByCoreChipData</c> row lists one break row per break (0..5).</summary>
        public static uint MaxBreak(ITableService tables, uint stcId)
        {
            SupportChipCoreChipData? data = tables.GetTable<SupportChipCoreChipData>().FirstOrDefault(row => TableFields.U(row, 1) == stcId);
            SupportChipCoreChipBreakByCoreChipData? breaks = data is null ? null : tables.GetTable<SupportChipCoreChipBreakByCoreChipData>().FirstOrDefault(row => TableFields.U(row, 1) == TableFields.U(data, 4));
            int count = breaks is null ? 1 : TableFields.List(breaks, 2).Count;
            return (uint)Math.Max(0, count - 1);
        }

        /// <summary>What decomposing a chip returns: <c>SupportChipCoreChipData</c> column 8 (item -> count).</summary>
        public static IReadOnlyList<(uint Item, uint Count)> DecomposeYield(ITableService tables, uint stcId)
        {
            SupportChipCoreChipData? data = tables.GetTable<SupportChipCoreChipData>().FirstOrDefault(row => TableFields.U(row, 1) == stcId);
            return data is null ? [] : TableFields.Map(data, 8);
        }

        public static IKKKAGJPGEJ ToProto(ChipState chip) =>
            new() { Id = chip.Id, StcId = chip.StcId, MDFIDAFPBJN = chip.Locked, BreakTimes = chip.BreakTimes };

        public static List<uint> FormationChips(IAccountStateService state, uint uid, uint formation) =>
            (state.Get<Dictionary<string, List<uint>>>(uid, FormationKey) ?? []).GetValueOrDefault(formation.ToString()) ?? [];

        public static void SetFormationChips(IAccountStateService state, uint uid, uint formation, List<uint> chips)
        {
            Dictionary<string, List<uint>> all = state.Get<Dictionary<string, List<uint>>>(uid, FormationKey) ?? [];
            all[formation.ToString()] = chips;
            state.Set(uid, FormationKey, all);
        }

        public static uint BattleFormation(IAccountStateService state, uint uid) => state.Get<uint?>(uid, BattleKey) is { } id and > 0 ? id : 1;

        /// <summary>The ids of the break rows (<c>SupportChipCoreChipBreakData</c>) of the core chips of the formation used in battle: the client's support unit lists them as its chip skills (A-95).</summary>
        public static IReadOnlyList<uint> ActiveBreakRows(ITableService tables, IAccountStateService state, uint uid)
        {
            List<ChipState> chips = Chips(tables, state, uid);
            List<uint> rows = [];
            foreach (uint chipId in FormationChips(state, uid, BattleFormation(state, uid)))
            {
                ChipState? chip = chips.FirstOrDefault(c => c.Id == chipId);
                SupportChipCoreChipData? data = chip is null ? null : tables.GetTable<SupportChipCoreChipData>().FirstOrDefault(row => TableFields.U(row, 1) == chip.StcId);
                SupportChipCoreChipBreakByCoreChipData? breaks = data is null ? null : tables.GetTable<SupportChipCoreChipBreakByCoreChipData>().FirstOrDefault(row => TableFields.U(row, 1) == TableFields.U(data, 4));
                IReadOnlyList<uint> breakRows = breaks is null ? [] : TableFields.List(breaks, 2);
                if (chip is not null && chip.BreakTimes < breakRows.Count)
                    rows.Add(breakRows[(int)chip.BreakTimes]);
            }

            return rows;
        }

        /// <summary>The <c>PropertyData</c> rows of the core chips of the formation used in battle (empty when it has none).</summary>
        public static IReadOnlyList<uint> ActiveRows(ITableService tables, IAccountStateService state, uint uid)
        {
            List<ChipState> chips = Chips(tables, state, uid);
            List<uint> rows = [];
            foreach (uint chipId in FormationChips(state, uid, BattleFormation(state, uid)))
            {
                ChipState? chip = chips.FirstOrDefault(c => c.Id == chipId);
                if (chip is null)
                    continue;

                SupportChipCoreChipData? data = tables.GetTable<SupportChipCoreChipData>().FirstOrDefault(row => TableFields.U(row, 1) == chip.StcId);
                if (data is null)
                    continue;

                SupportChipCoreChipBreakByCoreChipData? breaks = tables.GetTable<SupportChipCoreChipBreakByCoreChipData>().FirstOrDefault(row => TableFields.U(row, 1) == TableFields.U(data, 4));
                IReadOnlyList<uint> breakRows = breaks is null ? [] : TableFields.List(breaks, 2);
                if (chip.BreakTimes >= breakRows.Count)
                    continue;

                SupportChipCoreChipBreakData? breakRow = tables.GetTable<SupportChipCoreChipBreakData>().FirstOrDefault(row => TableFields.U(row, 1) == breakRows[(int)chip.BreakTimes]);
                if (breakRow is not null && TableFields.U(breakRow, 6) != 0)
                    rows.Add(TableFields.U(breakRow, 6));
            }

            return rows;
        }
    }
}
