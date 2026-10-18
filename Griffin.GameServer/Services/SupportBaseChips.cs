using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The base chips ("基准元件", 162 rows of <c>SupportChipBaseChipData</c>) of the "框架拓展" screen (A-97). The real game gives them out in other modes (the empty list says
    /// "obtained through gameplay such as 异位冲突"); nothing in the tables says how, so the account owns <see cref="Copies"/> of each. An instance is
    /// <c>{id, stc id, locked, six stat values}</c> (<c>SC_SupportChip</c> field 1): the client adds <c>values[stat - 1]</c> of every chip placed in a formation to that stat of the
    /// formation (<c>JJGCDACIHNI.GIMBDHBFLIB</c>) and shows as a "secondary" any other non-zero value. Here the chip has its table value (column 7) in its own stat (column 6)
    /// and no secondary. A formation keeps, for each of the six frames (stat), the chips placed in its seven slots (<c>CS_SupportFormationModifyBaseChip</c>).
    /// </summary>
    public static class SupportBaseChips
    {
        public const string StateKey = "support.basechips";
        public const int Copies = 2;
        private const uint FirstId = 100000;

        public sealed record Chip(uint Id, uint Stc, int Stat, uint Value);

        public static List<Chip> All(ITableService tables)
        {
            List<Chip> chips = [];
            uint index = 0;
            foreach (SupportChipBaseChipData row in tables.GetTable<SupportChipBaseChipData>())
            {
                int stat = (int)TableFields.U(row, 6);
                for (int copy = 0; copy < Copies; copy++)
                    chips.Add(new Chip(FirstId + index * Copies + (uint)copy, TableFields.U(row, 1), stat, TableFields.U(row, 7)));
                index++;
            }

            return chips;
        }

        public static MCFANDPJCGB ToProto(Chip chip)
        {
            MCFANDPJCGB message = new() { Id = chip.Id, StcId = chip.Stc };
            for (int stat = 1; stat <= 6; stat++)
                message.ACEIFDEGLMH.Add(stat == chip.Stat ? chip.Value : 0u);
            return message;
        }

        public static Dictionary<uint, List<uint>> Placed(IAccountStateService state, uint uid, uint formation) =>
            (state.Get<Dictionary<string, Dictionary<uint, List<uint>>>>(uid, StateKey) ?? []).GetValueOrDefault(formation.ToString()) ?? [];

        public static void SetPlaced(IAccountStateService state, uint uid, uint formation, Dictionary<uint, List<uint>> placed)
        {
            Dictionary<string, Dictionary<uint, List<uint>>> all = state.Get<Dictionary<string, Dictionary<uint, List<uint>>>>(uid, StateKey) ?? [];
            all[formation.ToString()] = placed;
            state.Set(uid, StateKey, all);
        }

        /// <summary>The sum of the stat values of the chips placed in a formation, by stat 1..6.</summary>
        public static int[] Totals(ITableService tables, IAccountStateService state, uint uid, uint formation)
        {
            int[] totals = new int[7];
            Dictionary<uint, Chip> owned = All(tables).ToDictionary(chip => chip.Id);
            foreach (List<uint> ids in Placed(state, uid, formation).Values)
                foreach (uint id in ids.Where(id => id != 0).Distinct())
                    if (owned.TryGetValue(id, out Chip? chip) && chip.Stat is >= 1 and <= 6)
                        totals[chip.Stat] += (int)chip.Value;
            return totals;
        }

        public static void Fill(IAccountStateService state, uint uid, uint formation, OMBHFHECHOO message)
        {
            message.JELCIHPBECA.Clear();
            foreach ((uint frame, List<uint> ids) in Placed(state, uid, formation))
            {
                DKIBNBANDLM slots = new();
                slots.CCGPCPEPBEA.AddRange(ids);
                message.JELCIHPBECA[frame] = slots;
            }
        }
    }
}
