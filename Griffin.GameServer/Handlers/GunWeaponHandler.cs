using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Database.Entities;
using Griffin.Database.Repositories;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using static Griffin.Common.Proto.DarkZoneBuff.Types;

namespace Griffin.GameServer.Handlers
{
    public sealed class GunWeaponHandler(
        IInventoryService inventoryService,
        IWeaponRepository weaponRepository,
        IWeaponProgressService progress,
        IWeaponModService weaponMods,
        IModProgressService modProgress,
        IAccountStateService accountState) : GunWeaponHandlerBase
    {
        // Mod polarization and the skin shop for weapons are not simulated: the answers are the client's own refusals.
        public override void HandleGunWeaponModPolarization(CS_GunWeaponModPolarization request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsGunWeaponModPolarization, HNINDAOJCKP.GunWeaponModCanNotPolarity);

        public override void HandleGunWeaponModContinuouslyPolarization(CS_GunWeaponModContinuouslyPolarization request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsGunWeaponModContinuouslyPolarization, HNINDAOJCKP.GunWeaponModContinuousLyPolarityCanNotStart);

        public override void HandleBuyGunWeaponSkin(CS_BuyGunWeaponSkin request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsBuyGunWeaponSkin, HNINDAOJCKP.WeaponSkinHasNoRequiredItem);

        public override void HandleBuyGunWeaponModSkin(CS_BuyGunWeaponModSkin request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsBuyGunWeaponModSkin, HNINDAOJCKP.WeaponModSkinHasNoRequiredItem);

        // ---- mod lock plans: saved selections of mods to keep locked, per plan group and slot (stored in mod.lockplans)
        private sealed class LockSlot
        {
            public List<uint> Mods { get; set; } = [];
            public uint Flag { get; set; }
        }

        private static SC_SetGunWeaponModLockPlan PlanReply(uint group, Dictionary<uint, LockSlot> slots)
        {
            SimCombatMythicInfo info = new() { BEFHELAINLN = group };
            foreach ((uint slot, LockSlot value) in slots)
            {
                SimCombatMythicInfo.Types.DCDLLEBGAHH entry = new() { IFKCJEAGCFH = value.Flag };
                entry.CEAEBKMHDCJ.AddRange(value.Mods);
                info.LCPMEOFCFPB[slot] = entry;
            }

            return new SC_SetGunWeaponModLockPlan { CLMMDIGBOJC = info };
        }

        public override void HandleSetGunWeaponModLockPlan(CS_SetGunWeaponModLockPlan request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Dictionary<uint, Dictionary<uint, LockSlot>> plans = accountState.Get<Dictionary<uint, Dictionary<uint, LockSlot>>>(uid, "mod.lockplans") ?? [];
            if (!plans.TryGetValue(request.BEFHELAINLN, out Dictionary<uint, LockSlot>? slots))
                plans[request.BEFHELAINLN] = slots = [];

            slots[request.ODMAMCEEMBD] = new LockSlot { Mods = request.CEAEBKMHDCJ.ToList(), Flag = request.IFKCJEAGCFH };
            accountState.Set(uid, "mod.lockplans", plans);
            connection.Send(PlanReply(request.BEFHELAINLN, slots));
        }

        public override void HandleDelGunWeaponModLockPlan(CS_DelGunWeaponModLockPlan request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Dictionary<uint, Dictionary<uint, LockSlot>> plans = accountState.Get<Dictionary<uint, Dictionary<uint, LockSlot>>>(uid, "mod.lockplans") ?? [];
            if (!plans.TryGetValue(request.BEFHELAINLN, out Dictionary<uint, LockSlot>? slots) || !slots.Remove(request.ODMAMCEEMBD))
            {
                connection.Refuse(MsgId.MsgCsDelGunWeaponModLockPlan, HNINDAOJCKP.GunWeaponModLockPlanIndexNotFound);
                return;
            }

            accountState.Set(uid, "mod.lockplans", plans);
            connection.Send(new SC_DelGunWeaponModLockPlan { CLMMDIGBOJC = PlanReply(request.BEFHELAINLN, slots).CLMMDIGBOJC });
        }

        /// <summary>Unlocks the given mods (clears the lock flag of each mod that is owned).</summary>
        public override void HandleUnlockGunWeaponModLockPlan(CS_UnlockGunWeaponModLockPlan request, Connection connection)
        {
            if (connection.Account is null)
                return;

            SC_UnlockGunWeaponModLockPlan response = new() { Locked = false };
            foreach (WeaponModEntity mod in inventoryService.GetPlayerInventory<WeaponModEntity>(connection.Account.Uid).Where(m => request.JNADCNCACDA.Contains(m.Id)))
            {
                mod.Field5 = 0;
                response.Ids.Add(mod.Id);
            }

            if (response.Ids.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsUnlockGunWeaponModLockPlan, HNINDAOJCKP.GunWeaponModNotFound);
                return;
            }

            weaponMods.Save();
            connection.Send(response);
        }

        public override void HandleGunWeaponModLvUp(CS_GunWeaponModLvUp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<Google.Protobuf.IMessage> messages = [];
            ModLevelResult? result = modProgress.LevelUp(connection.Account.Uid, (uint)request.Id, request.Materials.Select(m => (uint)m),
                request.Items.ToDictionary(p => p.Key, p => p.Value), messages);
            if (result is null)
            {
                connection.Refuse(MsgId.MsgCsGunWeaponModLvUp, modProgress.LastError);
                return;
            }

            messages.Add(new SC_GunWeaponModLvUp { Id = result.Id, Level = result.Level, Exp = result.Exp });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleGunWeaponModDismantle(CS_GunWeaponModDismantle request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<Google.Protobuf.IMessage> messages = [];
            IReadOnlyList<uint> removed = modProgress.Dismantle(connection.Account.Uid, request.Ids.Select(i => (uint)i), messages);
            if (removed.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsGunWeaponModDismantle, HNINDAOJCKP.GunWeaponModIsNotFree);
                return;
            }

            SC_GunWeaponModDismantle response = new();
            response.Ids.AddRange(removed.Select(i => (ulong)i));
            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>Toggles the lock of a mod (stored in <c>Field5</c>: 0 free, 1 locked); the answer carries the new state.</summary>
        public override void HandleGunWeaponModLockUnlock(CS_GunWeaponModLockUnlock request, Connection connection)
        {
            if (connection.Account is null)
                return;

            WeaponModEntity? mod = inventoryService.GetPlayerInventory<WeaponModEntity>(connection.Account.Uid).FirstOrDefault(m => m.Id == request.Id);
            if (mod is null)
            {
                connection.Refuse(MsgId.MsgCsGunWeaponModLockUnlock, HNINDAOJCKP.GunWeaponModNotFound);
                return;
            }

            mod.Field5 = mod.Field5 == 0 ? 1u : 0u;
            weaponMods.Save();
            connection.Send(new SC_GunWeaponModLockUnlock { Id = request.Id, Locked = mod.Field5 != 0 });
        }

        public override void HandleGunWeaponLvUp(CS_GunWeaponLvUp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            WeaponLevelResult? result = progress.LevelUp(connection.Account.Uid, request.Id, request.Level);
            if (result is null)
            {
                connection.Refuse(MsgId.MsgCsGunWeaponLvUp, progress.LastError);
                return;
            }

            List<Google.Protobuf.IMessage> messages = [new SC_GunWeaponUp { Id = result.WeaponUid, Level = result.Level, Exp = result.Exp }];
            messages.AddRange(result.Updates);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleGunWeaponLockUnlock(CS_GunWeaponLockUnlock request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (progress.ToggleLock(connection.Account.Uid, request.Id) is { } locked)
                connection.Send(new SC_GunWeaponLockUnlock { Id = request.Id, Locked = locked });
            else
                connection.Refuse(MsgId.MsgCsGunWeaponLockUnlock, HNINDAOJCKP.GunWeaponNotFound);
        }

        public override void HandleGunWeaponDismantle(CS_GunWeaponDismantle request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<Google.Protobuf.IMessage> messages = [];
            IReadOnlyList<uint> removed = progress.Dismantle(connection.Account.Uid, request.Uids, messages);
            if (removed.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsGunWeaponDismantle, HNINDAOJCKP.GunWeaponLocked);
                return;
            }

            SC_GunWeaponDismantle response = new();
            response.Uids.AddRange(removed);
            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleGunWeaponBreak(CS_GunWeaponBreak request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (progress.Break(connection.Account.Uid, request.Id, request.MaterialUid) is { } times)
                connection.Send(new SC_GunWeaponBreak { Id = request.Id, BreakTimes = times });
            else
                connection.Refuse(MsgId.MsgCsGunWeaponBreak, progress.LastError);
        }

        public override void HandleGunWeapons(CS_GunWeapons request, Connection connection)
        {
            if (connection.Account == null) return;

            SC_GunWeapons scGunWeapons = new SC_GunWeapons();

            foreach (WeaponEntity weapon in inventoryService.GetPlayerInventory<WeaponEntity>(connection.Account.Uid))
            {
                scGunWeapons.Weapons.Add(weapon.ToProtoWeapon());

                if (weapon.GunId != 0)
                    scGunWeapons.FEDCFGGDNBN[weapon.Id] = weapon.GunId;
            }

            connection.SendAutoEncrypted(scGunWeapons);
        }

        public override void HandleGetGunWeaponModLockPlan(CS_GetGunWeaponModLockPlan request, Connection connection)
        {
                (uint Index, uint[] Slots, uint PlanField2)[] plans =
                {
                    (0, new uint[] { 1, 2, 9 }, 2),
                    (1, new uint[] { 1, 2, 4 }, 2),
                    (2, new uint[] { 1, 2, 9 }, 2),
                    (3, new uint[] { 1, 2, 10 }, 3),
                    (4, new uint[] { 1, 2, 4 }, 2),
                    (5, new uint[] { 1, 2, 4 }, 2),
                    (6, new uint[] { 1, 2, 4 }, 2),
                    (7, new uint[] { 1, 2, 10 }, 3),
                    (8, new uint[] { 1, 2, 10 }, 3),
                    (9, new uint[] { 1, 2, 10 }, 3),
                    (10, new uint[] { 1, 2, 10 }, 3),
                    (11, new uint[] { 1, 2, 10 }, 3),
                    (12, new uint[] { 1, 2, 10 }, 3),
                    (13, new uint[] { 1, 2, 9 }, 3),
                    (14, new uint[] { 1, 2, 9 }, 3),
                    (15, new uint[] { 1, 2, 9 }, 3),
                    (16, new uint[] { 1, 2, 9 }, 3),
                };
                SC_GetGunWeaponModLockPlan response = new SC_GetGunWeaponModLockPlan
                {
                    DEOAAPJBIGP = new FJKJEJJPNLI
                    {
                        KJDFHNBFOMB = false,
                    },
                };
                foreach ((uint Index, uint[] Slots, uint PlanField2) plan in plans)
                {
                    SimCombatMythicInfo entry = new SimCombatMythicInfo
                    {
                        BEFHELAINLN = plan.Index,
                    };
                    SimCombatMythicInfo.Types.DCDLLEBGAHH planValue = new SimCombatMythicInfo.Types.DCDLLEBGAHH
                    {
                        IFKCJEAGCFH = plan.PlanField2,
                    };
                    planValue.CEAEBKMHDCJ.AddRange(plan.Slots);
                    entry.LCPMEOFCFPB[1] = planValue;
                    response.DEOAAPJBIGP.PMDFJCLFFPB.Add(entry);
                }
                connection.Send(1, response);
        }

        public override void HandleGunWeaponMods(CS_GunWeaponMods request, Connection connection)
        {
            if (connection.Account == null) return;

            WeaponModEntity[] mods = inventoryService.GetPlayerInventory<WeaponModEntity>(connection.Account.Uid);
            SC_GunWeaponMods response = new SC_GunWeaponMods();
            foreach (WeaponModEntity mod in mods)
                response.Mods.Add(mod.ToProtoWeaponMod());

            connection.SendAutoEncrypted(response);
        }

        public override void HandleGunWeaponSkinItems(CS_GunWeaponSkinItems request, Connection connection)
        {
            if (connection.Account == null)
            {
                connection.Send(new SC_GunWeaponSkinItems { PGMKLGCFAOF = { } });
                return;
            }

            SC_GunWeaponSkinItems response = new SC_GunWeaponSkinItems();
            foreach (WeaponSkinEntity weaponSkin in inventoryService.GetPlayerInventory<WeaponSkinEntity>(connection.Account.Uid))
                response.PGMKLGCFAOF[weaponSkin.WeaponSkinId] = 1;

            connection.Send(response);
        }

        public override void HandleGunWeaponModGetContinuouslyPolarization(CS_GunWeaponModGetContinuouslyPolarization request, Connection connection)
        {
                connection.Send(new SC_GunWeaponModGetContinuouslyPolarization
                {
                    NHJFCEMIPEO = new GEDLLJGMIED
                    {
                        AMKICOIHKPD = 0,
                        DDGMKANMOLN = 0,
                        GENGCMEPHFM = { },
                    },
                });
        }

        public override void HandleReplaceGunWeaponSkin(CS_ReplaceGunWeaponSkin request, Connection connection)
        {
            connection.Send(new SC_ReplaceGunWeaponSkin
            {
                Id = request.Id,
                GunId = request.GunId,
            });
        }

        /// <summary>Switches "show the weapon skin" (<c>skin_use</c>, bit 2 of the weapon flags, next to lock = bit 0 and red dot = bit 1) and keeps it.</summary>
        public override void HandleUseGunWeaponSkin(CS_UseGunWeaponSkin request, Connection connection)
        {
            if (connection.Account is null)
                return;

            WeaponEntity? weapon = inventoryService.GetPlayerInventory<WeaponEntity>(connection.Account.Uid).FirstOrDefault(w => w.Id == request.Id);
            if (weapon is null)
            {
                connection.Refuse(MsgId.MsgCsUseGunWeaponSkin, HNINDAOJCKP.GunWeaponNotFound);
                return;
            }

            weapon.Flags = request.PMKFBMGBDNP ? weapon.Flags | SkinUseFlag : weapon.Flags & ~SkinUseFlag;
            weaponRepository.SaveChanges();
            connection.Send(new SC_UseGunWeaponSkin
            {
                Id = request.Id,
                PMKFBMGBDNP = request.PMKFBMGBDNP,
            });
        }

        private const uint SkinUseFlag = 1u << 2;

        /// <summary>
        /// Puts a mod (accessory) in a slot of a weapon and keeps it (<c>WeaponEntity.EquippedModIds</c>, index = slot - 1; the weapon list
        /// reports them in <c>weapon_mods</c>). The mod leaves its previous weapon and slot and a mod already in the target slot is
        /// taken out, each announced to the client as <c>SC_GunWeaponModBelong</c> with weapon 0 and the slot it left. A request with
        /// weapon 0 takes the mod off. The client also clears a slot by sending its weapon/slot with mod id 0.
        /// ASSUMPTION: slots are 1..8 and capacity or mod type are not checked.
        /// </summary>
        public override void HandleGunWeaponModBelong(CS_GunWeaponModBelong request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            WeaponEntity[] weapons = inventoryService.GetPlayerInventory<WeaponEntity>(uid);
            if (request.WeaponId != 0 && request.WeaponModId == 0)
            {
                WeaponEntity? target = weapons.FirstOrDefault(w => w.Id == request.WeaponId);
                if (target is null)
                {
                    connection.Refuse(MsgId.MsgCsGunWeaponModBelong, HNINDAOJCKP.GunWeaponNotFound);
                    return;
                }
                if (request.Slot is < 1 or > 8)
                {
                    connection.Refuse(MsgId.MsgCsGunWeaponModBelong, HNINDAOJCKP.GunWeaponModNotMatchWithSlot);
                    return;
                }

                int emptyIndex = (int)request.Slot - 1;
                if (emptyIndex < target.EquippedModIds.Length)
                {
                    Unequip(target, emptyIndex);
                    weaponRepository.SaveChanges();
                }
                connection.Send(new SC_GunWeaponModBelong { WeaponId = target.Id, Slot = request.Slot, WeaponModId = 0 });
                return;
            }

            if (inventoryService.GetPlayerInventory<WeaponModEntity>(uid).All(m => m.Id != request.WeaponModId))
            {
                connection.Refuse(MsgId.MsgCsGunWeaponModBelong, HNINDAOJCKP.GunWeaponModNotFound);
                return;
            }

            List<SC_GunWeaponModBelong> messages = [];
            (WeaponEntity Weapon, int Index)? holder = weapons
                .Select(w => (Weapon: w, Index: Array.IndexOf(w.EquippedModIds, request.WeaponModId)))
                .Where(h => h.Index >= 0).Select(h => ((WeaponEntity, int)?)h).FirstOrDefault();

            if (request.WeaponId == 0)
            {
                if (holder is null)
                {
                    connection.Refuse(MsgId.MsgCsGunWeaponModBelong, HNINDAOJCKP.GunWeaponModNotEquip);
                    return;
                }

                Unequip(holder.Value.Weapon, holder.Value.Index);
                messages.Add(new SC_GunWeaponModBelong { WeaponId = 0, Slot = (uint)holder.Value.Index + 1, WeaponModId = request.WeaponModId });
            }
            else
            {
                WeaponEntity? weapon = weapons.FirstOrDefault(w => w.Id == request.WeaponId);
                if (weapon is null)
                {
                    connection.Refuse(MsgId.MsgCsGunWeaponModBelong, HNINDAOJCKP.GunWeaponNotFound);
                    return;
                }

                if (request.Slot is < 1 or > 8)
                {
                    connection.Refuse(MsgId.MsgCsGunWeaponModBelong, HNINDAOJCKP.GunWeaponModNotMatchWithSlot);
                    return;
                }

                int slot = (int)request.Slot - 1;
                if (holder is { } current && !(current.Weapon.Id == weapon.Id && current.Index == slot))
                {
                    Unequip(current.Weapon, current.Index);
                    messages.Add(new SC_GunWeaponModBelong { WeaponId = 0, Slot = (uint)current.Index + 1, WeaponModId = request.WeaponModId });
                }

                uint occupant = weapon.EquippedModIds.Length > slot ? weapon.EquippedModIds[slot] : 0;
                if (occupant != 0 && occupant != request.WeaponModId)
                {
                    Unequip(weapon, slot);
                    messages.Add(new SC_GunWeaponModBelong { WeaponId = 0, Slot = request.Slot, WeaponModId = occupant });
                }

                uint[] equipped = new uint[Math.Max(weapon.EquippedModIds.Length, slot + 1)];
                weapon.EquippedModIds.CopyTo(equipped, 0);
                equipped[slot] = request.WeaponModId;
                weapon.EquippedModIds = equipped;
                messages.Add(new SC_GunWeaponModBelong { WeaponId = weapon.Id, Slot = request.Slot, WeaponModId = request.WeaponModId });
            }

            weaponRepository.SaveChanges();
            connection.SendAutoEncrypted(messages.Cast<Google.Protobuf.IMessage>().ToArray());
        }

        private static void Unequip(WeaponEntity weapon, int index)
        {
            uint[] equipped = (uint[])weapon.EquippedModIds.Clone();
            equipped[index] = 0;
            weapon.EquippedModIds = equipped;
        }

        public override void HandleUnlockGunWeaponModSkin(CS_UnlockGunWeaponModSkin request, Connection connection)
        {
            connection.Send(new SC_UnlockGunWeaponModSkin
            {
                GunId = request.GunId,
            });
        }

        public override void HandleGunWeaponRedDotClear(CS_GunWeaponRedDotClear request, Connection connection)
        {
            connection.Send(new SC_GunWeaponRedDotClear
            {
                KNGOGHKBHBE = { request.KNGOGHKBHBE },
            });
        }

        public override void HandleGunWeaponBelong(CS_GunWeaponBelong request, Connection connection)
        {
            if (connection.Account == null) return;

            WeaponEntity[] weapons = inventoryService.GetPlayerInventory<WeaponEntity>(connection.Account.Uid);
            GunEntity[] guns = inventoryService.GetPlayerInventory<GunEntity>(connection.Account.Uid);

            GunEntity? gun = guns.FirstOrDefault(g => g.GunId == request.GunId);
            WeaponEntity? previous = gun is null ? null : weapons.FirstOrDefault(w => w.Id == gun.WeaponId);
            WeaponEntity? weapon = weapons.FirstOrDefault(w => w.Id == request.Id);
            if (gun is null || previous is null || weapon is null)
            {
                connection.Refuse(MsgId.MsgCsGunWeaponBelong, gun is null ? HNINDAOJCKP.GunNotFound : HNINDAOJCKP.GunWeaponNotFound);
                return;
            }

            if (previous.Id == weapon.Id)
            {
                connection.Send(new SC_GunWeaponBelong
                {
                    Id = request.Id,
                    GunId = request.GunId,
                });
                return;
            }

            if (weapon.GunId != 0 && weapon.GunId != request.GunId)
            {
                GunEntity otherGun = guns.First(g => g.GunId == weapon.GunId);
                otherGun.WeaponId = previous.Id;
                previous.GunId = otherGun.GunId;
            }
            else
            {
                previous.GunId = 0;
            }

            weapon.GunId = request.GunId;
            gun.WeaponId = request.Id;
            weaponRepository.SaveChanges();

            connection.Send(
                new SC_GunWeaponBelong { Id = previous.Id, GunId = previous.GunId },
                new SC_GunWeaponBelong { Id = weapon.Id, GunId = weapon.GunId });
        }

    }
}
