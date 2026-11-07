using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Extensions;
using Griffin.Common.Protocol;

namespace Griffin.GameServer.Handlers
{
    public sealed class DarkZoneHandler(Griffin.GameServer.Services.IModProgressService modProgress, Griffin.GameServer.Services.IAccountStateService accountState, Griffin.Common.Table.ITableService tableService, Griffin.GameServer.Services.IItemUseService itemUse, Griffin.GameServer.Services.IRewardService rewardService) : DarkZoneHandlerBase
    {
        /// <summary>The menus of the Dark Zone kept per account: talents, weekly difficulty, egg stages opened and quest groups claimed.</summary>
        public sealed class DzState
        {
            public List<uint> Talents { get; set; } = [];
            public int Difficulty { get; set; } = (int)HBMBNIHMKLI.Types.FHOMGAKNIAF.Easy;
            public List<uint> EggStages { get; set; } = [];
            public List<uint> QuestsClaimed { get; set; } = [];
            public Dictionary<uint, LineUpState> LineUps { get; set; } = [];
            public List<uint> Squad { get; set; } = [];
            public List<ContractState> Contracts { get; set; } = [];
            public uint StorageBuyTimes { get; set; }
        }

        public sealed class LineUpState
        {
            public List<uint> Guns { get; set; } = [];
            public uint Leader { get; set; }
        }

        /// <summary>A doll sent on a contract (DarkzoneContractData: 3 = seconds it lasts).</summary>
        public sealed class ContractState
        {
            public uint Gun { get; set; }
            public uint Contract { get; set; }
            public long Start { get; set; }
            public long End { get; set; }
        }

        private void SaveDz(uint uid, Action<DzState> change)
        {
            lock (Griffin.GameServer.Services.AccountLock.For(uid))
            {
                DzState state = Dz(uid);
                change(state);
                accountState.Set(uid, DzKey, state);
            }
        }

        private EKGNJFNONNB ContractInfo(DzState state)
        {
            EKGNJFNONNB info = new();
            info.LBGCCEHCNHD.AddRange(state.Contracts.Select(c => new AJHNKKCHBBB { GunId = c.Gun, JIDABKEPOOE = c.Contract, StartTime = c.Start, FEMJMHOADOI = c.End }));
            return info;
        }

        public override void HandleDarkZoneLineUps(CS_DarkZoneLineUps request, Connection connection)
        {
            if (connection.Account is null)
                return;
            DzState state = Dz(connection.Account.Uid);
            SC_DarkZoneLineUps reply = new();
            uint max = state.LineUps.Count == 0 ? 0 : state.LineUps.Keys.Max();
            for (uint i = 0; i <= max && state.LineUps.Count > 0; i++)
            {
                LineUpState line = state.LineUps.GetValueOrDefault(i) ?? new LineUpState();
                LIJEDJGNKBD.Types.LineUp proto = new() { Leader = line.Leader };
                proto.Guns.AddRange(line.Guns);
                reply.LineUp.Add(proto);
            }
            connection.Send(reply);
        }

        public override void HandleDarkZoneLineUp(CS_DarkZoneLineUp request, Connection connection)
        {
            if (connection.Account is null)
                return;
            SaveDz(connection.Account.Uid, s => s.LineUps[request.Idx] = new LineUpState { Guns = [.. request.Guns], Leader = request.Leader });
            connection.Send(new SC_DarkZoneLineUp());
        }

        public override void HandleDarkZoneSetLineUp(CS_DarkZoneSetLineUp request, Connection connection)
        {
            if (connection.Account is null)
                return;
            SaveDz(connection.Account.Uid, s => s.Squad = [.. request.Guns]);
            SC_DarkZoneSetLineUp reply = new();
            reply.Guns.AddRange(request.Guns);
            connection.Send(reply);
        }

        public override void HandleDarkZoneGetContract(CS_DarkZoneGetContract request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_DarkZoneGetContract { Info = ContractInfo(Dz(connection.Account.Uid)) });
        }

        public override void HandleDarkZoneSetContract(CS_DarkZoneSetContract request, Connection connection)
        {
            if (connection.Account is null)
                return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<uint, long> length = tableService.GetTable<Griffin.Common.Table.DarkzoneContractData>()
                .ToDictionary(r => Griffin.GameServer.Services.TableFields.U(r, 1), r => (long)Griffin.GameServer.Services.TableFields.U(r, 3));
            DzState after = new();
            SaveDz(connection.Account.Uid, s =>
            {
                foreach (CS_DarkZoneSetContract.Types.HFDNLIKKFED pick in request.AEIMHFHKMDH)
                {
                    s.Contracts.RemoveAll(c => c.Gun == pick.GunId);
                    s.Contracts.Add(new ContractState { Gun = pick.GunId, Contract = pick.JIDABKEPOOE, Start = now, End = now + length.GetValueOrDefault(pick.JIDABKEPOOE, 54000) });
                }
                after = s;
            });
            connection.Send(new SC_DarkZoneSetContract { Info = ContractInfo(after) });
        }

        /// <summary>
        /// Ends contracts. A finished one pays its loot: DarkzoneContractData column 5 lists "package:rolls:weight"; one package is chosen
        /// by weight and rolled that many times (DropPackageData). A contract ended early pays nothing.
        /// </summary>
        public override void HandleDarkZoneEndContract(CS_DarkZoneEndContract request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            HashSet<uint> guns = request.AEIMHFHKMDH.Select(p => p.GunId).ToHashSet();
            List<ContractState> ended = [];
            SaveDz(uid, s =>
            {
                ended = s.Contracts.Where(c => guns.Contains(c.Gun)).ToList();
                s.Contracts.RemoveAll(c => guns.Contains(c.Gun));
            });

            List<Google.Protobuf.IMessage> messages = [];
            SC_DarkZoneEndContract reply = new();
            reply.AEIMHFHKMDH.AddRange(request.AEIMHFHKMDH);
            foreach (ContractState contract in ended.Where(c => now >= c.End))
            {
                Google.Protobuf.IMessage? row = tableService.GetTable<Griffin.Common.Table.DarkzoneContractData>().FirstOrDefault(r => Griffin.GameServer.Services.TableFields.U(r, 1) == contract.Contract);
                List<(uint Package, int Rolls, int Weight)> loot = (row is null ? "" : Griffin.GameServer.Services.TableFields.S(row, 5))
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(e => e.Split(':'))
                    .Where(p => p.Length == 3 && uint.TryParse(p[0], out _) && int.TryParse(p[1], out _) && int.TryParse(p[2], out _))
                    .Select(p => (uint.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2])))
                    .ToList();
                int total = loot.Sum(l => Math.Max(0, l.Weight));
                if (total <= 0)
                    continue;
                int roll = Random.Shared.Next(total);
                (uint Package, int Rolls, int Weight) chosen = loot.First(l => (roll -= Math.Max(0, l.Weight)) < 0);
                for (int i = 0; i < chosen.Rolls; i++)
                    foreach ((uint item, int count) in itemUse.RollPackage(chosen.Package))
                    {
                        rewardService.Grant(uid, item, count, messages);
                        reply.Rewards[item] = reply.Rewards.GetValueOrDefault(item) + (uint)count;
                    }
            }

            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleDarkZoneBuyStorage(CS_DarkZoneBuyStorage request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint times = 0;
            SaveDz(connection.Account.Uid, s => { s.StorageBuyTimes += Math.Max(1, request.Num); times = s.StorageBuyTimes; });
            connection.Send(new SC_DarkZoneBuyStorage { StorageBuyTimes = times });
        }

        public override void HandleDarkZoneStorageMove(CS_DarkZoneStorageMove request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_DarkZoneStorageMove());
        }

        public override void HandleDarkZoneEquip(CS_DarkZoneEquip request, Connection connection)
        {
            if (connection.Account is null)
                return;
            SC_DarkZoneEquip reply = new();
            reply.Entities.AddRange(request.Entities);
            connection.Send(reply);
        }

        public override void HandleDarkZoneSell(CS_DarkZoneSell request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_DarkZoneSell());
        }

        private const string DzKey = "darkzone.menus";

        private DzState Dz(uint uid) => accountState.Get<DzState>(uid, DzKey) ?? new DzState();

        public override void HandleDarkZoneTalentUnlock(CS_DarkZoneTalentUnlock request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (Griffin.GameServer.Services.AccountLock.For(uid))
            {
                DzState state = Dz(uid);
                if (!state.Talents.Contains(request.TalentId))
                    state.Talents.Add(request.TalentId);
                accountState.Set(uid, DzKey, state);
            }
            connection.Send(new SC_DarkZoneTalentUnlock { TalentId = request.TalentId });
        }

        public override void HandleDarkZoneTalentReset(CS_DarkZoneTalentReset request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (Griffin.GameServer.Services.AccountLock.For(uid))
            {
                DzState state = Dz(uid);
                state.Talents.Clear();
                accountState.Set(uid, DzKey, state);
            }
            connection.Send(new SC_DarkZoneTalentReset());
        }

        public override void HandleDarkZoneSetWeeklyDifficulty(CS_DarkZoneSetWeeklyDifficulty request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (Griffin.GameServer.Services.AccountLock.For(uid))
            {
                DzState state = Dz(uid);
                state.Difficulty = (int)request.IFNEDNHKBJN;
                accountState.Set(uid, DzKey, state);
            }
            connection.Send(new SC_DarkZoneSetWeeklyDifficulty { IFNEDNHKBJN = request.IFNEDNHKBJN });
        }

        public override void HandleDarkZoneUnlockEggStage(CS_DarkZoneUnlockEggStage request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (Griffin.GameServer.Services.AccountLock.For(uid))
            {
                DzState state = Dz(uid);
                if (!state.EggStages.Contains(request.StageId))
                    state.EggStages.Add(request.StageId);
                accountState.Set(uid, DzKey, state);
            }
            connection.Send(new SC_DarkZoneUnlockEggStage { StageId = request.StageId });
        }

        public override void HandleDarkZoneTakeQuestReward(CS_DarkZoneTakeQuestReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            lock (Griffin.GameServer.Services.AccountLock.For(uid))
            {
                DzState state = Dz(uid);
                if (!state.QuestsClaimed.Contains(request.GroupId))
                    state.QuestsClaimed.Add(request.GroupId);
                accountState.Set(uid, DzKey, state);
            }
            connection.Send(new SC_DarkZoneTakeQuestReward { Quest = new DarkZoneQuestGroup { Id = request.GroupId, Status = DarkZoneQuestGroup.Types.Status.Finished } });
        }

        public override void HandleDarkZoneStorage(CS_DarkZoneStorage request, Connection connection)
        {
                    connection.Send(new SC_DarkZoneStorage
                    {
                        StorageBuyTimes = 0,
                        Storage = new PileBag
                        {
                            Entities = { },
                            Others = { },
                            House = true,
                            IHOAILNKNNE = { },
                            Weapons = { },
                            WeaponMods = { },
                        },
                        Bag = new PileBag
                        {
                            Entities = { },
                            Others = { },
                            House = false,
                            IHOAILNKNNE = { },
                            Weapons = { },
                            WeaponMods = { },
                        },
                        Equips = { },
                    });
        }

        public override void HandleDarkZoneQuestModeInfo(CS_DarkZoneQuestModeInfo request, Connection connection)
        {
                    connection.Send(new SC_DarkZoneQuestModeInfo
                    {
                        JIAMDNEMBAK = { },
                    });
        }

        /// <summary>
        /// The "Dismantle" button of the storeroom (UIRepositoryDecomposingDialog) of the 4.0.5136 client does not send CS_GunWeaponModDismantle: it sends this request
        /// with the ids of the weapon mods (log of 2026-10-22: the server refused it with DarkZoneNotStart and nothing happened). The mods are dismantled like in
        /// <see cref="GunWeaponHandler"/>; the client removes them with SC_GunWeaponModDismantle and takes what it got from the answer (A-153).
        /// </summary>
        public override void HandleDarkZoneWishDismantle(CS_DarkZoneWishDismantle request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<Google.Protobuf.IMessage> messages = [];
            Dictionary<uint, int> gains = [];
            IReadOnlyList<uint> removed = modProgress.Dismantle(connection.Account.Uid, request.Ids, messages, gains);
            if (removed.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsDarkZoneWishDismantle, HNINDAOJCKP.GunWeaponModIsNotFree);
                return;
            }

            SC_GunWeaponModDismantle gone = new();
            gone.Ids.AddRange(removed.Select(i => (ulong)i));
            SC_DarkZoneWishDismantle answer = new();
            answer.Ids.AddRange(removed);
            foreach ((uint item, int count) in gains)
                answer.DropItems[item] = (uint)count;
            List<Google.Protobuf.IMessage> all = [gone, .. GunWeaponHandler.CoalesceTotals(messages), answer];
            connection.SendAutoEncrypted(all.ToArray());
        }

        public override void HandleDarkZoneWishExp(CS_DarkZoneWishExp request, Connection connection)
        {
                    connection.Send(new SC_DarkZoneWishExp
                    {
                    });
        }

        public override void HandleDarkZoneGetWeeklyInfo(CS_DarkZoneGetWeeklyInfo request, Connection connection)
        {
            switch (request.IPGJJLJMEBO)
            {
                case CS_DarkZoneGetWeeklyInfo.Types.EPIBDODMBLN.All:
                    connection.Send(new SC_DarkZoneGetWeeklyInfo
                    {
                        IPGJJLJMEBO = CS_DarkZoneGetWeeklyInfo.Types.EPIBDODMBLN.All,
                        BCCILNBHDAO = new HBMBNIHMKLI
                        {
                            ONMNEMDCEIJ = 1001,
                            TalentId = { connection.Account is null ? [] : Dz(connection.Account.Uid).Talents },
                            IFNEDNHKBJN = connection.Account is null ? HBMBNIHMKLI.Types.FHOMGAKNIAF.Easy : (HBMBNIHMKLI.Types.FHOMGAKNIAF)Dz(connection.Account.Uid).Difficulty,
                            Quests =
                            {
                                new DJIPBLHIDPA { QuestId = 3018, JONLCAGBGJE = false },
                                new DJIPBLHIDPA { QuestId = 3006, JONLCAGBGJE = false },
                                new DJIPBLHIDPA { QuestId = 3002, JONLCAGBGJE = false },
                                new DJIPBLHIDPA { QuestId = 3013, JONLCAGBGJE = false },
                            },
                            FEMJMHOADOI = 1779500060L,
                            LELDCCEAHIO = { },
                            GCACAKMMKKL = { },
                            FHIIMKLFEEH = { },
                        },
                    });
                    break;
            }
        }

        public override void HandleDarkZoneGetEthnicInfo(CS_DarkZoneGetEthnicInfo request, Connection connection)
        {
            connection.Send(new SC_DarkZoneGetEthnicInfo
            {
                CAAMMBOPHLI = 0,
                ADDDENOAOJO = 0,
                FNALHFDBDHB = 0,
                FIIIHMMPLNH = 0,
                Point = 0,
                MGFKLGLDBPE = { },
                PlanId = 170063,
                FELLJFBGNJG = { },
                JIAMIEJEDJJ = 0,
            });
        }

        public override void HandleDarkZoneGetUnlockEggStage(CS_DarkZoneGetUnlockEggStage request, Connection connection)
        {
            connection.Send(new SC_DarkZoneGetUnlockEggStage
            {
                NDINCEOKEOA = { connection.Account is null ? [] : Dz(connection.Account.Uid).EggStages },
            });
        }

    }
}
