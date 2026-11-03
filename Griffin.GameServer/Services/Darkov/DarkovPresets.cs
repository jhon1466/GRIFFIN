namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>The preset characters of the tutorial stages (<c>DarkovPresetCharacter</c>): what the hero carries when the room does not use the account's loadout.</summary>
    public static class DarkovPresets
    {
        public static DarkovProfile Create(uint preset)
        {
            var profile = new DarkovProfile { Bag = [], Equip = [null, null, null, null], Ammo = [], QuickSlots = new uint[6] };
            profile.Normalize(preset == 1009002 ? 28 : 20);
            if (!RawTables.ById("DarkovPresetCharacterData").TryGetValue(preset, out PbMsg? row))
                return profile;
            var items = RawTables.ById("EsItemData");
            DItem Make(uint cfg, uint count)
            {
                var item = new DItem { Uid = profile.NewUid(), Cfg = cfg, Count = count };
                if (items.TryGetValue(cfg, out PbMsg? info) && info.U(13) > 0)
                    item.DurCur = item.DurMax = (uint)info.U(13);
                return item;
            }

            // equipment: armour (9), weapon (10) and the third piece (11) of the preset
            uint[] gear = [(uint)row.U(9), (uint)row.U(10), (uint)row.U(11)];
            for (int i = 0; i < gear.Length; i++)
                if (gear[i] != 0)
                    profile.Equip[i] = Make(gear[i], 1);
            uint ammo = (uint)row.U(17);
            if (ammo != 0)
            {
                profile.Bag[0] = Make(ammo, 90);
                profile.Bag[1] = Make(ammo, 90);
                profile.Bag[2] = Make(ammo, 90);
                profile.Bag[3] = Make(3130201, 5);
            }

            return profile;
        }
    }
}
