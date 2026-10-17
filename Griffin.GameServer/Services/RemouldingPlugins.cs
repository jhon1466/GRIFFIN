using Griffin.Database.Entities;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The plugins ("显像" chips) placed in the six slots of the remoulding core of each character. The client keeps them in
    /// <c>Gun.grass_group[].plugin_uids</c> (one uid per slot, 0 = empty) and in <c>GunRemouldingPlugin.gun_id</c> of each plugin, and sends
    /// <c>CS_GunRemouldingPluginBelong</c> (one slot; slot is 1-based, slot 0 + uid takes that plugin off, uid 0 empties the slot) and
    /// <c>CS_GunRemouldingPluginBatchBelong</c> (all six slots; an empty list takes everything off). Until now these requests were echoed
    /// and nothing was saved, so every plugin came off at the next login. ASSUMPTION A-58: a plugin sits on one slot of one character
    /// at a time; the plugin list itself is the fixed list of <c>GunHandler.PluginCatalog</c>.
    /// </summary>
    public static class RemouldingPlugins
    {
        public const int SlotCount = 6;

        private const string StateKey = "remoulding.plugins";

        private static Dictionary<string, ulong[]> Load(IAccountStateService state, uint uid) =>
            state.Get<Dictionary<string, ulong[]>>(uid, StateKey) ?? [];

        /// <summary>The six slots of the character's core (0 = empty).</summary>
        public static ulong[] SlotsOf(IAccountStateService state, uint uid, uint gunId)
        {
            ulong[] slots = new ulong[SlotCount];
            if (Load(state, uid).TryGetValue(gunId.ToString(), out ulong[]? saved))
                Array.Copy(saved, slots, Math.Min(saved.Length, SlotCount));
            return slots;
        }

        /// <summary>The character a plugin is on (0 = free).</summary>
        public static uint GunOf(IAccountStateService state, uint uid, ulong plugin)
        {
            foreach ((string gun, ulong[] slots) in Load(state, uid))
                if (slots.Contains(plugin) && uint.TryParse(gun, out uint id))
                    return id;
            return 0;
        }

        /// <summary>Puts the plugins of <paramref name="slots"/> on the character; a plugin that was on another place is taken from it.</summary>
        public static ulong[] Set(IAccountStateService state, uint uid, uint gunId, IReadOnlyList<ulong> slots)
        {
            Dictionary<string, ulong[]> all = Load(state, uid);
            ulong[] mine = new ulong[SlotCount];
            for (int i = 0; i < SlotCount && i < slots.Count; i++)
                mine[i] = slots[i];

            // the same plugin never appears twice
            HashSet<ulong> seen = [];
            for (int i = 0; i < SlotCount; i++)
                if (mine[i] != 0 && !seen.Add(mine[i]))
                    mine[i] = 0;

            foreach ((string gun, ulong[] other) in all.ToList())
            {
                if (gun == gunId.ToString())
                    continue;
                for (int i = 0; i < other.Length; i++)
                    if (other[i] != 0 && seen.Contains(other[i]))
                        other[i] = 0;
            }

            all[gunId.ToString()] = mine;
            state.Set(uid, StateKey, all);
            return mine;
        }

        /// <summary>The ids of the plugins the account owns (the catalog), used to validate requests.</summary>
        public static bool Owns(IReadOnlySet<ulong> catalog, ulong plugin) => plugin == 0 || catalog.Contains(plugin);

        /// <summary>The character can hold plugins only when it owns its core (level 55).</summary>
        public static bool HasCore(GunEntity gun) => RemouldingCores.OwnedCore(gun) != 0;
    }
}
