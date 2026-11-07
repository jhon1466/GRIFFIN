namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The affinity-key slots of each doll (契合键 equipped from the affinity panel). The client keeps them apart from the three shared
    /// key slots: Gun fields 59 (key ids) and 60 (key uids), filled by SC_GunLoveTalentSkillItemBelong. They were never stored nor sent,
    /// so an equipped affinity key was gone after a relaunch.
    /// </summary>
    public static class LoveKeySlots
    {
        public const string StateKey = "love.keys";
        public const int SlotCount = 3;

        public sealed class Slots
        {
            public uint[] Stc { get; set; } = new uint[SlotCount];
            public ulong[] Uid { get; set; } = new ulong[SlotCount];
        }

        public static Dictionary<string, Slots> All(IAccountStateService state, uint uid) =>
            state.Get<Dictionary<string, Slots>>(uid, StateKey) ?? [];

        public static Slots Of(IAccountStateService state, uint uid, uint gunId)
        {
            Slots slots = All(state, uid).GetValueOrDefault(gunId.ToString()) ?? new Slots();
            if (slots.Stc.Length < SlotCount) slots.Stc = [.. slots.Stc, .. new uint[SlotCount - slots.Stc.Length]];
            if (slots.Uid.Length < SlotCount) slots.Uid = [.. slots.Uid, .. new ulong[SlotCount - slots.Uid.Length]];
            return slots;
        }

        /// <summary>The doll that wears the key, 0 when none.</summary>
        public static uint HolderOf(IAccountStateService state, uint uid, ulong keyUid) =>
            keyUid == 0 ? 0 : All(state, uid).FirstOrDefault(p => p.Value.Uid.Contains(keyUid)) is { Key: { } gun } && uint.TryParse(gun, out uint id) ? id : 0;

        public static void Save(IAccountStateService state, uint uid, Dictionary<string, Slots> all) => state.Set(uid, StateKey, all);
    }
}
