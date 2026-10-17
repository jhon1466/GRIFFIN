using Griffin.Common.Proto;
using Griffin.Database.Entities;

namespace Griffin.GameServer.Extensions;

public static class ProtoExtensions
{
    public static Gun ToProtoGunCharacter(this GunEntity gun)
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
            },
        };

        proto.PrivateTalentSkillItems.AddRange(gun.PrivateTalentSkillItems);
        proto.PublicTalentSkillItems.AddRange(gun.PublicTalentSkillItems);
        proto.PublicTalentSkillItemsUid.AddRange(gun.PublicTalentSkillItemsUid);

        foreach (KeyValuePair<ulong, uint> entry in gun.TalentTree)
        {
            proto.TalentTree[entry.Key] = entry.Value;
        }

        proto.DKKBDFJPOOA.AddRange(Services.TalentBits.Words(gun.TalentTree.Keys));

        foreach (KeyValuePair<uint, uint> entry in gun.GunTalentConsume)
        {
            if (entry.Key != Services.RemouldingCores.StorageKey)
                proto.GunTalentConsume[entry.Key] = entry.Value;
        }

        // the remoulding core of the weapon panel (unlocked at level 55); MHBBHAHAIGD (56) = index of the equipped one
        if (Services.RemouldingCores.OwnedCore(gun) is var core and not 0)
            // the client reads field 1 as the LEVEL and field 3 as the CORE ID (the proto names idx/core_level are swapped)
            proto.GrassGroup.Add(new GrassGroup { Idx = Services.RemouldingCores.LevelOf(gun), CoreLevel = core });

        return proto;
    }

    public static GunWeaponLite ToProtoWeapon(this WeaponEntity weapon)
    {
        ArgumentNullException.ThrowIfNull(weapon);

        var proto = new GunWeaponLite
        {
            Id = weapon.Id,
            StcId = weapon.WeaponId,
            Level = (uint)weapon.Level,
            Exp = weapon.CurExp,
            GunId = weapon.GunId,
            BreakTimes = (uint)weapon.BreakTimes,
            LPEEABKNKKE = weapon.Flags,
        };

        if (weapon.EquippedModIds is { Length: > 0 })
        {
            foreach (uint modId in weapon.EquippedModIds.Where(id => id != 0))
            {
                proto.WeaponMods.Add(new GunWeaponLite.Types.WeaponMod
                {
                    Id = modId,
                    GunId = weapon.GunId,
                });
            }
        }

        return proto;
    }

    public static GunWeaponMod ToProtoWeaponMod(this WeaponModEntity weaponMod)
    {
        ArgumentNullException.ThrowIfNull(weaponMod);

        return new GunWeaponMod
        {
            Id = weaponMod.Id,
            StcId = weaponMod.WeaponModId,
            Level = weaponMod.Level,
            Locked = weaponMod.Field5,
            Exp = weaponMod.Field6,
            Suit = weaponMod.Field7
        };
    }
}
