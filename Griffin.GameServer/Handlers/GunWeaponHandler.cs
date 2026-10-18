using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
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
        IAccountStateService accountState,
        ITableService tableService,
        IItemUseService itemUse) : GunWeaponHandlerBase
    {
        private const uint GoldItem = 2;

        /// <summary>
        /// The only way the 4.0.5136 client levels a mod up (<c>CS_GunWeaponModLvUp</c> is never sent): fodder (mods in <c>materials</c>, upgrade items in
        /// <c>items</c>) raises the level as before and <c>FKBCLPBCILC</c> = N amplifies N affixes of a mod at its last level (A-78): it costs N times the
        /// amplification item and gold, rolls a polarity for N affixes, keeps a backup so that the player can redo or confirm it (<c>CS_WeaponModPolarityRollBack</c>)
        /// and answers with the mod after the level push.
        /// </summary>
        public override void HandleGunWeaponModPolarization(CS_GunWeaponModPolarization request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                WeaponModEntity? mod = inventoryService.GetPlayerInventory<WeaponModEntity>(uid).FirstOrDefault(m => m.Id == request.Id);
                if (mod is null)
                {
                    connection.Refuse(MsgId.MsgCsGunWeaponModPolarization, HNINDAOJCKP.GunWeaponModNotFound);
                    return;
                }

                void Refuse(HNINDAOJCKP error) => connection.Refuse(MsgId.MsgCsGunWeaponModPolarization, error);
                uint n = request.FKBCLPBCILC;
                uint rank = ModPolarity.Rank(tableService, mod.WeaponModId);
                if (!ModPolarity.CanPolarize(tableService, mod.WeaponModId))
                {
                    Refuse(HNINDAOJCKP.ThisRankModHaveNotPolarityItem);
                    return;
                }

                int affixCount = ModPolarity.AffixCount(mod.Field7);
                if (n > affixCount || affixCount == 0)
                {
                    Refuse(HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                // The panel's button "精调" (client screenshot: material 5000/5000, gold 20000): the fodder has to reach 1500 (rank 4) or 5000 (rank 5) experience, which costs
                // 3000 / 20000 gold (the community wiki: military 1500 + 3000, precision 5000 + 20000); N extra amplification items (N x 131002 + N x 8000 gold) are optional
                long need = rank >= 5 ? 5000 : 1500;
                long gold = rank >= 5 ? 20000 : 3000;
                (uint item, uint count) = ModPolarity.AmplifyItem(tableService);
                Dictionary<uint, int> extra = new() { [GoldItem] = (int)(gold + ModPolarity.AmplifyGold(tableService) * n) };
                if (n > 0)
                    extra[item] = (int)(count * n);
                List<Google.Protobuf.IMessage> messages = [];
                if (!modProgress.ConsumeFodder(uid, mod.Id, request.Materials, request.Items.ToDictionary(p => p.Key, p => p.Value), need, extra, messages))
                {
                    Refuse(modProgress.LastError);
                    return;
                }

                // every roll is a random polarity for all the affixes; with N amplification items the better of N + 1 rolls is kept; the previous state is kept for "redo"
                ModRefineState refine = ModPolarity.Get(accountState, uid, mod.Id);
                refine.HasBackup = true;
                refine.BackupPolarity = refine.Polarity;
                refine.BackupCount = refine.Count;
                ulong best = 0;
                long bestSum = -1;
                for (int roll = 0; roll <= n; roll++)
                {
                    ulong bits = ModPolarity.Roll(tableService, mod.WeaponModId, mod.Field7, affixCount, Random.Shared);
                    long sum = Enumerable.Range(0, 4).Sum(slot => (long)ModPolarity.PermilleOf(bits, slot));
                    if (sum > bestSum)
                    {
                        best = bits;
                        bestSum = sum;
                    }
                }

                refine.Polarity = best;
                ModPolarity.Set(accountState, uid, mod.Id, refine);
                weaponMods.Save();
                // the client builds a NEW mod object from the answer (level 1): the level push goes AFTER it (A-82)
                messages.Add(new SC_GunWeaponModPolarization { Id = mod.Id, Mod = ModPolarity.ToProto(tableService, accountState, uid, mod), FKBCLPBCILC = n });
                messages.Add(new SC_GunWeaponModLvUp { Id = mod.Id, Level = Math.Max(1, mod.Level), Exp = mod.Field6 });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        /// <summary>
        /// Continuous polarization (rank 5): every round of the request consumes its fodder and rolls one set of polarities for the mod; the polarity bits of every
        /// round come back and the player keeps one of them with <c>ContinuouslyPolarizationSelect</c> (an index, -1 = none). A polarized mod re-rolls the polarity of the
        /// affixes it already has, an unpolarized one polarizes the N affixes of the round. The experience a round needs is the one the mod is worth as a material (A-78).
        /// </summary>
        public override void HandleGunWeaponModContinuouslyPolarization(CS_GunWeaponModContinuouslyPolarization request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                WeaponModEntity? mod = inventoryService.GetPlayerInventory<WeaponModEntity>(uid).FirstOrDefault(m => m.Id == request.Id);
                if (mod is null)
                {
                    connection.Refuse(MsgId.MsgCsGunWeaponModContinuouslyPolarization, HNINDAOJCKP.GunWeaponModNotFound);
                    return;
                }

                void Refuse(HNINDAOJCKP error) => connection.Refuse(MsgId.MsgCsGunWeaponModContinuouslyPolarization, error);
                if (ModPolarity.Rank(tableService, mod.WeaponModId) < 5 || !ModPolarity.CanPolarize(tableService, mod.WeaponModId))
                {
                    Refuse(HNINDAOJCKP.GunWeaponModContinuousLyPolarityLowRank);
                    return;
                }

                if (request.JFMFOHAHOBL.Count == 0)
                {
                    Refuse(HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                ModRefineState refine = ModPolarity.Get(accountState, uid, mod.Id);
                bool polarized = ModPolarity.Polarized(refine.Polarity);
                (uint amplifyItem, uint amplifyCount) = ModPolarity.AmplifyItem(tableService);
                uint amplifyGold = ModPolarity.AmplifyGold(tableService);
                long minExp = ModPolarity.BaseExp(tableService, mod.WeaponModId);
                int affixCount = ModPolarity.AffixCount(mod.Field7);
                ulong rolledAffixes = polarized ? PolarizedAffixes(refine.Polarity, mod.Field7) : mod.Field7;
                List<Google.Protobuf.IMessage> messages = [];
                List<ulong> candidates = [];
                foreach (DDFNNIBCBND round in request.JFMFOHAHOBL)
                {
                    uint n = round.FKBCLPBCILC;
                    if ((!polarized && n == 0) || n > affixCount)
                    {
                        Refuse(HNINDAOJCKP.ClientInputInvalid);
                        return;
                    }

                    Dictionary<uint, int> extra = new() { [GoldItem] = (int)(minExp * modProgress.GoldPerExp() + amplifyGold * n) };
                    if (n > 0)
                        extra[amplifyItem] = (int)(amplifyCount * n);
                    if (!modProgress.ConsumeFodder(uid, mod.Id, round.Materials, round.Items.ToDictionary(p => p.Key, p => p.Value), minExp, extra, messages))
                    {
                        // rounds already paid stay paid; the refusal only ends the series
                        if (candidates.Count == 0)
                        {
                            Refuse(modProgress.LastError);
                            return;
                        }

                        break;
                    }

                    candidates.Add(ModPolarity.Roll(tableService, mod.WeaponModId, rolledAffixes, polarized ? ModPolarity.AffixCount(rolledAffixes) : (int)n, Random.Shared));
                }

                refine.Candidates = candidates;
                refine.CandidateBase = refine.Polarity;
                ModPolarity.Set(accountState, uid, mod.Id, refine);
                weaponMods.Save();
                SC_GunWeaponModContinuouslyPolarization response = new() { Id = mod.Id };
                response.GENGCMEPHFM.AddRange(candidates);
                messages.Add(response);
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        /// <summary>The affixes that carry polarity (their 16-bit slot of <paramref name="affixes"/>, the other slots empty) so that a re-roll keeps the same affixes.</summary>
        private static ulong PolarizedAffixes(ulong polarity, ulong affixes)
        {
            ulong mask = 0;
            for (int slot = 0; slot < 4; slot++)
                if (((polarity >> (slot * 16)) & 0xFFF) != 0)
                    mask |= 0xFFFFUL << (slot * 16);
            return affixes & mask;
        }

        public override void HandleGunWeaponModContinuouslyPolarizationSelect(CS_GunWeaponModContinuouslyPolarizationSelect request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                WeaponModEntity? mod = inventoryService.GetPlayerInventory<WeaponModEntity>(uid).FirstOrDefault(m => m.Id == request.Id);
                if (mod is null)
                {
                    connection.Refuse(MsgId.MsgCsGunWeaponModContinuouslyPolarizationSelect, HNINDAOJCKP.GunWeaponModNotFound);
                    return;
                }

                ModRefineState refine = ModPolarity.Get(accountState, uid, mod.Id);
                int index = request.LMLFNAACPPN;
                if (refine.Candidates.Count == 0 || index >= refine.Candidates.Count)
                {
                    connection.Refuse(MsgId.MsgCsGunWeaponModContinuouslyPolarizationSelect, HNINDAOJCKP.GunWeaponModContinuousLyPolarityCanNotSelect);
                    return;
                }

                if (index >= 0)
                    refine.Polarity = refine.Candidates[index];
                refine.Candidates = [];
                ModPolarity.Set(accountState, uid, mod.Id, refine);
                connection.SendAutoEncrypted<Google.Protobuf.IMessage>(
                [
                    new SC_GunWeaponModContinuouslyPolarizationSelect { Id = mod.Id, LMLFNAACPPN = index },
                    new SC_WeaponModAdvanceEnhance { Mod = ModPolarity.ToProto(tableService, accountState, uid, mod) },
                    new SC_GunWeaponModLvUp { Id = mod.Id, Level = Math.Max(1, mod.Level), Exp = mod.Field6 },
                ]);
            }
        }

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
                scGunWeapons.Weapons.Add(weapon.ToProtoWeapon(WeaponModSkins.Of(accountState, connection.Account.Uid, weapon.Id)));

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

            // mods created before the affixes existed get them here too (the list is what the client builds its panels from)
            WeaponModEntity[] mods = weaponMods.GetPlayerWeaponMods(connection.Account.Uid);
            SC_GunWeaponMods response = new SC_GunWeaponMods();
            foreach (WeaponModEntity mod in mods)
                response.Mods.Add(ModPolarity.ToProto(tableService, accountState, connection.Account.Uid, mod));

            // the client creates every mod at level 1 without experience (the list has no field for them) and takes the real ones from this push (A-74)
            List<Google.Protobuf.IMessage> batch = [response];
            foreach (WeaponModEntity mod in mods.Where(m => m.Level > 1 || m.Field6 > 0))
                batch.Add(new SC_GunWeaponModLvUp { Id = mod.Id, Level = Math.Max(1, mod.Level), Exp = mod.Field6 });
            connection.SendAutoEncrypted(batch.ToArray());
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

        /// <summary>The pending continuous polarization (a mod with unchosen candidates), or an empty state.</summary>
        public override void HandleGunWeaponModGetContinuouslyPolarization(CS_GunWeaponModGetContinuouslyPolarization request, Connection connection)
        {
            GEDLLJGMIED state = new();
            if (connection.Account is not null)
            {
                uint uid = connection.Account.Uid;
                foreach (WeaponModEntity mod in inventoryService.GetPlayerInventory<WeaponModEntity>(uid))
                {
                    ModRefineState refine = ModPolarity.Get(accountState, uid, mod.Id);
                    if (refine.Candidates.Count == 0)
                        continue;

                    state.AMKICOIHKPD = mod.Id;
                    state.DDGMKANMOLN = refine.CandidateBase;
                    state.GENGCMEPHFM.AddRange(refine.Candidates);
                    break;
                }
            }

            connection.Send(new SC_GunWeaponModGetContinuouslyPolarization { NHJFCEMIPEO = state });
        }

        public override void HandleReplaceGunWeaponSkin(CS_ReplaceGunWeaponSkin request, Connection connection)
        {
            // CS: weapon id + skin id (0 = none). The skin is kept on the weapon and travels in field 5 of its message (A-88).
            if (connection.Account is null)
                return;

            WeaponEntity? weapon = inventoryService.GetPlayerInventory<WeaponEntity>(connection.Account.Uid).FirstOrDefault(w => w.Id == request.Id);
            if (weapon is null)
            {
                connection.Refuse(MsgId.MsgCsReplaceGunWeaponSkin, HNINDAOJCKP.GunWeaponNotFound);
                return;
            }

            weapon.EquippedSkinId = request.GunId;
            weaponRepository.SaveChanges();
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
                if (request.Slot == 0)
                {
                    // "unequip everything" (the red button of the parts panel): the request carries no slot
                    List<SC_GunWeaponModBelong> cleared = [];
                    for (int index = 0; index < target.EquippedModIds.Length; index++)
                        if (target.EquippedModIds[index] != 0)
                        {
                            Unequip(target, index);
                            cleared.Add(new SC_GunWeaponModBelong { WeaponId = target.Id, Slot = (uint)index + 1, WeaponModId = 0 });
                        }

                    weaponRepository.SaveChanges();
                    if (cleared.Count > 0)
                        connection.SendAutoEncrypted(cleared.ToArray());
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
