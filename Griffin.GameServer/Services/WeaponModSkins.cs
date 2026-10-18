namespace Griffin.GameServer.Services
{
    /// <summary>The skins ("配件涂装") the player chose for the accessories of each weapon, by slot (index = slot - 1). Kept in the account state (A-89).</summary>
    public static class WeaponModSkins
    {
        public const string StateKey = "weapon.modskins";

        public static IReadOnlyList<uint> Of(IAccountStateService state, uint uid, uint weaponId) =>
            state.Get<Dictionary<string, List<uint>>>(uid, StateKey)?.GetValueOrDefault(weaponId.ToString()) ?? [];

        public static void Set(IAccountStateService state, uint uid, uint weaponId, int slot, uint skin)
        {
            Dictionary<string, List<uint>> all = state.Get<Dictionary<string, List<uint>>>(uid, StateKey) ?? [];
            List<uint> skins = all.GetValueOrDefault(weaponId.ToString()) ?? [];
            while (skins.Count <= slot)
                skins.Add(0);
            skins[slot] = skin;
            all[weaponId.ToString()] = skins;
            state.Set(uid, StateKey, all);
        }
    }
}
