using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Repositories;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using Griffin.PcapParser;
using System;

namespace Griffin.GameServer.Handlers
{
    public sealed class GunHandler(
        IInventoryService inventoryService,
        ICharacterProgressService progress,
        ITalentService talents,
        IGunService gunService,
        IAccountStateService accountState,
        ITableService tableService,
        IItemUseService itemUse,
        ILobbyCookingService cooking,
        IRewardService rewardService,
        IAchievementService achievements,
        IWeaponRepository weaponRepositoryForReset) : GunHandlerBase
    {
        /// <summary>
        /// The reset event (activity type 38, "重置详情"): the character goes back to level 1, class 1, without experience, keys and core, and the weapon to level 1 and
        /// break 1; the dialog of the client promises the return of what was invested, which is paid back here (A-84): the experience (resource 25) and gold of every level
        /// of <c>GunLevelExpData</c>, the materials of the classes passed (<c>GunClassData</c> col 7) and the Data Links and gold of the core (<c>LevelUpData</c>).
        /// </summary>
        public override void HandleActicityResetWeaponOrGun(CS_ActicityResetWeaponOrGun request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            GunEntity? gun = request.GunId == 0 ? null : gunService.GetPlayerGuns(uid).FirstOrDefault(g => g.GunId == request.GunId);
            WeaponEntity? weapon = request.WeaponId == 0 ? null : inventoryService.GetPlayerInventory<WeaponEntity>(uid).FirstOrDefault(w => w.Id == request.WeaponId);
            if (gun is null && weapon is null)
            {
                connection.Refuse(MsgId.MsgCsActicityResetWeaponOrGun, HNINDAOJCKP.GunNotFound);
                return;
            }

            List<Google.Protobuf.IMessage> messages = [];
            if (gun is not null)
            {
                Dictionary<uint, long> refund = [];
                foreach (GunLevelExpData row in tableService.GetTable<GunLevelExpData>().Where(r => r.Level > 1 && r.Level <= (uint)gun.Level))
                {
                    refund[ICharacterProgressService.ExpResource] = refund.GetValueOrDefault(ICharacterProgressService.ExpResource) + row.Exp;
                    refund[ICharacterProgressService.GoldResource] = refund.GetValueOrDefault(ICharacterProgressService.GoldResource) + row.Gold;
                }

                refund[ICharacterProgressService.ExpResource] = refund.GetValueOrDefault(ICharacterProgressService.ExpResource) + gun.Exp;
                GunData? data = tableService.GetTable<GunData>().FirstOrDefault(g => g.Id == gun.GunId);
                uint group = data is null ? 0 : TableFields.U(data, 35);
                for (uint gunClass = 1; gunClass < Math.Max(1u, gun.GunClass); gunClass++)
                {
                    GunClassData? classRow = tableService.GetTable<GunClassData>().FirstOrDefault(c => TableFields.U(c, 2) == group && TableFields.U(c, 3) == gunClass);
                    if (classRow is not null)
                        foreach ((uint item, uint count) in TableFields.Map(classRow, 7))
                            refund[item] = refund.GetValueOrDefault(item) + count;
                }

                uint coreLevel = RemouldingCores.LevelOf(gun);
                if (coreLevel > 1)
                {
                    (uint links, uint coreGold) = RemouldingCores.CostBetween(tableService, 1, coreLevel);
                    refund[49u] = refund.GetValueOrDefault(49u) + links;
                    refund[ICharacterProgressService.GoldResource] = refund.GetValueOrDefault(ICharacterProgressService.GoldResource) + coreGold;
                }

                foreach ((uint item, long count) in refund.Where(p => p.Value > 0))
                    rewardService.Grant(uid, item, (int)Math.Min(count, int.MaxValue), messages);

                gun.Level = 1;
                gun.Exp = 0;
                gun.GunClass = 1;
                RemouldingCores.SetLevel(gun, 1);
                gun.PrivateTalentSkillItems = [0, 0, 0];
                gun.PublicTalentSkillItems = [0, 0, 0];
                gun.IsAllTalentUnlock = false;
                gunService.Save();
            }

            if (weapon is not null)
            {
                weapon.Level = 1;
                weapon.CurExp = 0;
                weapon.BreakTimes = 1;
                weaponRepositoryForReset.SaveChanges();
            }

            messages.Add(new SC_ActicityResetWeaponOrGun { IKDMNOBMIFE = request.IKDMNOBMIFE, GunId = request.GunId, WeaponId = request.WeaponId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        // Remoulding plugins and cores: no plugin or core exists on the account (the list is empty), so acting on one is "not found".
        // A character has exactly one core (see RemouldingCores), so "belong" only confirms it.
        public override void HandleGunRemouldingCoreBelong(CS_GunRemouldingCoreBelong request, Connection connection)
        {
            if (connection.Account is null)
                return;

            GunEntity? gun = gunService.GetPlayerGuns(connection.Account.Uid).FirstOrDefault(g => g.GunId == request.GunId);
            if (gun is null || RemouldingCores.OwnedCore(gun) != request.KCMKNJMDAIE)
            {
                connection.Refuse(MsgId.MsgCsGunRemouldingCoreBelong, HNINDAOJCKP.GunRemouldingCoreNotEquipped);
                return;
            }

            connection.Send(new SC_GunRemouldingCoreBelong { KCMKNJMDAIE = request.KCMKNJMDAIE, GunId = request.GunId });
        }

        /// <summary>Raises the core of a character (A-54; it costs Data Links and gold since A-76). The client's field order (the proto names are swapped): core id, wanted level, character.</summary>
        public override void HandleGunRemouldingCoreLvUp(CS_GunRemouldingCoreLvUp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            lock (AccountLock.For(connection.Account.Uid))
            {
                GunEntity? gun = gunService.GetPlayerGuns(connection.Account.Uid).FirstOrDefault(g => g.GunId == request.GunId);
                if (gun is null || RemouldingCores.OwnedCore(gun) != request.CoreLevel)
                {
                    connection.Refuse(MsgId.MsgCsGunRemouldingCoreLvUp, HNINDAOJCKP.GunRemouldingCoreNotEquipped);
                    return;
                }

                uint level = RemouldingCores.LevelOf(gun);
                if (level >= RemouldingCores.MaxLevel)
                {
                    connection.Refuse(MsgId.MsgCsGunRemouldingCoreLvUp, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                // Cost (A-76; the totals match the community wiki: level 2 = 750 Sardis Gold + 30 Data Links, level 60 = 120 000 + 32 760): the
                // difference of the accumulated amounts of LevelUpData (column 2 = Data Links, item 49; column 6 = gold, item 2) between both levels.
                uint wanted = Math.Min(RemouldingCores.MaxLevel, request.Idx > level ? request.Idx : level + 1);
                Dictionary<uint, int> cost = [];
                (uint links, uint gold) = RemouldingCores.CostBetween(tableService, level, wanted);
                if (links > 0)
                    cost[49] = (int)links;
                if (gold > 0)
                    cost[2] = (int)gold;

                List<Google.Protobuf.IMessage> spent = [];
                if (cost.Count > 0 && !itemUse.Spend(connection.Account.Uid, cost, spent))
                {
                    connection.Refuse(MsgId.MsgCsGunRemouldingCoreLvUp, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                RemouldingCores.SetLevel(gun, wanted);
                gunService.Save();
                spent.Add(new SC_GunRemouldingCoreLvUp { CoreLevel = request.CoreLevel, Idx = RemouldingCores.LevelOf(gun), GunId = request.GunId });
                connection.SendAutoEncrypted(spent.ToArray());
            }
        }

        public override void HandleGunRemouldingPluginDecompose(CS_GunRemouldingPluginDecompose request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsGunRemouldingPluginDecompose, HNINDAOJCKP.GunRemouldingPluginNotFound);

        public override void HandleGunRemouldingPluginLockUnlock(CS_GunRemouldingPluginLockUnlock request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsGunRemouldingPluginLockUnlock, HNINDAOJCKP.GunRemouldingPluginNotFound);

        /// <summary>Unlocks one point of the character's talent tree (see <see cref="ITalentService"/>).</summary>
        public override void HandleGunTalentLvUp(CS_GunTalentLvUp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<Google.Protobuf.IMessage> messages = [];
            TalentUnlockResult? result = talents.Unlock(connection.Account.Uid, request.TalentId, request.GroupId, messages);
            if (result is null)
            {
                connection.Refuse(MsgId.MsgCsGunTalentLvUp, talents.LastError);
                return;
            }

            SC_GunTalentLvUp response = new() { TalentId = result.TalentId, GroupId = result.PointId, Level = result.Level };
            response.FKOIBNPGCME.AddRange(result.Bits);
            foreach ((uint item, uint count) in result.Cost)
                response.Consume[item] = count;

            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleGunTalentReset(CS_GunTalentReset request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<Google.Protobuf.IMessage> messages = [];
            if (!talents.Reset(connection.Account.Uid, request.TalentId, messages))
            {
                connection.Refuse(MsgId.MsgCsGunTalentReset, talents.LastError);
                return;
            }

            messages.Add(new SC_GunTalentReset { TalentId = request.TalentId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleGuns(CS_Guns request, Connection connection)
        {
            if (connection.Account == null) return;

            SC_Guns scGuns = new SC_Guns();

            foreach (GunEntity gun in inventoryService.GetPlayerInventory<GunEntity>(connection.Account.Uid))
            {
                scGuns.Guns.Add(gun.ToProtoGunCharacter(RemouldingPlugins.SlotsOf(accountState, connection.Account.Uid, gun.GunId), cooking.FoodBytes(connection.Account.Uid, gun.GunId)));

            }

            //uint[] chars = new uint[] { 1017, 1024, 1001, 1008 };
            //uint[] chars = new uint[] { 1032, 1063 };
            //scGuns.Guns.Add(CreateNewGunCharacter(1032, 20));
            //scGuns.Guns.Add(CreateNewGunCharacter(1063, 20));
            
            connection.SendAutoEncrypted(scGuns);
        }

        public override void HandleGunLevelUp(CS_GunLevelUp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            int before = gunService.GetPlayerGuns(uid).FirstOrDefault(g => g.GunId == request.GunId)?.Level ?? 0;
            IReadOnlyDictionary<uint, bool> achievementsBefore = achievements.Completed(uid);
            LevelUpResult? result = progress.LevelUp(connection.Account.Uid, request.GunId, request.Level);
            if (result is null)
            {
                connection.Refuse(MsgId.MsgCsGunLevelUp, progress.LastError);
                return;
            }

            SC_GunLevelUp levelUp = new() { GunId = result.GunId, Level = result.Level, Exp = result.Exp };

            // crossing the unlock level gives the remoulding core: the client creates it from this list (A-54)
            if (before < RemouldingCores.UnlockLevel && result.Level >= RemouldingCores.UnlockLevel && RemouldingCores.CoreOf(result.GunId) is var core and not 0)
                levelUp.PDGOLKFCJFH.Add(core);

            connection.SendAutoEncrypted<Google.Protobuf.IMessage>(levelUp, result.Spent);
            if (achievements.NewlyCompleted(uid, achievementsBefore) is { } unlocked)
                connection.Send(new SC_CommonQuestCountUpdate { Counters = unlocked });
        }

        public override void HandleGunClassUpdate(CS_GunClassUpdate request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (progress.ClassUp(connection.Account.Uid, request.GunId) is not { } promoted)
            {
                connection.Refuse(MsgId.MsgCsGunClassUpdate, progress.LastError);
                return;
            }

            List<Google.Protobuf.IMessage> messages = [.. promoted.Spent];
            messages.Add(new SC_GunClassUpdate { GunId = request.GunId, GunClass = promoted.GunClass });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleGunUpgrade(CS_GunUpgrade request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (progress.GradeUp(connection.Account.Uid, request.GunId) is { } grade)
                connection.Send(new SC_GunUpgrade { GunId = request.GunId, Grade = grade });
            else
                connection.Refuse(MsgId.MsgCsGunUpgrade, progress.LastError);
        }

        public override void HandleGunRemouldingPlugins(CS_GunRemouldingPlugins request, Connection connection)
        {
            SC_GunRemouldingPlugins plugins = PluginCatalog();
            if (connection.Account is not null)
                foreach (GunRemouldingPlugin plugin in plugins.RemouldingPlugins)
                    plugin.GunId = RemouldingPlugins.GunOf(accountState, connection.Account.Uid, plugin.Id);

            connection.Send(4, plugins);
        }

        private static readonly Lazy<IReadOnlySet<ulong>> PluginIds = new(() => PluginCatalog().RemouldingPlugins.Select(p => p.Id).ToHashSet());

        /// <summary>Puts a plugin in a slot of the core (slot is 1-based; slot 0 takes the plugin off; plugin 0 empties the slot) and keeps it (A-58).</summary>
        public override void HandleGunRemouldingPluginBelong(CS_GunRemouldingPluginBelong request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                GunEntity? gun = gunService.GetPlayerGuns(uid).FirstOrDefault(g => g.GunId == request.GunId);
                if (gun is null || !RemouldingPlugins.HasCore(gun))
                {
                    connection.Refuse(MsgId.MsgCsGunRemouldingPluginBelong, HNINDAOJCKP.GunRemouldingCoreNotEquipped);
                    return;
                }

                if (request.Slot > RemouldingPlugins.SlotCount || !RemouldingPlugins.Owns(PluginIds.Value, request.GIBKJMJPLGC))
                {
                    connection.Refuse(MsgId.MsgCsGunRemouldingPluginBelong, request.Slot > RemouldingPlugins.SlotCount
                        ? HNINDAOJCKP.GunRemouldingCoreSlotError : HNINDAOJCKP.GunRemouldingPluginNotFound);
                    return;
                }

                ulong[] slots = RemouldingPlugins.SlotsOf(accountState, uid, request.GunId);
                if (request.Slot == 0)
                {
                    for (int i = 0; i < slots.Length; i++)
                        if (slots[i] == request.GIBKJMJPLGC)
                            slots[i] = 0;
                }
                else
                {
                    for (int i = 0; i < slots.Length; i++)
                        if (request.GIBKJMJPLGC != 0 && slots[i] == request.GIBKJMJPLGC)
                            slots[i] = 0;
                    slots[request.Slot - 1] = request.GIBKJMJPLGC;
                }

                RemouldingPlugins.Set(accountState, uid, request.GunId, slots);
                connection.Send(new SC_GunRemouldingPluginBelong { GunId = request.GunId, Slot = request.Slot, GIBKJMJPLGC = request.GIBKJMJPLGC });
            }
        }

        /// <summary>All six slots at once: "一键装配" sends the plugins, "全部卸下" an empty list (A-58).</summary>
        public override void HandleGunRemouldingPluginBatchBelong(CS_GunRemouldingPluginBatchBelong request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                GunEntity? gun = gunService.GetPlayerGuns(uid).FirstOrDefault(g => g.GunId == request.GunId);
                if (gun is null || !RemouldingPlugins.HasCore(gun))
                {
                    connection.Refuse(MsgId.MsgCsGunRemouldingPluginBatchBelong, HNINDAOJCKP.GunRemouldingCoreNotEquipped);
                    return;
                }

                if (request.PluginUids.Count > RemouldingPlugins.SlotCount || request.PluginUids.Any(p => !RemouldingPlugins.Owns(PluginIds.Value, p)))
                {
                    connection.Refuse(MsgId.MsgCsGunRemouldingPluginBatchBelong, HNINDAOJCKP.GunRemouldingPluginNotFound);
                    return;
                }

                ulong[] slots = RemouldingPlugins.Set(accountState, uid, request.GunId, request.PluginUids.ToList());
                SC_GunRemouldingPluginBatchBelong reply = new() { GunId = request.GunId };
                reply.PluginUids.AddRange(slots);
                connection.Send(reply);
            }
        }

        internal static SC_GunRemouldingPlugins PluginCatalog() =>
            new SC_GunRemouldingPlugins
                    {
                        RemouldingPlugins =
                        {
                            new GunRemouldingPlugin
                            {
                                Id = 1835910277177040896UL,
                                StcId = 983201,
                                ACEIFDEGLMH =
                                {
                                    1200201,
                                    2300501,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1846099719155113984UL,
                                StcId = 984102,
                                ACEIFDEGLMH =
                                {
                                    1100302,
                                    2100701,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1843288636979699712UL,
                                StcId = 983301,
                                ACEIFDEGLMH =
                                {
                                    1300101,
                                    2100201,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1839071274439041024UL,
                                StcId = 983101,
                                ACEIFDEGLMH =
                                {
                                    1100201,
                                    2100301,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1829565371798020096UL,
                                StcId = 984202,
                                ACEIFDEGLMH =
                                {
                                    1200402,
                                    2400701,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1846544928750985216UL,
                                StcId = 983101,
                                ACEIFDEGLMH =
                                {
                                    1100401,
                                    2300501,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1845186142339620864UL,
                                StcId = 984302,
                                ACEIFDEGLMH =
                                {
                                    1300302,
                                    2200201,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1830929806184701952UL,
                                StcId = 983101,
                                ACEIFDEGLMH =
                                {
                                    1100301,
                                    2400201,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1832835441797128192UL,
                                StcId = 983301,
                                ACEIFDEGLMH =
                                {
                                    1300401,
                                    2100701,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1845005422894800896UL,
                                StcId = 984302,
                                ACEIFDEGLMH =
                                {
                                    1300402,
                                    2300801,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1858088292359102464UL,
                                StcId = 983101,
                                ACEIFDEGLMH =
                                {
                                    1100301,
                                    2400101,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1836550333413744640UL,
                                StcId = 983401,
                                ACEIFDEGLMH =
                                {
                                    1400101,
                                    2300101,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1841010189906501632UL,
                                StcId = 984402,
                                ACEIFDEGLMH =
                                {
                                    1400202,
                                    2100201,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1835280701670318080UL,
                                StcId = 984202,
                                ACEIFDEGLMH =
                                {
                                    1200302,
                                    2200701,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1838893343599255552UL,
                                StcId = 984402,
                                ACEIFDEGLMH =
                                {
                                    1400102,
                                    2200301,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1842370612705648640UL,
                                StcId = 984402,
                                ACEIFDEGLMH =
                                {
                                    1400102,
                                    2100601,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1857996740069711872UL,
                                StcId = 983301,
                                ACEIFDEGLMH =
                                {
                                    1300401,
                                    2200801,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1843288855838482432UL,
                                StcId = 984402,
                                ACEIFDEGLMH =
                                {
                                    1400302,
                                    2200801,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1834636826666622976UL,
                                StcId = 983101,
                                ACEIFDEGLMH =
                                {
                                    1100101,
                                    2100801,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1845459095485702144UL,
                                StcId = 984202,
                                ACEIFDEGLMH =
                                {
                                    1200202,
                                    2400401,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1840808736761864192UL,
                                StcId = 983401,
                                ACEIFDEGLMH =
                                {
                                    1400201,
                                    2400201,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1842370402064556032UL,
                                StcId = 983101,
                                ACEIFDEGLMH =
                                {
                                    1100101,
                                    2200601,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1830196550596321280UL,
                                StcId = 983401,
                                ACEIFDEGLMH =
                                {
                                    1400301,
                                    2400401,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1831464973352591360UL,
                                StcId = 983401,
                                ACEIFDEGLMH =
                                {
                                    1400401,
                                    2300401,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1842097119480012800UL,
                                StcId = 985102,
                                ACEIFDEGLMH =
                                {
                                    1100203,
                                    2400101,
                                    2101001,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1827044870051618816UL,
                                StcId = 984302,
                                ACEIFDEGLMH =
                                {
                                    1300302,
                                    2200101,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1834013572877803520UL,
                                StcId = 983101,
                                ACEIFDEGLMH =
                                {
                                    1100201,
                                    2200701,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1856336655181438976UL,
                                StcId = 983201,
                                ACEIFDEGLMH =
                                {
                                    1200301,
                                    2100301,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1845640086511706112UL,
                                StcId = 984302,
                                ACEIFDEGLMH =
                                {
                                    1300302,
                                    2300501,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1828925846182715392UL,
                                StcId = 985102,
                                ACEIFDEGLMH =
                                {
                                    1100303,
                                    2400202,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = true,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1826204793002024960UL,
                                StcId = 983401,
                                ACEIFDEGLMH =
                                {
                                    1400301,
                                    2400201,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1826383457987616768UL,
                                StcId = 983401,
                                ACEIFDEGLMH =
                                {
                                    1400101,
                                    2400201,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1833379456729767936UL,
                                StcId = 983201,
                                ACEIFDEGLMH =
                                {
                                    1200301,
                                    2200401,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1844548013226221568UL,
                                StcId = 984102,
                                ACEIFDEGLMH =
                                {
                                    1100102,
                                    2300101,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1845818391211827200UL,
                                StcId = 984202,
                                ACEIFDEGLMH =
                                {
                                    1200302,
                                    2200101,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1839905421879435264UL,
                                StcId = 984402,
                                ACEIFDEGLMH =
                                {
                                    1400302,
                                    2100401,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1846545133357522944UL,
                                StcId = 984302,
                                ACEIFDEGLMH =
                                {
                                    1300302,
                                    2400201,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1832285033641041920UL,
                                StcId = 983201,
                                ACEIFDEGLMH =
                                {
                                    1200301,
                                    2300501,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1837905451422867456UL,
                                StcId = 984302,
                                ACEIFDEGLMH =
                                {
                                    1300202,
                                    2100301,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1827847029261361152UL,
                                StcId = 983401,
                                ACEIFDEGLMH =
                                {
                                    1400101,
                                    2300701,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                            new GunRemouldingPlugin
                            {
                                Id = 1828489963575992320UL,
                                StcId = 983301,
                                ACEIFDEGLMH =
                                {
                                    1300101,
                                    2200601,
                                },
                                GunId = 0,
                                MDFIDAFPBJN = false,
                            },
                        },
                    };

        /// <summary>
        /// Puts a costume on a character and keeps it (<c>GunEntity.CostumeId</c>, sent back in <c>SC_Guns</c>): until now the request
        /// was echoed and the change was lost at the next login. The costume must be a <c>ClothesData</c> row (id 1105502, 1108001...) whose
        /// column 21 is that character (<c>CostumeData</c> only lists 53 of them: 1108001 was refused with error 10114). ASSUMPTION: ownership is not checked (the client
        /// only offers the costumes it lists). The parts of the new costume are those last saved for it (<c>costume.parts</c>).
        /// </summary>
        public override void HandleGunCostume(CS_GunCostume request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            GunEntity? gun = inventoryService.GetPlayerInventory<GunEntity>(uid).FirstOrDefault(g => g.GunId == request.GunId);
            if (gun is null)
            {
                connection.Refuse(MsgId.MsgCsGunCostume, HNINDAOJCKP.GunNotFound);
                return;
            }

            if (!tableService.GetTable<ClothesData>().Any(c => c.Id == request.CostumeId && TableFields.U(c, 21) == request.GunId))
            {
                connection.Refuse(MsgId.MsgCsGunCostume, HNINDAOJCKP.GunConfigNotExist);
                return;
            }

            gun.CostumeId = request.CostumeId;
            gun.CostumeParts = SavedParts(uid).GetValueOrDefault(PartsKey(request.CostumeId, BAGPCFGACEF.Types.GCBADMLINMA.Barrack));
            gunService.Save();
            connection.Send(new SC_GunCostume
            {
                GunId = request.GunId,
                CostumeId = request.CostumeId,
            });
        }

        /// <summary>
        /// Saves the parts (hair, glasses...) chosen for a costume per place (<c>Barrack</c> = the character screen, <c>Lounge</c> = the
        /// lobby): <c>costume.parts</c> in the account state, key <c>costume:place</c>. The Barrack parts of the worn costume are also
        /// what <c>SC_Guns</c> reports (<c>GunEntity.CostumeParts</c>).
        /// </summary>
        public override void HandleGunCostumeParts(CS_GunCostumeParts request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            GunEntity? gun = inventoryService.GetPlayerInventory<GunEntity>(uid).FirstOrDefault(g => g.GunId == request.GunId);
            if (gun is null)
            {
                connection.Refuse(MsgId.MsgCsGunCostumeParts, HNINDAOJCKP.GunNotFound);
                return;
            }

            Dictionary<string, uint> saved = SavedParts(uid);
            saved[PartsKey(request.CostumeId, request.LLLFHFLGNCH)] = request.CostumeParts;
            accountState.Set(uid, PartsStateKey, saved);
            if (request.CostumeId == gun.CostumeId && request.LLLFHFLGNCH != BAGPCFGACEF.Types.GCBADMLINMA.Lounge)
            {
                gun.CostumeParts = request.CostumeParts;
                gunService.Save();
            }

            connection.Send(new SC_GunCostumeParts
            {
                CostumeId = request.CostumeId,
                CostumeParts = request.CostumeParts,
                LLLFHFLGNCH = request.LLLFHFLGNCH,
            });
        }

        private const string PartsStateKey = "costume.parts";

        private static string PartsKey(uint costume, BAGPCFGACEF.Types.GCBADMLINMA place) => $"{costume}:{(int)place}";

        private Dictionary<string, uint> SavedParts(uint uid) => accountState.Get<Dictionary<string, uint>>(uid, PartsStateKey) ?? [];

        /// <summary>
        /// Puts a public talent skill item in a slot (index 0..2) of a character, or empties the slot (item uid 0). The item leaves the
        /// character that wore it and the item that was in the target slot is taken out; both characters and both items are reported.
        /// ASSUMPTION: slots are 1..3 and no other rule (talent level, item type) is checked.
        /// </summary>
        public override void HandleGunPublicTalentSkillItemBelong2(CS_GunPublicTalentSkillItemBelong2 request, Connection connection)
        {
            if (connection.Account is null)
                return;

            GunEntity[] guns = gunService.GetPlayerGuns(connection.Account.Uid);
            GunEntity? target = guns.FirstOrDefault(g => g.GunId == request.GunId);
            if (target is null)
            {
                connection.Refuse(MsgId.MsgCsGunPublicTalentSkillItemBelong2, HNINDAOJCKP.GunNotFound);
                return;
            }

            (ulong Id, uint StcId) wanted = PublicTalentHandler.ItemsOf(accountState, connection.Account.Uid).FirstOrDefault(i => i.Id == request.PublicTalentSkillItemUid);
            if (request.PublicTalentSkillItemUid != 0 && wanted.Id == 0)
            {
                connection.Refuse(MsgId.MsgCsGunPublicTalentSkillItemBelong2, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            // The slot is a 0-BASED INDEX: protobuf omits field 2 when it is 0, so a request without it is the FIRST slot. The real client
            // tries the free slots in order (log 2026-10-16 16:14: no field 2, then slot 1, then slot 2). Treating "no slot" as a read-only
            // preview (the earlier reading) answered "装配成功" without equipping anything. ASSUMPTION A-48.
            if (request.Slot > 2)
            {
                connection.Refuse(MsgId.MsgCsGunPublicTalentSkillItemBelong2, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            int index = (int)request.Slot;
            ulong occupant = target.PublicTalentSkillItemsUid.Length > index ? target.PublicTalentSkillItemsUid[index] : 0;
            GunEntity? previousHolder = request.PublicTalentSkillItemUid == 0 ? null
                : guns.FirstOrDefault(g => g.PublicTalentSkillItemsUid.Contains(request.PublicTalentSkillItemUid));

            static void Set(GunEntity gun, int slot, uint stcId, ulong uid)
            {
                uint[] items = new uint[Math.Max(3, gun.PublicTalentSkillItems.Length)];
                ulong[] uids = new ulong[Math.Max(3, gun.PublicTalentSkillItemsUid.Length)];
                gun.PublicTalentSkillItems.CopyTo(items, 0);
                gun.PublicTalentSkillItemsUid.CopyTo(uids, 0);
                items[slot] = stcId;
                uids[slot] = uid;
                gun.PublicTalentSkillItems = items;
                gun.PublicTalentSkillItemsUid = uids;
            }

            if (previousHolder is not null)
                Set(previousHolder, Array.IndexOf(previousHolder.PublicTalentSkillItemsUid, request.PublicTalentSkillItemUid), 0, 0);

            Set(target, index, wanted.StcId, wanted.Id);
            gunService.Save();

            static GunAndSkills Describe(GunEntity gun)
            {
                GunAndSkills result = new() { GunId = gun.GunId };
                result.PublicSkillItem.AddRange(gun.PublicTalentSkillItems);
                result.PublicSkillItemUid.AddRange(gun.PublicTalentSkillItemsUid);
                return result;
            }

            SC_GunPublicTalentSkillItemBelong2 reply = new() { Gun1 = Describe(target) };
            if (previousHolder is not null && previousHolder.GunId != target.GunId)
                reply.Gun2 = Describe(previousHolder);
            if (wanted.Id != 0)
                reply.Public1 = PublicTalentHandler.ToProto(wanted.Id, wanted.StcId, target.GunId);
            if (occupant != 0 && occupant != request.PublicTalentSkillItemUid
                && PublicTalentHandler.ItemsOf(accountState, connection.Account.Uid).FirstOrDefault(i => i.Id == occupant) is { Id: not 0 } displaced)
                reply.Public2 = PublicTalentHandler.ToProto(displaced.Id, displaced.StcId, 0);
            connection.Send(reply);
        }

        public override void HandleGetGunRemouldingPluginLockPlan(CS_GetGunRemouldingPluginLockPlan request, Connection connection)
        {
                connection.Send(new SC_GetGunRemouldingPluginLockPlan
                {
                    DJNPEOFHKHD = new AOEAJGGGBBM
                    {
                        COFDFFJMNMA = false,
                    },
                });
        }

        public override void HandleGunSelected(CS_GunSelected request, Connection connection)
        {
            connection.Send(new SC_GunSelected
            {
                GunId = request.GunId,
                KOPLHBNFPIN = request.KOPLHBNFPIN,
            });
        }

        // Affection talent item: the account has no such item (the item list is the three public ones), so only the character is reported.
        public override void HandleGunLoveTalentSkillItemBelong(CS_GunLoveTalentSkillItemBelong request, Connection connection)
        {
            if (connection.Account is null)
                return;

            GunEntity? gun = gunService.GetPlayerGuns(connection.Account.Uid).FirstOrDefault(g => g.GunId == request.GunId);
            if (gun is null)
            {
                connection.Refuse(MsgId.MsgCsGunLoveTalentSkillItemBelong, HNINDAOJCKP.GunNotFound);
                return;
            }

            GunAndSkills described = new() { GunId = gun.GunId };
            described.PublicSkillItem.AddRange(gun.PublicTalentSkillItems);
            described.PublicSkillItemUid.AddRange(gun.PublicTalentSkillItemsUid);
            connection.Send(new SC_GunLoveTalentSkillItemBelong { Gun1 = described });
        }

        /// <summary>Puts a private talent item id in a slot (1..3) of a character and keeps it (<c>GunEntity.PrivateTalentSkillItems</c>). ASSUMPTION: no ownership or type check.</summary>
        public override void HandleGunPrivateTalentSkillItemBelong2(CS_GunPrivateTalentSkillItemBelong2 request, Connection connection)
        {
            if (connection.Account is null)
                return;

            GunEntity? gun = gunService.GetPlayerGuns(connection.Account.Uid).FirstOrDefault(g => g.GunId == request.GunId);
            if (gun is null)
            {
                connection.Refuse(MsgId.MsgCsGunPrivateTalentSkillItemBelong2, HNINDAOJCKP.GunNotFound);
                return;
            }

            // 0-based slot index (field 2 is omitted when it is 0), like the public talent items (A-48)
            if (request.Slot > 2)
            {
                connection.Refuse(MsgId.MsgCsGunPrivateTalentSkillItemBelong2, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            uint[] items = new uint[Math.Max(3, gun.PrivateTalentSkillItems.Length)];
            gun.PrivateTalentSkillItems.CopyTo(items, 0);
            uint[] peak = TalentKeySlots.Normalize(gun);
            if (TalentKeySlots.IsPeakKey(request.PrivateTalentSkillItemId))
            {
                // the peak key (峰位键, e.g. 921055) goes to the middle slot of the wheel, not to the side ones (A-61)
                for (int i = 0; i < peak.Length; i++)
                    if (peak[i] == request.PrivateTalentSkillItemId)
                        peak[i] = 0;
                peak[request.Slot] = request.PrivateTalentSkillItemId;
                TalentKeySlots.Store(gun, peak);
            }
            else
            {
                items[request.Slot] = request.PrivateTalentSkillItemId;
                gun.PrivateTalentSkillItems = items;
            }

            gunService.Save();
            SC_GunPrivateTalentSkillItemBelong2 reply = new() { GunId = gun.GunId };
            reply.GunPrivateTalents.AddRange(items);
            reply.JKHPKIEMIEI.AddRange(peak);
            connection.Send(reply);
        }

        /// <summary>
        /// Activates the next Mind Spiral key from <c>SpTalentKeyData</c>. This progression is independent from the character's ordinary level;
        /// the table's key cost is spent and its tier is persisted to <c>Gun.NGJCBNLMPHP</c> (field 58).
        /// </summary>
        /// <summary>
        /// Receives the public key of a character whose whole talent tree is unlocked (button of the key node). The key is the item
        /// of <c>SquadTalentGunData</c> column 13 (e.g. 901080 for character 1080); it is added to the account's public items. ASSUMPTION A-50.
        /// </summary>
        public override void HandleGetGunPublicSkillItem(CS_GetGunPublicSkillItem request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                GunEntity? gun = gunService.GetPlayerGuns(uid).FirstOrDefault(g => g.GunId == request.GunId);
                if (gun is null)
                {
                    connection.Refuse(MsgId.MsgCsGetGunPublicSkillItem, HNINDAOJCKP.GunNotFound);
                    return;
                }

                uint keyItem = TalentBits.KeyItemOf(gun.GunId);
                if (gun.IsGetPublicTalentSkillItem || keyItem == 0 || !(gun.IsAllTalentUnlock || TalentBits.AllUnlocked(gun.GunId, gun.TalentTree.Keys)))
                {
                    connection.Refuse(MsgId.MsgCsGetGunPublicSkillItem, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                List<PublicTalentHandler.ExtraItem> extra = accountState.Get<List<PublicTalentHandler.ExtraItem>>(uid, PublicTalentHandler.ExtraKey) ?? [];
                PublicTalentHandler.ExtraItem received = new() { Id = 1843288605358900000UL + keyItem, StcId = keyItem };
                extra.Add(received);
                accountState.Set(uid, PublicTalentHandler.ExtraKey, extra);
                gun.IsAllTalentUnlock = true;
                gun.IsGetPublicTalentSkillItem = true;
                gunService.Save();
                // the client adds the new key to its public item list only from this push (the list is read once, when the panel opens)
                connection.SendAutoEncrypted<Google.Protobuf.IMessage>(
                    new SC_NewPublicTalentSkillItem { PublicSkillItem = PublicTalentHandler.ToProto(received.Id, received.StcId, 0) },
                    new SC_GetGunPublicSkillItem { PublicTalentSkill = keyItem, GunId = gun.GunId });
            }
        }

        public override void HandleGunSpTalentLvUp(CS_GunSpTalentLvUp request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                GunEntity? gun = gunService.GetPlayerGuns(uid).FirstOrDefault(row => row.GunId == request.GunId);
                if (gun is null)
                {
                    connection.Refuse(MsgId.MsgCsGunSpTalentLvUp, HNINDAOJCKP.GunNotFound);
                    return;
                }

                // the real client names the KEY ITEM of the character (TalentKeyData id, e.g. 921055) plus the level; the tier row id (92105501) is
                // accepted too (A-60: the tier-2 button of Nikketa answered "client data abnormal" because only the row id was looked up)
                SpTalentKeyData? key = tableService.GetTable<SpTalentKeyData>().FirstOrDefault(row => row.Id == request.PrivateTalentSkillItemId)
                    ?? tableService.GetTable<SpTalentKeyData>().FirstOrDefault(row => row.CBJBAFMMPDB == request.PrivateTalentSkillItemId && row.Level == request.Level);
                bool belongsToGun = key is not null
                    && tableService.GetTable<TalentKeyData>().Any(row => TableFields.U(row, 1) == key.CBJBAFMMPDB && TableFields.U(row, 9) == gun.GunId);
                if (!belongsToGun || key is null || request.Level == 0 || key.Level != request.Level
                    || request.Level != gun.SpTalentLevel + 1)
                {
                    connection.Refuse(MsgId.MsgCsGunSpTalentLvUp, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                if (!TryParseCost(key.Cost, out Dictionary<uint, int> cost))
                {
                    Serilog.Log.Warning("Invalid Mind Spiral cost in SpTalentKeyData {KeyId}: {Cost}", key.Id, key.Cost);
                    connection.Refuse(MsgId.MsgCsGunSpTalentLvUp, HNINDAOJCKP.ItemConfigNotExist);
                    return;
                }

                List<Google.Protobuf.IMessage> updates = [];
                if (!itemUse.Spend(uid, cost, updates))
                {
                    connection.Refuse(MsgId.MsgCsGunSpTalentLvUp, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                gun.SpTalentLevel = request.Level;
                gunService.Save();
                updates.Add(new SC_GunSpTalentLvUp
                {
                    GunId = request.GunId,
                    PrivateTalentSkillItemId = request.PrivateTalentSkillItemId,
                    Level = gun.SpTalentLevel,
                });
                connection.SendAutoEncrypted(updates.ToArray());
            }
        }

        private static bool TryParseCost(string text, out Dictionary<uint, int> cost)
        {
            cost = [];
            foreach (string entry in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] pair = entry.Split(':', StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || !uint.TryParse(pair[0], out uint itemId) || itemId == 0
                    || !int.TryParse(pair[1], out int amount) || amount <= 0)
                    return false;
                cost[itemId] = checked(cost.GetValueOrDefault(itemId) + amount);
            }

            return cost.Count > 0;
        }

        /// <summary>Key of the dormitory behaviours the account bought (also announced by <c>GetCharacterDailyInfo</c>).</summary>
        public const string PerformancesKey = "dorm.performances";

        /// <summary>
        /// Buying a locked dormitory behaviour (the dialog <c>UIDormChrBehaviourBuyDialog</c> sends the behaviour id): it is remembered per account and
        /// announced by <c>GetCharacterDailyInfo</c> so the client stops offering it. ASSUMPTION A-34: the cost table of the dialog row was not identified
        /// (<c>CharacterDailyData</c> has no cost), so buying is free.
        /// </summary>
        public override void HandleBuyGunPerformance(CS_BuyGunPerformance request, Connection connection)
        {
            if (connection.Account is not null && request.HLKFJHBHEDC != 0)
            {
                Dictionary<uint, bool> bought = accountState.Get<Dictionary<uint, bool>>(connection.Account.Uid, PerformancesKey) ?? [];
                bought[request.HLKFJHBHEDC] = true;
                accountState.Set(connection.Account.Uid, PerformancesKey, bought);
            }

            connection.Send(new SC_BuyGunPerformance { HLKFJHBHEDC = request.HLKFJHBHEDC });
        }
    }
}
