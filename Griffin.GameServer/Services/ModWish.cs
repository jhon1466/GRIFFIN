using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The wished effect of the weapon-part simulation stages (SimCombatResourceData types 30..33, client UISimCombatWeaponModWishDialog). The
    /// options are that row's column 8 (suit tabs 102..117); the client sends the chosen one with CS_SimCombatWeaponModAssignedDrop
    /// {1 = stage, 2 = simulation type, 3 = suit tab}. A suit tab is ModPowerData column 2 of rows 1..16, whose column 3 is the effect
    /// (1..16, fire, ...) that the rank 4/5 mods of plan 201 carry. The mods dropped by those stages then carry the wished effect (A-229).
    /// </summary>
    public static class ModWish
    {
        public const string Key = "weapon-mod-wish.v1";

        public sealed class State
        {
            /// <summary>Stage id or simulation type -> wished suit tab.</summary>
            public Dictionary<uint, uint> Wishes { get; set; } = [];
        }

        private static readonly AsyncLocal<uint> current = new();

        /// <summary>The effect the mods created now must carry (0 = random).</summary>
        public static uint CurrentEffect
        {
            get => current.Value;
            set => current.Value = value;
        }

        /// <summary>The effect of a suit tab (ModPowerData column 2 -> column 3), 0 when unknown.</summary>
        public static uint EffectOfSuit(ITableService tables, uint suit) =>
            suit == 0 ? 0 : tables.GetTable<ModPowerData>().Where(r => TableFields.U(r, 2) == suit).Select(r => TableFields.U(r, 3)).FirstOrDefault();

        /// <summary>The wished suit tab of a stage: by its id, else by its simulation type.</summary>
        public static uint SuitFor(ITableService tables, State? state, uint stageId)
        {
            if (state is null)
                return 0;
            if (state.Wishes.TryGetValue(stageId, out uint suit))
                return suit;
            uint type = tables.GetTable<SimCombatResourceData>().Where(r => r.Id == stageId).Select(r => TableFields.U(r, 2)).FirstOrDefault();
            return type != 0 && state.Wishes.TryGetValue(type, out suit) ? suit : 0;
        }
    }
}
