using Griffin.Common.Proto;
using Griffin.Database.Entities;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Extensions;

public static class ProtoExtensions
{
    public static Gun ToProtoGunCharacter(this GunEntity gun, IReadOnlyList<ulong>? pluginSlots = null, ulong foodBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(gun);

        var proto = new Gun
        {
            Id = gun.GunId,
            Timestamp = new DateTimeOffset(gun.TimeCreated).ToUnixTimeSeconds(),
            Level = (uint)gun.Level,
            GunClass = gun.GunClass,
            Costume = gun.CostumeId,
            CostumeParts = gun.CostumeParts,
            Grade = gun.Grade,
            Exp = gun.Exp,
            Energy = gun.Energy,
            AuthLevel = gun.AuthLevel,
              NGJCBNLMPHP = gun.SpTalentLevel,
            IsGetPublicTalentSkillItem = gun.IsGetPublicTalentSkillItem,
            IsAllTalnetUnlock = gun.IsAllTalentUnlock || Services.TalentBits.AllUnlocked(gun.GunId, gun.TalentTree.Keys),
            Preset = (GunPresetType)gun.Preset,
            PresetId = gun.PresetId,
            TalentResetNum = gun.TalentResetNum,
            WeaponId = gun.WeaponId,
            Love = new LoungeChatMessage
            {
                KFIAKLNJHMB = gun.LoveLevel,
                ABOJJOJODME = gun.LoveExp,
                // the vow (level 9): the client reads the flag (field 4) for the vow icon and its red dot, and the time (field 5) of the ceremony (A-73)
                APPPIAJDHLD = gun.LoveLevel >= Handlers.LoveHandler.VowLevel,
                PCPEAMOJPCF = gun.GunTalentConsume.GetValueOrDefault(Handlers.LoveHandler.VowTimeKey),
            },
        };

        // peak keys (峰位键) have their own slots in the middle of the wheel (field 52), not in the private ones (A-61)
        uint[] peakKeys = Services.TalentKeySlots.Normalize(gun);
        proto.NJKHKDCNJPK.AddRange(peakKeys);
        proto.HEENBCJCHFG = foodBytes;   // the points of the dishes given to the character (field 61, A-76)
        proto.PrivateTalentSkillItems.AddRange(gun.PrivateTalentSkillItems);
        proto.PublicTalentSkillItems.AddRange(gun.PublicTalentSkillItems);
        proto.PublicTalentSkillItemsUid.AddRange(gun.PublicTalentSkillItemsUid);

        foreach (KeyValuePair<ulong, uint> entry in gun.TalentTree)
        {
            proto.TalentTree[entry.Key] = entry.Value;
        }

        foreach (ulong keyPoint in Services.TalentBits.EffectivePoints(gun))
            if (!proto.TalentTree.ContainsKey(keyPoint))
                proto.TalentTree[keyPoint] = 1;

        proto.DKKBDFJPOOA.AddRange(Services.TalentBits.Words(Services.TalentBits.EffectivePoints(gun)));

        foreach (KeyValuePair<uint, uint> entry in gun.GunTalentConsume)
        {
            if (entry.Key < Services.TalentKeySlots.ReservedBase)
                proto.GunTalentConsume[entry.Key] = entry.Value;
        }

        // the remoulding core of the weapon panel (unlocked at level 55); MHBBHAHAIGD (56) = index of the equipped one
        if (Services.RemouldingCores.OwnedCore(gun) is var core and not 0)
        {
            // the client reads field 1 as the LEVEL and field 3 as the CORE ID (the proto names idx/core_level are swapped)
            GrassGroup group = new() { Idx = Services.RemouldingCores.LevelOf(gun), CoreLevel = core };
            if (pluginSlots is not null)
                group.PluginUids.AddRange(pluginSlots);
            proto.GrassGroup.Add(group);
        }

        return proto;
    }

    public static GunWeaponLite ToProtoWeapon(this WeaponEntity weapon, IReadOnlyList<uint>? modSkins = null)
    {
        ArgumentNullException.ThrowIfNull(weapon);

        var proto = new GunWeaponLite
        {
            Id = weapon.Id,
            StcId = weapon.WeaponId,
            Level = (uint)weapon.Level,
            Exp = weapon.CurExp,
            // field 5 of the weapon is its equipped SKIN in client 4.0.5136 (ECOCKEKCCAF.ELLCBFHEEJO, set by the replace-skin reply); the owner gun travels in the map of SC_GunWeapons (A-88)
            GunId = weapon.EquippedSkinId,
            BreakTimes = (uint)weapon.BreakTimes,
            LPEEABKNKKE = weapon.Flags,
        };

        // The mods are POSITIONAL (index = slot - 1; the client reads them with its slot index and pads the list) and field 2 of each entry is the SKIN
        // of the accessory in that slot (ECOCKEKCCAF.LGBBHFCNAJD.ELLCBFHEEJO, set by SC_ReplaceGunWeaponModSkin; the old proto called it gun_id, A-89).
        int slots = Math.Max(weapon.EquippedModIds?.Length ?? 0, modSkins?.Count ?? 0);
        for (int slot = 0; slot < slots; slot++)
        {
            uint modId = weapon.EquippedModIds is not null && slot < weapon.EquippedModIds.Length ? weapon.EquippedModIds[slot] : 0;
            uint skin = modSkins is not null && slot < modSkins.Count ? modSkins[slot] : 0;
            if (modId == 0 && skin == 0 && slot >= slots - 1 && proto.WeaponMods.Count == 0)
                continue;
            proto.WeaponMods.Add(new GunWeaponLite.Types.WeaponMod { Id = modId, GunId = skin });
        }

        while (proto.WeaponMods.Count > 0 && proto.WeaponMods[^1].Id == 0 && proto.WeaponMods[^1].GunId == 0)
            proto.WeaponMods.RemoveAt(proto.WeaponMods.Count - 1);

        return proto;
    }

    public static GunWeaponMod ToProtoWeaponMod(this WeaponModEntity weaponMod, ModRefineState? refine = null, uint power = 0)
    {
        ArgumentNullException.ThrowIfNull(weaponMod);

        return new GunWeaponMod
        {
            Id = weaponMod.Id,
            StcId = weaponMod.WeaponModId,
            // The names of this message come from an older build; the 4.0.5136 client (NHHPFBAEDAG) reads field 3 as the AFFIXES of the mod (four 16-bit
            // pairs), field 5 as its polarity (non-zero = polarized), field 6 as flag bits (bit 0 = locked) and field 7 as the affixes of the polarization.
            // The level and the experience are not in this message: they travel in SC_GunWeaponModLvUp (A-74).
            Locked = weaponMod.Field7,
            ModSuitPowerId = power,
            // field 5 = refinement steps bought, field 7 = polarity bits of the affixes (A-78)
            Level = refine?.Count ?? 0,
            Exp = weaponMod.Field5 != 0 ? 1u : 0u,
            Suit = refine?.Polarity ?? 0
        };
    }
}
