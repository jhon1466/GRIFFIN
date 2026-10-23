using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.Database.Services;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using System;

namespace Griffin.GameServer.Handlers
{
    public sealed class LobbyHandler(ILobbyGardenService garden, ILobbyProgressService lobbyProgress, IAccountStateService accountState, IAccountService accountService, IRewardService rewardService, ITableService tableService, ILobbyCookingService cooking, IDailyQuestProgressService questProgress, IItemUseService itemUse, ILobbyHeatService heat, ILobbyMiniGameService miniGames, ILobbyPartyService parties) : LobbyHandlerBase
    {
        int lobbyGetBuildInfoCount;

        public override void HandleLobbyGetEventInfo(CS_LobbyGetEventInfo request, Connection connection)
        {
            uint nowUnix = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            connection.SendAutoEncrypted(new SC_LobbyGetEventInfo
            {
                ELPBKBNCKBP =
                {
                    [30200] = true,
                    [2000005] = true,
                    [2000002] = true,
                    [31000] = true,
                    [2000011] = true,
                    [30610] = true,
                    [103] = true,
                    [30710] = true,
                    [31210] = true,
                    [30510] = true,
                    [30900] = true,
                    [101] = true,
                    [30400] = true,
                    [102] = true,
                    [2000001] = true,
                    [30100] = true,
                    [31310] = true,
                    [30300] = true,
                    [2000013] = true,
                    [31110] = true,
                    [30810] = true,
                },
                MAPKAGCPLIM =
                {
                    [2000003] = new KMIAGEBIMIP
                    {
                        EventId = 2000003,
                        CGPNIPFNENP = nowUnix,
                        FJBJIJIFACH = 0,
                    },
                    [2000012] = new KMIAGEBIMIP
                    {
                        EventId = 2000012,
                        CGPNIPFNENP = nowUnix,
                        FJBJIJIFACH = 0,
                    },
                    [2000019] = new KMIAGEBIMIP
                    {
                        EventId = 2000019,
                        CGPNIPFNENP = nowUnix,
                        FJBJIJIFACH = 0,
                    },
                    [2000015] = new KMIAGEBIMIP
                    {
                        EventId = 2000015,
                        CGPNIPFNENP = nowUnix,
                        FJBJIJIFACH = 0,
                    },
                    [2000020] = new KMIAGEBIMIP
                    {
                        EventId = 2000020,
                        CGPNIPFNENP = nowUnix,
                        FJBJIJIFACH = 0,
                    },
                    [31410] = new KMIAGEBIMIP
                    {
                        EventId = 31410,
                        CGPNIPFNENP = nowUnix,
                        FJBJIJIFACH = 0,
                    },
                },
                INCNFOFGJFB = { },
                DAHKBJPOFCP = { 43016, 43015, 38003, 43008, 44001, 44002, 44003, 43001 },
                DBNONLHDHLM = { },
                HINGCIJFHAC = { 3, 1, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21 },
            });
        }

        private const string PartsKey = "lobby.parts";
        private const string BedroomKey = "lobby.bedroom";

        /// <summary>
        /// Rebuild of the central bedroom (A-84): <c>LobbyBedroomUnlockData</c> col 5 = steps that must be done first, col 7 = price (items), col 9 = reward. The steps done
        /// are stored in <c>lobby.bedroom</c> and sent in field 2 of the build info extra (<c>ABOOLELFBEE</c>); before they were not kept ("after logging back in, the state resets").
        /// </summary>
        public override void HandleLobbyBedRoomRebuildUnlock(CS_LobbyBedRoomRebuildUnlock request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            IMessage? row = tableService.GetTable<LobbyBedroomUnlockData>().FirstOrDefault(r => TableFields.U(r, 1) == request.FKDDKKFIKCA);
            List<uint> done = accountState.Get<List<uint>>(uid, BedroomKey) ?? [];
            if (row is null)
            {
                connection.Refuse(MsgId.MsgCsLobbyBedRoomRebuildUnlock, HNINDAOJCKP.LobbyBedroomPreConditionNotFinish);
                return;
            }

            if (done.Contains(request.FKDDKKFIKCA))
            {
                connection.Refuse(MsgId.MsgCsLobbyBedRoomRebuildUnlock, HNINDAOJCKP.LobbyBedroomUnlockRepeat);
                return;
            }

            if (TableFields.List(row, 5).Any(need => need != 0 && !done.Contains(need)))
            {
                connection.Refuse(MsgId.MsgCsLobbyBedRoomRebuildUnlock, HNINDAOJCKP.LobbyBedroomPreConditionNotFinish);
                return;
            }

            List<IMessage> messages = [];
            Dictionary<uint, int> cost = TableFields.Map(row, 7).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => (int)p.Value);
            if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
            {
                connection.Refuse(MsgId.MsgCsLobbyBedRoomRebuildUnlock, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            foreach ((uint item, uint count) in TableFields.Map(row, 9))
                rewardService.Grant(uid, item, (int)count, messages);
            done.Add(request.FKDDKKFIKCA);
            accountState.Set(uid, BedroomKey, done);
            messages.Add(new SC_LobbyBedRoomRebuildUnlock { FKDDKKFIKCA = request.FKDDKKFIKCA });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        private ABOOLELFBEE BedroomState(Connection connection)
        {
            ABOOLELFBEE extra = new() { DAAAANLMKAA = 35 };
            if (connection.Account is not null)
                extra.HKCFCDNFCAE.AddRange(accountState.Get<List<uint>>(connection.Account.Uid, BedroomKey) ?? []);
            return extra;
        }

        /// <summary>The parts the player unlocked (LobbyUnlockPart) are added to the room of each part (LobbyPartData col 2) in the build info.</summary>
        private SC_LobbyGetBuildInfo WithUnlockedParts(Connection connection, SC_LobbyGetBuildInfo info)
        {
            if (connection.Account is null)
                return info;

            foreach (uint unlockedRoom in accountState.Get<List<uint>>(connection.Account.Uid, RoomsKey) ?? [])
                if (!info.CPJOGIOCNMM.ContainsKey(unlockedRoom))
                    info.CPJOGIOCNMM[unlockedRoom] = new OICGGHHHFBI();

            foreach (uint part in accountState.Get<List<uint>>(connection.Account.Uid, PartsKey) ?? [])
            {
                IMessage? row = tableService.GetTable<LobbyPartData>().FirstOrDefault(r => TableFields.U(r, 1) == part);
                if (row is null)
                    continue;

                uint room = TableFields.U(row, 2);
                if (!info.CPJOGIOCNMM.TryGetValue(room, out OICGGHHHFBI? entry))
                    info.CPJOGIOCNMM[room] = entry = new OICGGHHHFBI();
                if (!entry.KICFGBGNDGF.Contains(part))
                    entry.KICFGBGNDGF.Add(part);
            }

            return info;
        }

        /// <summary>
        /// Unlocks a lobby part (decoration level of a room): the price is the item map of <c>LobbyPartData</c> col 6 and the part is stored in
        /// <c>lobby.parts</c>. ASSUMPTION: the chain order of the parts (col 14) is not enforced.
        /// </summary>
        public override void HandleLobbyUnlockPart(CS_LobbyUnlockPart request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            IMessage? part = tableService.GetTable<LobbyPartData>().FirstOrDefault(r => TableFields.U(r, 1) == request.EGINIIGDCBO);
            if (part is null)
            {
                connection.Refuse(MsgId.MsgCsLobbyUnlockPart, HNINDAOJCKP.LobbyPartLocked);
                return;
            }

            List<uint> unlocked = accountState.Get<List<uint>>(uid, PartsKey) ?? [];
            if (unlocked.Contains(request.EGINIIGDCBO))
            {
                connection.Refuse(MsgId.MsgCsLobbyUnlockPart, HNINDAOJCKP.LobbyPartAlreadyUnlocked);
                return;
            }

            List<IMessage> messages = [];
            Dictionary<uint, int> cost = TableFields.Map(part, 6).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => (int)p.Value);
            if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
            {
                connection.Refuse(MsgId.MsgCsLobbyUnlockPart, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            unlocked.Add(request.EGINIIGDCBO);
            accountState.Set(uid, PartsKey, unlocked);
            messages.Add(new SC_LobbyUnlockPart { EGINIIGDCBO = request.EGINIIGDCBO });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyGetBuildInfo(CS_LobbyGetBuildInfo request, Connection connection)
        {
                    lobbyGetBuildInfoCount++;
                    LobbyProgress progressNow = connection.Account is null ? new LobbyProgress(49, ILobbyProgressService.BaseExp, []) : lobbyProgress.Get(connection.Account.Uid);

                    connection.Send(
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 28,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40140 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40141 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40142 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 31,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1562 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1563 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1564 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1565 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1566 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1561 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 28,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40142 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40140 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40141 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 31,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1564 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1565 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1566 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1561 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1562 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1563 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 28,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40140 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40141 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40142 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 31,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1561 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1562 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1563 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1564 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1565 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1566 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 28,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40140 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40141 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40142 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 31,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1561 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1562 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1563 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1564 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1565 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1566 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 28,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40140 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40141 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40142 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 31,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1561 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1562 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1563 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1564 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1565 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1566 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 28,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40140 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40141 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40142 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 31,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1562 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1563 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1564 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1565 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1566 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1561 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 28,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40140 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40141 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 40142 },
                            },
                        },
                        new SC_PlayerStatusCounterSync
                        {
                            Num = 31,
                            Watchers =
                            {
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1562 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1563 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1564 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1565 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1566 },
                                new PELLFAAPKOL { SystemId = 63, UniqueId = 1561 },
                            },
                        },
                        WithUnlockedParts(connection, new SC_LobbyGetBuildInfo
                        {
                            CPJOGIOCNMM =
                            {
                                [101] = new OICGGHHHFBI { KICFGBGNDGF = { 10131, 10101, 10122, 10123, 10115, 10130, 10108, 10125, 10128, 10132, 10124, 10136, 10116, 10153, 10133 } },
                                [102] = new OICGGHHHFBI { KICFGBGNDGF = { 10204, 10205, 10212, 10226, 10231, 10211, 10201, 10206, 10218 } },
                                [103] = new OICGGHHHFBI { KICFGBGNDGF = { 10304, 10302, 10301 } },
                                [104] = new OICGGHHHFBI { KICFGBGNDGF = { 10401, 10402 } },
                                [106] = new OICGGHHHFBI { KICFGBGNDGF = { 10601, 10602, 10603, 10607 } },
                                [107] = new OICGGHHHFBI { KICFGBGNDGF = { 10716, 10701, 10702, 10704, 10703 } },
                                [109] = new OICGGHHHFBI { KICFGBGNDGF = { } },
                                [201] = new OICGGHHHFBI { KICFGBGNDGF = { } },
                                [202] = new OICGGHHHFBI { KICFGBGNDGF = { 20202, 20203 } },
                            },
                            Level = progressNow.Level,
                            CFOCLIHKKJL = { progressNow.ClaimedLevels },
                            Exp = progressNow.Exp,
                            KAFLIJKLBKO = BedroomState(connection),
                        }));

                    if (lobbyGetBuildInfoCount != 2)
                    {
                        return;
                    }

                    connection.SendUnsequenced(
                        new SC_RecordStatisticUpdate
                        {
                            CHEOLEADCCB = 1,
                            MDAIIFCGJMO = 11,
                            BCGNJMDFLDG = 0,
                        },
                        new SC_PlayerCounterSync
                        {
                            Counters =
                            {
                                new PlayerCounter
                                {
                                    Id = 1437,
                                    Watchers =
                                    {
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 410261,
                                            Focused = true,
                                            GBBABCAJHBL = 410261,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 410262,
                                            Focused = true,
                                            GBBABCAJHBL = 410262,
                                        },
                                    },
                                    Counter = new PlayerCounter.Types.Counter { Id = 1437, SummaryCounter = 0,
                                        DNGBLDDNGIN = 58,
                                        IMMIPLFMKIF = false,
                                        BBFIKDDGMFN = { 58, 1026 },
                                        EndTime = 0,
                                        FKPIGDOCHOB = 0,
                                        GBBFJEBJPEC = { },
                                        
                                        
                                    },
                                },
                            },
                        },
                        new SC_PlayerCounterSync
                        {
                            Counters =
                            {
                                new PlayerCounter
                                {
                                    Id = 1437,
                                    Watchers =
                                    {
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 410261,
                                            Focused = true,
                                            GBBABCAJHBL = 410261,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 410262,
                                            Focused = true,
                                            GBBABCAJHBL = 410262,
                                        },
                                    },
                                    Counter = new PlayerCounter.Types.Counter { Id = 1437, SummaryCounter = 0,
                                        DNGBLDDNGIN = 58,
                                        IMMIPLFMKIF = false,
                                        BBFIKDDGMFN = { 58, 1026 },
                                        EndTime = 0,
                                        FKPIGDOCHOB = 0,
                                        GBBFJEBJPEC = { },
                                        
                                        
                                    },
                                },
                            },
                        },
                        new SC_PlayerCounterDel { SystemId = 30, UniqueId = 23023 },
                        new SC_PlayerCounterSync
                        {
                            Counters =
                            {
                                new PlayerCounter
                                {
                                    Id = 1439,
                                    Watchers =
                                    {
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 40001,
                                            Focused = true,
                                            GBBABCAJHBL = 40001,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44012,
                                            Focused = true,
                                            GBBABCAJHBL = 44012,
                                        },
                                    },
                                    Counter = new PlayerCounter.Types.Counter { Id = 1439, SummaryCounter = 0,
                                        DNGBLDDNGIN = 5,
                                        IMMIPLFMKIF = false,
                                        BBFIKDDGMFN = { 5, 1026 },
                                        EndTime = 0,
                                        FKPIGDOCHOB = 0,
                                        GBBFJEBJPEC = { },
                                        
                                        
                                    },
                                },
                            },
                        },
                        new SC_PlayerCounterSync
                        {
                            Counters =
                            {
                                new PlayerCounter
                                {
                                    Id = 1439,
                                    Watchers =
                                    {
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 40001,
                                            Focused = true,
                                            GBBABCAJHBL = 40001,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44012,
                                            Focused = true,
                                            GBBABCAJHBL = 44012,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44015,
                                            Focused = true,
                                            GBBABCAJHBL = 44015,
                                        },
                                    },
                                    Counter = new PlayerCounter.Types.Counter { Id = 1439, SummaryCounter = 0,
                                        DNGBLDDNGIN = 5,
                                        IMMIPLFMKIF = false,
                                        BBFIKDDGMFN = { 5, 1026 },
                                        EndTime = 0,
                                        FKPIGDOCHOB = 0,
                                        GBBFJEBJPEC = { },
                                        
                                        
                                    },
                                },
                            },
                        },
                        new SC_PlayerCounterSync
                        {
                            Counters =
                            {
                                new PlayerCounter
                                {
                                    Id = 1439,
                                    Watchers =
                                    {
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 40001,
                                            Focused = true,
                                            GBBABCAJHBL = 40001,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44012,
                                            Focused = true,
                                            GBBABCAJHBL = 44012,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44015,
                                            Focused = true,
                                            GBBABCAJHBL = 44015,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 46032,
                                            Focused = true,
                                            GBBABCAJHBL = 46032,
                                        },
                                    },
                                    Counter = new PlayerCounter.Types.Counter { Id = 1439, SummaryCounter = 0,
                                        DNGBLDDNGIN = 5,
                                        IMMIPLFMKIF = false,
                                        BBFIKDDGMFN = { 5, 1026 },
                                        EndTime = 0,
                                        FKPIGDOCHOB = 0,
                                        GBBFJEBJPEC = { },
                                        
                                        
                                    },
                                },
                            },
                        },
                        new SC_PlayerCounterSync
                        {
                            Counters =
                            {
                                new PlayerCounter
                                {
                                    Id = 1439,
                                    Watchers =
                                    {
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 40001,
                                            Focused = true,
                                            GBBABCAJHBL = 40001,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44012,
                                            Focused = true,
                                            GBBABCAJHBL = 44012,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44015,
                                            Focused = true,
                                            GBBABCAJHBL = 44015,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 46032,
                                            Focused = true,
                                            GBBABCAJHBL = 46032,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 71007,
                                            Focused = true,
                                            GBBABCAJHBL = 71007,
                                        },
                                    },
                                    Counter = new PlayerCounter.Types.Counter { Id = 1439, SummaryCounter = 0,
                                        DNGBLDDNGIN = 5,
                                        IMMIPLFMKIF = false,
                                        BBFIKDDGMFN = { 5, 1026 },
                                        EndTime = 0,
                                        FKPIGDOCHOB = 0,
                                        GBBFJEBJPEC = { },
                                        
                                        
                                    },
                                },
                            },
                        },
                        new SC_PlayerCounterSync
                        {
                            Counters =
                            {
                                new PlayerCounter
                                {
                                    Id = 1439,
                                    Watchers =
                                    {
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 40001,
                                            Focused = true,
                                            GBBABCAJHBL = 40001,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44012,
                                            Focused = true,
                                            GBBABCAJHBL = 44012,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44015,
                                            Focused = true,
                                            GBBABCAJHBL = 44015,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 46032,
                                            Focused = true,
                                            GBBABCAJHBL = 46032,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 71007,
                                            Focused = true,
                                            GBBABCAJHBL = 71007,
                                        },
                                    },
                                    Counter = new PlayerCounter.Types.Counter { Id = 1439, SummaryCounter = 0,
                                        DNGBLDDNGIN = 5,
                                        IMMIPLFMKIF = false,
                                        BBFIKDDGMFN = { 5, 1026 },
                                        EndTime = 0,
                                        FKPIGDOCHOB = 0,
                                        GBBFJEBJPEC = { },
                                        
                                        
                                    },
                                },
                            },
                        },
                        new SC_PlayerCounterSync
                        {
                            Counters =
                            {
                                new PlayerCounter
                                {
                                    Id = 1439,
                                    Watchers =
                                    {
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 40001,
                                            Focused = true,
                                            GBBABCAJHBL = 40001,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44012,
                                            Focused = true,
                                            GBBABCAJHBL = 44012,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44015,
                                            Focused = true,
                                            GBBABCAJHBL = 44015,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 46032,
                                            Focused = true,
                                            GBBABCAJHBL = 46032,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 71007,
                                            Focused = true,
                                            GBBABCAJHBL = 71007,
                                        },
                                    },
                                    Counter = new PlayerCounter.Types.Counter { Id = 1439, SummaryCounter = 0,
                                        DNGBLDDNGIN = 5,
                                        IMMIPLFMKIF = false,
                                        BBFIKDDGMFN = { 5, 1026 },
                                        EndTime = 0,
                                        FKPIGDOCHOB = 0,
                                        GBBFJEBJPEC = { },
                                        
                                        
                                    },
                                },
                            },
                        },
                        new SC_PlayerCounterSync
                        {
                            Counters =
                            {
                                new PlayerCounter
                                {
                                    Id = 1439,
                                    Watchers =
                                    {
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 40001,
                                            Focused = true,
                                            GBBABCAJHBL = 40001,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44012,
                                            Focused = true,
                                            GBBABCAJHBL = 44012,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 44015,
                                            Focused = true,
                                            GBBABCAJHBL = 44015,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 46032,
                                            Focused = true,
                                            GBBABCAJHBL = 46032,
                                        },
                                        new PlayerCounter.Types.Watcher
                                        {
                                            SystemId = (PlayerCounterSystem)1, UniqueId = 71007,
                                            Focused = true,
                                            GBBABCAJHBL = 71007,
                                        },
                                    },
                                    Counter = new PlayerCounter.Types.Counter { Id = 1439, SummaryCounter = 0,
                                        DNGBLDDNGIN = 5,
                                        IMMIPLFMKIF = false,
                                        BBFIKDDGMFN = { 5, 1026 },
                                        EndTime = 0,
                                        FKPIGDOCHOB = 0,
                                        GBBFJEBJPEC = { },
                                        
                                        
                                    },
                                },
                            },
                        },
                        new SC_DropCollect
                        {
                            Drops =
                            {
                                new UserDropCache
                                {
                                    ItemId = 16,
                                    ItemNum = 18,
                                    Relate = 0,
                                    OverflowNum = 0,
                                    ExtItems = { },
                                    TranItems = { },
                                    DropUp = false,
                                    CIPFHAHNMBM = FMMNODBGEJC.Gacha,
                                    WeaponModDisplay = null,
                                    
                                },
                                new UserDropCache
                                {
                                    ItemId = 47,
                                    ItemNum = 12,
                                    Relate = 0,
                                    OverflowNum = 0,
                                    ExtItems = { },
                                    TranItems = { },
                                    DropUp = false,
                                    CIPFHAHNMBM = FMMNODBGEJC.Gacha,
                                    WeaponModDisplay = null,
                                    
                                },
                                new UserDropCache
                                {
                                    ItemId = 23023,
                                    ItemNum = 1,
                                    Relate = 0,
                                    OverflowNum = 0,
                                    ExtItems = { },
                                    TranItems = { },
                                    DropUp = false,
                                    CIPFHAHNMBM = FMMNODBGEJC.Gacha,
                                    WeaponModDisplay = null,
                                    
                                },
                                new UserDropCache
                                {
                                    ItemId = 21011,
                                    ItemNum = 1,
                                    Relate = 0,
                                    OverflowNum = 0,
                                    ExtItems = { },
                                    TranItems = { },
                                    DropUp = false,
                                    CIPFHAHNMBM = FMMNODBGEJC.Gacha,
                                    WeaponModDisplay = null,
                                    
                                },
                            },
                        });
        }

        public override void HandleLobbyGetSchemeV2(CS_LobbyGetSchemeV2 request, Connection connection)
        {
            connection.Send(WithStoredScheme(connection, new SC_LobbyGetSchemeV2
            {
                CNLAIIIHBIK = new AFHDBEJPOHJ
                {
                    PKDLBOBNCBJ =
                    {
                        [101] = new AGGNMOGPNDI
                        {
                            Guns = { },
                            PPKDIKDPBIG = { },
                            ThemeId = 0,
                            HOCKKBJALMM = { },
                            JOCPKDIKBPF =
                            {
                                [1] = new OBLDJNGKIFA
                                {
                                    KHJPJMJHHCK = 0,
                                    AABKAFBKBCF = { 660736 },
                                    DLNCGLAONAH = { },
                                },
                                [2] = new OBLDJNGKIFA
                                {
                                    KHJPJMJHHCK = 1,
                                    AABKAFBKBCF = { 809472, 1073152, 1452288, 929536 },
                                    DLNCGLAONAH = { },
                                },
                                [3] = new OBLDJNGKIFA
                                {
                                    KHJPJMJHHCK = 0,
                                    AABKAFBKBCF = { 2622906368, 2622658560, 2623308800 },
                                    DLNCGLAONAH = { },
                                },
                            },
                        },
                        [102] = new AGGNMOGPNDI
                        {
                            Guns = { },
                            PPKDIKDPBIG =
                            {
                                [10202] = 202802,
                                [10204] = 113204,
                                [10201] = 202801,
                            },
                            ThemeId = 0,
                            HOCKKBJALMM = { },
                            JOCPKDIKBPF = { },
                        },
                    },
                    Name = "",
                },
                MPDOHKILMCA =
                {
                    new AFHDBEJPOHJ { PKDLBOBNCBJ = { }, Name = "" },
                    new AFHDBEJPOHJ { PKDLBOBNCBJ = { }, Name = "" },
                    new AFHDBEJPOHJ { PKDLBOBNCBJ = { }, Name = "" },
                    new AFHDBEJPOHJ { PKDLBOBNCBJ = { }, Name = "" },
                },
            }));
        }

        private const string SchemeKey = "lobby.scheme.v2";

        // Legacy saved-slot schemes (V1 requests): the client of this build uses LobbySetSchemeV2/GetSchemeV2 and was never seen sending these (0 in the real
        // logs), so they are acknowledged by repeating the request (ASSUMPTION A-38) instead of answering "function locked".
        public override void HandleLobbySetScheme(CS_LobbySetScheme request, Connection connection) =>
            connection.Send(new SC_LobbySetScheme { Id = request.Id, PNMFJCGGKNC = request.PNMFJCGGKNC });

        public override void HandleLobbySwitchScheme(CS_LobbySwitchScheme request, Connection connection) =>
            connection.Send(new SC_LobbySwitchScheme { HAOEIOLJDNP = request.HAOEIOLJDNP });

        public override void HandleLobbyOverwriteScheme(CS_LobbyOverwriteScheme request, Connection connection) =>
            connection.Send(new SC_LobbyOverwriteScheme { HAOEIOLJDNP = request.HAOEIOLJDNP });

        /// <summary>Applies a theme (and the costumes it sets) to a room of the saved layout (ASSUMPTION A-22: same fields as the scheme room).</summary>
        public override void HandleLobbyCustomizeRoomSetTheme(CS_LobbyCustomizeRoomSetTheme request, Connection connection)
        {
            if (connection.Account is not null)
            {
                uint uid = connection.Account.Uid;
                AFHDBEJPOHJ saved = accountState.Get<string>(uid, SchemeKey) is { } text ? AFHDBEJPOHJ.Parser.ParseFrom(Convert.FromBase64String(text)) : new AFHDBEJPOHJ();
                if (!saved.PKDLBOBNCBJ.TryGetValue(request.RoomId, out AGGNMOGPNDI? room))
                    saved.PKDLBOBNCBJ[request.RoomId] = room = new AGGNMOGPNDI();
                room.ThemeId = request.ThemeId;
                foreach ((uint key, uint value) in request.PPKDIKDPBIG)
                    room.PPKDIKDPBIG[key] = value;
                accountState.Set(uid, SchemeKey, Convert.ToBase64String(saved.ToByteArray()));
            }

            SC_LobbyCustomizeRoomSetTheme reply = new() { RoomId = request.RoomId, ThemeId = request.ThemeId };
            foreach ((uint key, uint value) in request.PPKDIKDPBIG)
                reply.PPKDIKDPBIG[key] = value;
            connection.Send(reply);
        }

        /// <summary>The lobby layout the player saved replaces the one of the capture: rooms (<c>PKDLBOBNCBJ</c>) saved by <c>LobbySetSchemeV2</c> are laid over the default.</summary>
        private SC_LobbyGetSchemeV2 WithStoredScheme(Connection connection, SC_LobbyGetSchemeV2 reply)
        {
            if (connection.Account is null || accountState.Get<string>(connection.Account.Uid, SchemeKey) is not { } saved)
                return reply;

            ApplyScheme(reply.CNLAIIIHBIK, AFHDBEJPOHJ.Parser.ParseFrom(Convert.FromBase64String(saved)));
            return reply;
        }

        private static void ApplyScheme(AFHDBEJPOHJ target, AFHDBEJPOHJ changes)
        {
            foreach ((uint room, AGGNMOGPNDI edit) in changes.PKDLBOBNCBJ)
            {
                if (!target.PKDLBOBNCBJ.TryGetValue(room, out AGGNMOGPNDI? current))
                    target.PKDLBOBNCBJ[room] = current = new AGGNMOGPNDI();
                if (edit.Guns.Count > 0)
                {
                    current.Guns.Clear();
                    current.Guns.AddRange(edit.Guns);
                }

                if (edit.ThemeId != 0)
                    current.ThemeId = edit.ThemeId;
                foreach ((uint key, uint value) in edit.PPKDIKDPBIG)
                    current.PPKDIKDPBIG[key] = value;
                foreach ((uint key, JBHAOOEHJOO value) in edit.HOCKKBJALMM)
                    current.HOCKKBJALMM[key] = value;
                foreach ((uint key, OBLDJNGKIFA value) in edit.JOCPKDIKBPF)
                    current.JOCPKDIKBPF[key] = value;
            }
        }

        /// <summary>
        /// The client sends only what changed (here two rooms with the layers or costumes edited), so each room is merged into the saved one: maps
        /// per key, characters and theme when present (ASSUMPTION A-22). The saved scheme is returned by <c>LobbyGetSchemeV2</c>.
        /// </summary>
        public override void HandleLobbySetSchemeV2(CS_LobbySetSchemeV2 request, Connection connection)
        {
            if (connection.Account is not null && request.PNMFJCGGKNC is { } changes)
            {
                uint uid = connection.Account.Uid;
                AFHDBEJPOHJ saved = accountState.Get<string>(uid, SchemeKey) is { } text ? AFHDBEJPOHJ.Parser.ParseFrom(Convert.FromBase64String(text)) : new AFHDBEJPOHJ();
                ApplyScheme(saved, changes);

                accountState.Set(uid, SchemeKey, Convert.ToBase64String(saved.ToByteArray()));
            }

            connection.Send(new SC_LobbySetSchemeV2 { PNMFJCGGKNC = request.PNMFJCGGKNC });
        }

                public override void HandleLobbyGetCafeInfo(CS_LobbyGetCafeInfo request, Connection connection)
        {
                    connection.Send(new SC_LobbyGetCafeInfo
                    {
                        FMICMDLIOJD = connection.Account is null ? 0 : cooking.CoffeeBuff(connection.Account.Uid),
                        CMADKJPJPHF = 5,
                        FFNCCJADIDB = { (connection.Account is null ? ILobbyCookingService.CoffeeKinds.ToDictionary(k => k, _ => 1u) : cooking.CoffeeLevels(connection.Account.Uid).ToDictionary(p => p.Key, p => p.Value)) },
                        IAOJDFGPGKA =
                        {
                            [1001] = 24,
                            [1008] = 24,
                            [1035] = 24,
                            [1048] = 8,
                            [1062] = 8,
                            [1066] = 57,
                        },
                        PMNDAICHKKO = { },
                        POFLALGADAO = { },
                    });
        }

        public override void HandleLobbyCookingInfo(CS_LobbyCookingInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            connection.Send(new SC_LobbyCookingInfo { Info = cooking.Info(connection.Account.Uid) });
        }

        public override void HandleLobbyBuffInfo(CS_LobbyBuffInfo request, Connection connection)
        {
            (uint dinnerGroup, uint dinnerLevel, bool dinnerUsed) = connection.Account is null ? (0u, 0u, false) : cooking.Dinner(connection.Account.Uid);
                    connection.Send(1, new SC_LobbyBuffInfo
                    {
                        KFNCPJPPGEF = dinnerGroup,
                        KGHAGBOFCMD =
                        {
                            [1001] = 5,
                            [1008] = 4,
                            [1009] = 5,
                            [1013] = 48,
                            [1015] = 5,
                            [1017] = 5,
                            [1021] = 5,
                            [1022] = 4,
                            [1023] = 4,
                            [1024] = 5,
                            [1025] = 4,
                            [1026] = 5,
                            [1027] = 4,
                            [1029] = 4,
                            [1032] = 7,
                            [1033] = 4,
                            [1034] = 5,
                            [1035] = 4,
                            [1036] = 4,
                            [1038] = 5,
                            [1039] = 8,
                            [1040] = 4,
                            [1042] = 5,
                            [1043] = 4,
                            [1044] = 5,
                            [1045] = 5,
                            [1047] = 4,
                            [1048] = 4,
                            [1049] = 4,
                            [1050] = 4,
                            [1051] = 4,
                            [1052] = 4,
                            [1053] = 5,
                            [1054] = 4,
                            [1055] = 4,
                            [1056] = 4,
                            [1057] = 4,
                            [1058] = 4,
                            [1059] = 4,
                            [1060] = 5,
                            [1061] = 5,
                            [1062] = 4,
                            [1063] = 4,
                            [1064] = 5,
                            [1065] = 5,
                            [1066] = 4,
                            [1068] = 4,
                            [1069] = 4,
                            [1070] = 5,
                            [1071] = 4,
                            [1072] = 4,
                            [1073] = 5,
                            [1074] = 4,
                            [1076] = 4,
                            [1077] = 4,
                        },
                        AJLCMPKKNAA = { (connection.Account is null ? [] : cooking.DinnerLevels(connection.Account.Uid).ToDictionary(p => p.Key, p => p.Value)) },
                        AEPCLIFECCC = 0,
                        MGIKONEONBF = dinnerLevel,
                        LHGLNJNFIPM = dinnerUsed,
                    });
        }

        /// <summary>Daily and weekly heat of the lobby (ASSUMPTION A-17: field 1 = today, 2 = week, as in <c>SC_LobbyQuestHeatUpdate</c>).</summary>
        public override void HandleLobbyGetQuestInfo(CS_LobbyGetQuestInfo request, Connection connection)
        {
            LobbyHeatSnapshot snapshot = connection.Account is null ? new LobbyHeatSnapshot(0, 0, false, [], new Dictionary<uint, uint>()) : heat.Get(connection.Account.Uid);
            SC_LobbyGetQuestInfo reply = new() { PAKHJDKPENP = snapshot.Today, LNJNKGNGBAC = snapshot.Week };
            reply.GKPAAEMKODB.AddRange(snapshot.Claimed);
            foreach ((uint source, uint count) in snapshot.Counts)
                reply.KCABBIEIGCJ[source] = count;
            connection.Send(reply);
        }

        /// <summary>Counts a lobby activity as heat and tells the client (also pays the first-interaction reward of the day).</summary>
        private void AddHeat(Connection connection, uint source)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = heat.Add(connection.Account.Uid, source);
            if (messages.Count > 0)
                connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyInteract(CS_LobbyInteract request, Connection connection)
        {
            AddHeat(connection, request.ECGAHCPCDDI);
            connection.Send(new SC_LobbyInteract
            {
                EKMDGLKBILM = request.EKMDGLKBILM,
                NGCBPMPCMMJ = connection.Account is null || heat.Get(connection.Account.Uid).FirstClaimed,
                ECGAHCPCDDI = request.ECGAHCPCDDI,
            });
        }

        public override void HandleLobbyTutorialInfo(CS_LobbyTutorialInfo request, Connection connection)
        {
                    connection.Send(1, new SC_LobbyTutorialInfo
                    {
                        Sends =
                        {
                            [100210] = true,
                            [100306] = true,
                            [100307] = true,
                            [100407] = true,
                            [200210] = true,
                            [200306] = true,
                            [200307] = true,
                            [200407] = true,
                        },
                        DIPIDKNFNPH =
                        {
                            [1001] = true,
                            [1002] = true,
                            [1003] = true,
                            [1004] = true,
                            [1005] = true,
                            [1006] = true,
                            [1007] = true,
                            [1008] = true,
                            [1010] = true,
                            [2002] = true,
                            [2003] = true,
                            [2004] = true,
                            [2005] = true,
                            [2006] = true,
                            [2007] = true,
                            [2008] = true,
                            [2010] = true,
                            [3007] = true,
                            [10031] = true,
                            [10041] = true,
                            [10051] = true,
                            [10081] = true,
                            [10091] = true,
                            [10101] = true,
                            [10102] = true,
                            [30100] = true,
                            [30200] = true,
                            [30300] = true,
                            [30310] = true,
                            [30410] = true,
                            [30510] = true,
                            [30610] = true,
                            [30710] = true,
                            [30810] = true,
                            [30900] = true,
                            [31000] = true,
                            [31010] = true,
                            [31110] = true,
                            [31210] = true,
                            [31310] = true,
                            [31910] = true,
                        },
                    });
        }

        public override void HandleLobbyTutorialFinish(CS_LobbyTutorialFinish request, Connection connection)
        {
            connection.Send(new SC_LobbyTutorialFinish
            {
                Groups = { request.Groups },
            });
        }

        public override void HandleLobbyTutorialSend(CS_LobbyTutorialSend request, Connection connection)
        {
            connection.Send(new SC_LobbyTutorialSend
            {
                KMHOOBIHPPE = { { request.PageId, 1u } },
                PageId = request.PageId,
            });
        }

        public override void HandleGetLobbyShareCodeIdx(CS_GetLobbyShareCodeIdx request, Connection connection)
        {
                    connection.Send(new SC_GetLobbyShareCodeIdx
                    {
                        BDMOEFJJFAM = { },
                    });
        }

        public override void HandleGetLobbyBgm(CS_GetLobbyBgm request, Connection connection)
        {
            connection.Send(new SC_GetLobbyBgm { JNHHAINDALC = 0 });
        }

        public override void HandleGetLobbyPhotoCut(CS_GetLobbyPhotoCut request, Connection connection)
        {
            SC_GetLobbyPhotoCut response = new SC_GetLobbyPhotoCut
            {
                OAGEAGJKLHB = { },
                BOKKHALJCMA = request.BOKKHALJCMA,
                INNLIAFIMIM = { },
            };

            if (request.BOKKHALJCMA == 1)
            {
                response.CCNDHNDHKBO.AddRange(new uint[] { 1, 2, 3, 4, 5, 6, 7, 53, 9, 10, 11, 12, 13, 14, 16, 17, 18, 19, 21, 22, 23, 24, 25, 26, 27, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 48, 49, 55, 56, 57, 58, 59, 60, 61, 62, 63, 64, 65, 66, 67, 69, 70, 71, 73, 74, 75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91 });
            }

            connection.Send(response);
        }

        // ---- welcome ceremony (欢迎仪式, "Cubierta de la tripulación - Ceremonia de bienvenida"): a doll greets the commander in a short cinematic (A-168)
        /// <summary>
        /// Client flow (read from the client): when the commander enters the crew deck the client plays the greeting of <c>JCOMGOPOLOP</c> (the doll the server
        /// chose, a row of <c>LobbyWelcomeData</c> col 2) unless the played flag (<c>HPNKLKLOECK</c>) is set or the system is locked; after it ran it sends
        /// <c>CS_LobbyWelcomeSettingPlayed</c>. So the server sets the flag false once per game day (05:00 CST) and true after the cinematic. The setting dialog
        /// edits: field 1 = random doll (true) or the chosen ones (false), the chosen dolls and a mode (1 = in the order listed, 0 = at random among them).
        /// </summary>
        private sealed class WelcomeSetting
        {
            /// <summary>Random doll among all the ones that have a greeting (the client names it "Random"; default).</summary>
            public bool Enabled { get; set; } = true;
            public List<uint> Guns { get; set; } = [];
            public uint Mode { get; set; }
            public long UpdatedAt { get; set; }
            /// <summary>Game day (GameDay.Key) the doll of the day was chosen / the cinematic was played.</summary>
            public string ChosenDay { get; set; } = string.Empty;
            public string PlayedDay { get; set; } = string.Empty;
            public uint Current { get; set; }
            public int Cursor { get; set; }
            public long PlayedAt { get; set; }
        }

        /// <summary>Dolls the account owns that have a greeting cinematic (<c>LobbyWelcomeData</c>).</summary>
        private List<uint> WelcomeCandidates(uint uid)
        {
            HashSet<uint> withGreeting = tableService.GetTable<LobbyWelcomeData>().Select(row => TableFields.U(row, 2)).ToHashSet();
            return (accountService.GetByUid(uid)?.Guns.Select(g => g.GunId) ?? []).Where(withGreeting.Contains).Distinct().OrderBy(id => id).ToList();
        }

        /// <summary>The doll that greets today: kept for the whole game day until the cinematic is played.</summary>
        private WelcomeSetting WelcomeOfToday(uint uid)
        {
            WelcomeSetting setting = accountState.Get<WelcomeSetting>(uid, "lobby.welcome") ?? new WelcomeSetting { UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            string today = GameDay.Key();
            List<uint> candidates = WelcomeCandidates(uid);
            if (setting.ChosenDay == today && candidates.Contains(setting.Current))
                return setting;

            List<uint> pool = setting.Enabled ? candidates : setting.Guns.Where(candidates.Contains).ToList();
            if (pool.Count == 0)
                pool = candidates;
            if (pool.Count == 0)
            {
                setting.Current = 0;
            }
            else if (!setting.Enabled && setting.Mode == 1)
            {
                setting.Current = pool[setting.Cursor % pool.Count];
            }
            else
            {
                setting.Current = pool[Random.Shared.Next(pool.Count)];
            }

            setting.ChosenDay = today;
            accountState.Set(uid, "lobby.welcome", setting);
            return setting;
        }

        private NMEMMFMPCJB WelcomeMessage(uint uid)
        {
            WelcomeSetting setting = WelcomeOfToday(uid);
            NMEMMFMPCJB message = new()
            {
                OIKBNGNPNIM = setting.Enabled,
                DHFNGECCLAK = setting.PlayedAt != 0 ? setting.PlayedAt : setting.UpdatedAt,
                JCOMGOPOLOP = setting.Current,
                // true = nothing to play (already played today, or no doll has a greeting)
                HPNKLKLOECK = setting.PlayedDay == GameDay.Key() || setting.Current == 0,
                NIBJKGCIDJK = new NMEMMFMPCJB.Types.CDECAJCOOPF { Mode = setting.Mode },
            };
            message.NIBJKGCIDJK.GGOLBAILKIO.AddRange(setting.Guns);
            return message;
        }

        public override void HandleLobbyWelcomeSettingInfo(CS_LobbyWelcomeSettingInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            connection.Send(new SC_LobbyWelcomeSettingInfo { OBOGLCLAIPH = WelcomeMessage(connection.Account.Uid) });
        }

        public override void HandleLobbyWelcomeSettingUpdate(CS_LobbyWelcomeSettingUpdate request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<uint> guns = request.MAHELIIIDDM.ToList();
            List<uint> owned = accountService.GetByUid(uid)?.Guns.Select(g => g.GunId).ToList() ?? [];
            if (guns.Distinct().Count() != guns.Count)
            {
                connection.Refuse(MsgId.MsgCsLobbyWelcomeSettingUpdate, HNINDAOJCKP.LobbyWelcomeGunIdsDuplicate);
                return;
            }

            if (guns.Any(g => !owned.Contains(g)))
            {
                connection.Refuse(MsgId.MsgCsLobbyWelcomeSettingUpdate, HNINDAOJCKP.LobbyWelcomeGunIdNotExist);
                return;
            }

            WelcomeSetting previous = accountState.Get<WelcomeSetting>(uid, "lobby.welcome") ?? new WelcomeSetting();
            accountState.Set(uid, "lobby.welcome", new WelcomeSetting
            {
                Enabled = request.OIKBNGNPNIM,
                Guns = guns,
                Mode = request.ENEKBLDCKHF,
                UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                // a new choice picks the doll of the day again (unless today's greeting already played)
                PlayedDay = previous.PlayedDay,
                PlayedAt = previous.PlayedAt,
                Cursor = previous.Cursor,
            });
            connection.Send(new SC_LobbyWelcomeSettingUpdate { OBOGLCLAIPH = WelcomeMessage(uid) });
        }

        /// <summary>The commander has seen these new events: acknowledged (nothing is stored; the events are rebuilt from the data).</summary>
        public override void HandleLobbyReadNewEvents(CS_LobbyReadNewEvents request, Connection connection) =>
            connection.Send(new SC_LobbyReadNewEvents { ICJPKOJLLFD = request });


        public override void HandleLobbyHandmadeGiftInfo(CS_LobbyHandmadeGiftInfo request, Connection connection)
        {
            connection.Send(new SC_LobbyHandmadeGiftInfo
            {
                KKLPCCBIFOM = { },
            });
        }

        public override void HandleGetLobbyHandmadeGiftMay(CS_GetLobbyHandmadeGiftMay request, Connection connection)
        {
            connection.Send(new SC_GetLobbyHandmadeGiftMay
            {
                MNGLCEKHNHE = { },
            });
        }

        public override void HandleGetLobbyHandmadeCommonGift(CS_GetLobbyHandmadeCommonGift request, Connection connection)
        {
            connection.Send(new SC_GetLobbyHandmadeCommonGift
            {
                KKLPCCBIFOM = { },
            });
        }

        public override void HandleGetLobbySchemeBriefV3(CS_GetLobbySchemeBriefV3 request, Connection connection)
        {
            connection.Send(new SC_GetLobbySchemeBriefV3
            {
                EDMELOEFEDA = new ENFPOEEMINP
                {
                    NCIJONLOPFC = { },
                },
            });
        }

        public override void HandleLobbyShareCodeV2TransformV3(CS_LobbyShareCodeV2TransformV3 request, Connection connection)
        {
            connection.Send(new SC_LobbyShareCodeV2TransformV3
            {
                HAOEIOLJDNP = request.HAOEIOLJDNP,
            });
        }

        public override void HandleLobbyCustomizeRoomInfo(CS_LobbyCustomizeRoomInfo request, Connection connection)
        {
            connection.Send(new SC_LobbyCustomizeRoomInfo
            {
                RoomId = request.RoomId,
                EGOMNOLAAID = new AFICNAJOLBB
                {
                    KFJDLALJNDI = NDHFANCENHD.Types.GLGHIGHBFOC.PhaseNone,
                },
            });
        }

        private sealed class PlacedGun
        {
            public uint GunId { get; set; }
            public uint Costume { get; set; }
            public uint Parts { get; set; }
            public uint Slot { get; set; }
            public long Time { get; set; }
            public long UpdatedAtMs { get; set; }
        }

        private const string LobbyRosterKey = "lobby.guns.roster";

        /// <summary>
        /// Placing characters in the lobby: costume, costume parts and slot (<c>PFIECLBKOMF</c>) of each requested character are stored
        /// (<c>lobby.guns</c>) and shown by <c>LobbyGetGunSetting</c>. Only owned characters can be placed. The three maps of the
        /// answer (the affection gained while away) are empty: this server does not accumulate lobby affection.
        /// </summary>
        public override void HandleLobbySetGun(CS_LobbySetGun request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<uint> owned = accountService.GetByUid(uid)?.Guns.Select(g => g.GunId).ToList() ?? [];
            uint[] selected = request.GKGDNPEIDHJ.Where(g => g.GunId != 0).Select(g => g.GunId).ToArray();
            if (selected.Any(gunId => !owned.Contains(gunId)) || selected.Distinct().Count() != selected.Length)
            {
                connection.Refuse(MsgId.MsgCsLobbySetGun, HNINDAOJCKP.LobbyWelcomeGunIdNotExist);
                return;
            }

            // The client sends the complete ordered activity-floor roster, including empty slots as entries with GunId=0.
            // Store that shape verbatim so the first six deployed positions and all remaining selectable characters survive
            // a fresh LobbyGetGunSetting request. Previously any empty slot was rejected and the next query rebuilt a fixed roster.
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long updatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            List<PlacedGun> roster = request.GKGDNPEIDHJ.Select(entry => new PlacedGun
            {
                GunId = entry.GunId,
                Costume = entry.CostumeId,
                Parts = entry.CostumeParts,
                Slot = entry.PFIECLBKOMF,
                Time = now,
                UpdatedAtMs = updatedAtMs,
            }).ToList();
            accountState.Set(uid, LobbyRosterKey, roster);

            Dictionary<uint, PlacedGun> placed = roster.Where(gun => gun.GunId != 0).ToDictionary(gun => gun.GunId);

            accountState.Set(uid, "lobby.guns", placed);
            connection.Send(new SC_LobbySetGun());
        }

        public override void HandleLobbyGetGunSetting(CS_LobbyGetGunSetting request, Connection connection)
        {
            uint nowUnix = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            SC_LobbyGetGunSetting gunSetting =
                new SC_LobbyGetGunSetting
                {
                    Guns =
                    {
                        new JBHAOOEHJOO { GunId = 1001, CostumeId = 1100100, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1008, CostumeId = 1100800, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1009, CostumeId = 1100900, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1013, CostumeId = 1101300, CostumeParts = 0, PFPHNDEDJHN = nowUnix, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1015, CostumeId = 1101500, CostumeParts = 0, PFPHNDEDJHN = nowUnix, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1017, CostumeId = 1101700, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1021, CostumeId = 1102100, CostumeParts = 0, PFPHNDEDJHN = nowUnix, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1022, CostumeId = 1102200, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1023, CostumeId = 1102300, CostumeParts = 0, PFPHNDEDJHN = nowUnix, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1024, CostumeId = 1102400, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1025, CostumeId = 1102501, CostumeParts = 0, PFPHNDEDJHN = nowUnix, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1026, CostumeId = 1102601, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1027, CostumeId = 1102700, CostumeParts = 0, PFPHNDEDJHN = nowUnix, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1028, CostumeId = 1102800, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1029, CostumeId = 1102900, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1032, CostumeId = 1103200, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1033, CostumeId = 1103300, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1034, CostumeId = 1103401, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1035, CostumeId = 1103500, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1036, CostumeId = 1103600, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1037, CostumeId = 1103700, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1038, CostumeId = 1103800, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1039, CostumeId = 1103901, CostumeParts = 273, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1040, CostumeId = 1104000, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1041, CostumeId = 1104100, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1042, CostumeId = 1104200, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1043, CostumeId = 1104300, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1044, CostumeId = 1104400, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1045, CostumeId = 1104500, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1047, CostumeId = 1104701, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1048, CostumeId = 1104800, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1049, CostumeId = 1104900, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1050, CostumeId = 1105000, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1051, CostumeId = 1105100, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1052, CostumeId = 1105203, CostumeParts = 273, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1053, CostumeId = 1105300, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1054, CostumeId = 1105401, CostumeParts = 273, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1055, CostumeId = 1105500, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1056, CostumeId = 1105600, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1057, CostumeId = 1105700, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1058, CostumeId = 1105800, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1059, CostumeId = 1105900, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1060, CostumeId = 1106000, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1061, CostumeId = 1106100, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1062, CostumeId = 1106200, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1063, CostumeId = 1106300, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1064, CostumeId = 1106400, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1065, CostumeId = 1106500, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1066, CostumeId = 1106600, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1067, CostumeId = 1106700, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1068, CostumeId = 1106800, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1069, CostumeId = 1106900, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1070, CostumeId = 1107000, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1071, CostumeId = 1107100, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1072, CostumeId = 1107200, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1073, CostumeId = 1107300, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1074, CostumeId = 1107400, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1075, CostumeId = 1107500, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1076, CostumeId = 1107600, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1077, CostumeId = 1107700, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                        new JBHAOOEHJOO { GunId = 1078, CostumeId = 1107800, CostumeParts = 0, PFPHNDEDJHN = 0, PFIECLBKOMF = 0 },
                    },
                };

            // placements saved by LobbySetGun override the fixed list
            if (connection.Account is not null)
            {
                // only characters of the account can be shown or chosen in the lobby: the fixed list above is the roster of a captured
                // account that owned every character, so entries of unowned characters are dropped and owned ones missing from it are added
                var owned = accountService.GetByUid(connection.Account.Uid)?.Guns.GroupBy(gun => gun.GunId).ToDictionary(group => group.Key, group => group.First()) ?? [];
                List<PlacedGun>? savedRoster = accountState.Get<List<PlacedGun>>(connection.Account.Uid, LobbyRosterKey);
                if (savedRoster is { Count: > 0 })
                {
                    gunSetting.Guns.Clear();
                    HashSet<uint> included = [];
                    foreach (PlacedGun saved in savedRoster)
                    {
                        if (saved.GunId == 0)
                        {
                            gunSetting.Guns.Add(new JBHAOOEHJOO());
                            continue;
                        }

                        if (!owned.ContainsKey(saved.GunId))
                            continue;

                        included.Add(saved.GunId);
                        gunSetting.Guns.Add(new JBHAOOEHJOO
                        {
                            GunId = saved.GunId,
                            CostumeId = saved.Costume,
                            CostumeParts = saved.Parts,
                            PFIECLBKOMF = saved.Slot,
                            PFPHNDEDJHN = saved.Time,
                        });
                    }

                    // Newly acquired dolls become available after the saved sequence, without shifting its deployed slots.
                    foreach ((uint gunId, var gun) in owned.OrderBy(pair => pair.Key))
                    {
                        if (!included.Contains(gunId))
                            gunSetting.Guns.Add(new JBHAOOEHJOO { GunId = gunId, CostumeId = gun.CostumeId, CostumeParts = gun.CostumeParts });
                    }
                }
                else
                {
                    Dictionary<uint, PlacedGun> placed = accountState.Get<Dictionary<uint, PlacedGun>>(connection.Account.Uid, "lobby.guns") ?? [];
                    foreach (JBHAOOEHJOO entry in gunSetting.Guns)
                    {
                        if (placed.TryGetValue(entry.GunId, out PlacedGun? saved))
                        {
                            entry.CostumeId = saved.Costume;
                            entry.CostumeParts = saved.Parts;
                            entry.PFIECLBKOMF = saved.Slot;
                            entry.PFPHNDEDJHN = saved.Time;
                        }
                    }

                    for (int i = gunSetting.Guns.Count - 1; i >= 0; i--)
                        if (!owned.ContainsKey(gunSetting.Guns[i].GunId))
                            gunSetting.Guns.RemoveAt(i);

                    foreach ((uint gunId, var gun) in owned.OrderBy(pair => pair.Key))
                        if (gunSetting.Guns.All(entry => entry.GunId != gunId))
                            gunSetting.Guns.Add(new JBHAOOEHJOO
                            {
                                GunId = gunId,
                                CostumeId = gun.CostumeId,
                                CostumeParts = gun.CostumeParts,
                            });
                }

                // The activity floor (活动层) has its own wardrobe state. DormSkinChange and the Lounge variant of
                // GunCostumeParts save this separately from the main-lobby placement in lobby.guns. The client reloads
                // the activity-floor models through LobbyGetGunSetting after EnterDorm, so overlay those saved clothes
                // and parts here while preserving the placement slot and timestamp. Without this, the captured default
                // costume was sent again and the floor appeared to forget the selected outfit on every re-entry.
                Dictionary<uint, uint> dormCostumes = accountState.Get<Dictionary<uint, uint>>(connection.Account.Uid, "dorm.costumes") ?? [];
                Dictionary<uint, long> dormCostumeTimes = accountState.Get<Dictionary<uint, long>>(connection.Account.Uid, "dorm.costumes.updatedAtMs") ?? [];
                Dictionary<uint, uint> dormParts = accountState.Get<Dictionary<uint, uint>>(connection.Account.Uid, DormHandler.PartsKey) ?? [];
                foreach (JBHAOOEHJOO entry in gunSetting.Guns)
                {
                    if (!dormCostumes.TryGetValue(entry.GunId, out uint costumeId))
                        continue;

                    PlacedGun? placement = null;
                    accountState.Get<Dictionary<uint, PlacedGun>>(connection.Account.Uid, "lobby.guns")?.TryGetValue(entry.GunId, out placement);
                    long dormUpdatedAt = dormCostumeTimes.GetValueOrDefault(entry.GunId);
                    long placementUpdatedAt = placement?.UpdatedAtMs ?? 0;
                    // Legacy dorm selections have no timestamp; keep them until a new placement is saved.
                    if (dormUpdatedAt == 0 ? placementUpdatedAt == 0 : dormUpdatedAt >= placementUpdatedAt)
                    {
                        entry.CostumeId = costumeId;
                        entry.CostumeParts = dormParts.GetValueOrDefault(costumeId, entry.CostumeParts);
                    }
                }
            }

            connection.Send(1, gunSetting);
        }

        /// <summary>
        /// The outfit (and parts) the player equips on a character in the activity floor. The real client equips an outfit with this request ALONE
        /// (<c>{gunId, costumeId}</c>, parts absent; log 2026-10-16 14:00), so the costume of the character is kept here (<c>dorm.costumes</c> with its time,
        /// the source <c>LobbyGetGunSetting</c>/<c>DormInfo</c> read) and the parts when they come (<c>dorm.parts</c>, costume -> parts). Before, only the parts were
        /// kept and the outfit was lost at every visit. ASSUMPTION A-42.
        /// </summary>
        public override void HandleLobbyGunCostumeParts(CS_LobbyGunCostumeParts request, Connection connection)
        {
            if (connection.Account is not null && request.CostumeId != 0)
            {
                uint uid = connection.Account.Uid;
                lock (AccountLock.For(uid))
                {
                    if (request.GunId != 0 && tableService.GetTable<ClothesData>().Any(c => c.Id == request.CostumeId && TableFields.U(c, 21) == request.GunId))
                    {
                        Dictionary<uint, uint> costumes = accountState.Get<Dictionary<uint, uint>>(uid, "dorm.costumes") ?? [];
                        costumes[request.GunId] = request.CostumeId;
                        accountState.Set(uid, "dorm.costumes", costumes);
                        Dictionary<uint, long> times = accountState.Get<Dictionary<uint, long>>(uid, "dorm.costumes.updatedAtMs") ?? [];
                        times[request.GunId] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        accountState.Set(uid, "dorm.costumes.updatedAtMs", times);
                    }

                    if (request.CostumeParts != 0)
                    {
                        Dictionary<uint, uint> parts = accountState.Get<Dictionary<uint, uint>>(uid, DormHandler.PartsKey) ?? [];
                        parts[request.CostumeId] = request.CostumeParts;
                        accountState.Set(uid, DormHandler.PartsKey, parts);
                    }
                }
            }

            connection.Send(new SC_LobbyGunCostumeParts());
        }

        public override void HandleLobbyWatchAvgDuo(CS_LobbyWatchAvgDuo request, Connection connection)
        {
            SC_LobbyWatchAvgDuo response = new SC_LobbyWatchAvgDuo
            {
                JLPAEKALOKI = request.JLPAEKALOKI,
            };
            response.Id.AddRange(request.Id);
            connection.Send(response);
        }

        // ---- garden (see ILobbyGardenService: rules from the help text of the game, field meanings are assumptions)
        public override void HandleLobbyPlantInfo(CS_LobbyPlantInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            connection.Send(new SC_LobbyPlantInfo { Info = garden.Info(connection.Account.Uid) });
        }

        public override void HandleLobbyPlantNew(CS_LobbyPlantNew request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (garden.Plant(connection.Account.Uid, request.JJGNOPPPPIB) is not { } info)
            {
                connection.Refuse(MsgId.MsgCsLobbyPlantNew, garden.LastError);
                return;
            }

            connection.Send(new SC_LobbyPlantNew { Info = info });
        }

        public override void HandleLobbyPlantWater(CS_LobbyPlantWater request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (garden.Water(connection.Account.Uid, request.JJGNOPPPPIB) is not { } result)
            {
                connection.Refuse(MsgId.MsgCsLobbyPlantWater, garden.LastError);
                return;
            }

            questProgress.AddCondition(connection.Account.Uid, conditionClass: 4110, link: "32017");
            AddHeat(connection, 99017);
            connection.Send(new SC_LobbyPlantWater { ICJPKOJLLFD = request, NPJLLHJBFBP = result.Vitality, HMPGFCICDPF = result.Time });
        }

        public override void HandleLobbyPlantAcce(CS_LobbyPlantAcce request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            if (garden.Fertilise(connection.Account.Uid, request.JJGNOPPPPIB, messages) is not { } result)
            {
                connection.Refuse(MsgId.MsgCsLobbyPlantAcce, garden.LastError);
                return;
            }

            messages.Add(new SC_LobbyPlantAcce { ICJPKOJLLFD = request, Exp = result.Exp, NPJLLHJBFBP = result.Vitality });
            connection.SendAutoEncrypted(messages.ToArray());
            AddHeat(connection, 99018);
        }

        public override void HandleLobbyPlantPick(CS_LobbyPlantPick request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            if (!garden.Harvest(connection.Account.Uid, request.JJGNOPPPPIB, messages))
            {
                connection.Refuse(MsgId.MsgCsLobbyPlantPick, garden.LastError);
                return;
            }

            messages.Add(new SC_LobbyPlantPick { ICJPKOJLLFD = request });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyPlantDig(CS_LobbyPlantDig request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (!garden.Dig(connection.Account.Uid, request.JJGNOPPPPIB))
            {
                connection.Refuse(MsgId.MsgCsLobbyPlantDig, garden.LastError);
                return;
            }

            connection.Send(new SC_LobbyPlantDig { ICJPKOJLLFD = request });
        }

        public override void HandleLobbyPlantRescue(CS_LobbyPlantRescue request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            if (garden.Rescue(connection.Account.Uid, request.JJGNOPPPPIB, messages) is not { } vitality)
            {
                connection.Refuse(MsgId.MsgCsLobbyPlantRescue, garden.LastError);
                return;
            }

            messages.Add(new SC_LobbyPlantRescue { ICJPKOJLLFD = request, NPJLLHJBFBP = vitality });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyPlantDress(CS_LobbyPlantDress request, Connection connection)
        {
            if (connection.Account is null)
                return;

            garden.Dress(connection.Account.Uid, request.CCOCGNFFIGD);
            connection.Send(new SC_LobbyPlantDress { ICJPKOJLLFD = request });
        }

        public override void HandleLobbyPlantSetPoint(CS_LobbyPlantSetPoint request, Connection connection)
        {
            if (connection.Account is null)
                return;

            connection.Send(new SC_LobbyPlantSetPoint { ICJPKOJLLFD = request, JKKJHINFLOE = garden.SetPoint(connection.Account.Uid, request.RoomId) ?? 0 });
        }

        /// <summary>Inviting a character to help has no effect on this server (acknowledged only).</summary>
        public override void HandleLobbyPlantInvite(CS_LobbyPlantInvite request, Connection connection) =>
            connection.Send(new SC_LobbyPlantInvite { ICJPKOJLLFD = request });

        // ---- upgrades and unlocks of the lobby state this server sends: the cafe, the kitchen buffs and every room are
        // already at their maximum / unlocked in it, so the honest answer to a request for more is the client's own error.
        public override void HandleLobbyStartCook(CS_LobbyStartCook request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = cooking.StartCook(connection.Account.Uid, request.Type, request.GunId, messages);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsLobbyStartCook, error);
                return;
            }

            (uint group, uint level, bool used) = cooking.Dinner(connection.Account.Uid);
            // Result (field 4) is read by the client as `quality = result - 1` (0 Good, 1 Normal, 2 Bad); left at 0 it becomes -1 and the cooking
            // timeline never ends (the character stays frozen at the stove; real client 2026-10-16 14:19). A dinner always turns out well: 1. ASSUMPTION A-43.
            messages.Add(new SC_LobbyStartCook { Type = request.Type, GunId = request.GunId, KFNCPJPPGEF = group, Result = 1, MGIKONEONBF = level, LHGLNJNFIPM = used });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyDishCook(CS_LobbyDishCook request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = cooking.Cook(connection.Account.Uid, request.BJIMNEJBOEJ.ToList(), request.GunId, request.PPHNECAAJPA, messages);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsLobbyDishCook, error);
                return;
            }

            questProgress.AddCondition(connection.Account.Uid, conditionClass: 3621, link: "32007");
            connection.SendAutoEncrypted(messages.ToArray());
            AddHeat(connection, 99019);
        }

        public override void HandleLobbyDishGift(CS_LobbyDishGift request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = cooking.Gift(connection.Account.Uid, request, messages);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsLobbyDishGift, error);
                return;
            }

            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyDishIndexClaim(CS_LobbyDishIndexClaim request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = cooking.ClaimIndex(connection.Account.Uid, request.BJIMNEJBOEJ.ToList(), messages);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsLobbyDishIndexClaim, error);
                return;
            }

            SC_LobbyDishIndexClaim response = new();
            response.BJIMNEJBOEJ.AddRange(request.BJIMNEJBOEJ.Distinct());
            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyDishIndexMilestoneClaim(CS_LobbyDishIndexMilestoneClaim request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = cooking.ClaimMilestone(connection.Account.Uid, request.GroupId, messages, out uint level);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsLobbyDishIndexMilestoneClaim, error);
                return;
            }

            messages.Add(new SC_LobbyDishIndexMilestoneClaim { GroupId = request.GroupId, Level = level });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyDishIndexFavorite(CS_LobbyDishIndexFavorite request, Connection connection)
        {
            if (connection.Account is null)
                return;

            HNINDAOJCKP error = cooking.Favorite(connection.Account.Uid, request.ItemId, out bool favorite);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsLobbyDishIndexFavorite, error);
                return;
            }

            connection.Send(new SC_LobbyDishIndexFavorite { ItemId = request.ItemId, BGJNHNFFEKK = favorite });
        }

        public override void HandleLobbyCafeMakeCoffee(CS_LobbyCafeMakeCoffee request, Connection connection)
        {
            if (connection.Account is null)
                return;

            HNINDAOJCKP error = cooking.MakeCoffee(connection.Account.Uid, request.Type, out uint buff);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsLobbyCafeMakeCoffee, error);
                return;
            }

            connection.Send(new SC_LobbyCafeMakeCoffee { Type = request.Type, FMICMDLIOJD = buff });
        }

        public override void HandleLobbyCafeUpgrade(CS_LobbyCafeUpgrade request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsLobbyCafeUpgrade, HNINDAOJCKP.LobbyCafeBuffLevelMax);

        // Coffee kind / dinner buff group level-ups: the cost is the one of the target level row of the client tables (see ILobbyCookingService).
        public override void HandleLobbyCafeCoffeeUpgrade(CS_LobbyCafeCoffeeUpgrade request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = cooking.UpgradeCoffee(connection.Account.Uid, request.Type, messages, out uint level);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsLobbyCafeCoffeeUpgrade, error);
                return;
            }

            messages.Add(new SC_LobbyCafeCoffeeUpgrade { Type = request.Type, JOMLDNKALNP = level });
            connection.SendAutoEncrypted(messages.ToArray());
            AddHeat(connection, 99013);
        }

        public override void HandleLobbyCookBuffUpgrade(CS_LobbyCookBuffUpgrade request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            HNINDAOJCKP error = cooking.UpgradeDinner(connection.Account.Uid, request.Type, messages, out uint level);
            if (error != HNINDAOJCKP.None)
            {
                connection.Refuse(MsgId.MsgCsLobbyCookBuffUpgrade, error);
                return;
            }

            messages.Add(new SC_LobbyCookBuffUpgrade { Type = request.Type, JOMLDNKALNP = level });
            connection.SendAutoEncrypted(messages.ToArray());
            AddHeat(connection, 99010);
        }

        private const string RoomsKey = "lobby.rooms";

        /// <summary>The rooms the build info lists from the start (the others are cleaned and paid for from the build list).</summary>
        private static readonly uint[] StartingRooms = [101, 102, 103, 104, 106, 107, 109, 201, 202];

        /// <summary>
        /// "Clean" a room of the build list (洗浴间...): the price is <c>LobbyRoomData</c> column 21 (item, amount; 30 x item 202 for the bathroom) and the room is kept in
        /// <c>lobby.rooms</c> and listed in the build info (A-103). It used to be refused with "already unlocked" (error -18008, "undefined:18008" in the client).
        /// </summary>
        public override void HandleLobbyUnlockRoom(CS_LobbyUnlockRoom request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            IMessage? room = tableService.GetTable<LobbyRoomData>().FirstOrDefault(r => TableFields.U(r, 1) == request.IAAPPCGFLMD);
            List<uint> unlocked = accountState.Get<List<uint>>(uid, RoomsKey) ?? [];
            if (room is null || StartingRooms.Contains(request.IAAPPCGFLMD) || unlocked.Contains(request.IAAPPCGFLMD))
            {
                connection.Refuse(MsgId.MsgCsLobbyUnlockRoom, room is null ? HNINDAOJCKP.LobbyRoomLocked : HNINDAOJCKP.LobbyRoomAlreadyUnlocked);
                return;
            }

            List<IMessage> messages = [];
            Dictionary<uint, int> cost = TableFields.Map(room, 21).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => (int)p.Value);
            if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
            {
                connection.Refuse(MsgId.MsgCsLobbyUnlockRoom, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            unlocked.Add(request.IAAPPCGFLMD);
            accountState.Set(uid, RoomsKey, unlocked);
            messages.Add(new SC_LobbyUnlockRoom { IAAPPCGFLMD = request.IAAPPCGFLMD, Info = new OICGGHHHFBI() });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>Level rewards (see <see cref="ILobbyProgressService"/>).</summary>
        public override void HandleLobbyClaimLevelReward(CS_LobbyClaimLevelReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            IReadOnlyList<uint> done = lobbyProgress.Claim(connection.Account.Uid, request.OPEIANKNJDK, messages, out HNINDAOJCKP error);
            if (done.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsLobbyClaimLevelReward, error);
                return;
            }

            SC_LobbyClaimLevelReward response = new();
            response.CFOCLIHKKJL.AddRange(done);
            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyGetWeeklyQuestReward(CS_LobbyGetWeeklyQuestReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            IReadOnlyList<uint> done = heat.ClaimWeekly(connection.Account.Uid, [request.RewardId], messages, out HNINDAOJCKP error);
            if (done.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsLobbyGetWeeklyQuestReward, error);
                return;
            }

            messages.Add(new SC_LobbyGetWeeklyQuestReward { RewardId = request.RewardId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyGetAllWeeklyQuestReward(CS_LobbyGetAllWeeklyQuestReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            LobbyHeatSnapshot snapshot = heat.Get(uid);
            IEnumerable<uint> reached = tableService.GetTable<LobbyWeeklyRewardData>()
                .Where(r => snapshot.Week >= TableFields.U(r, 2))
                .Select(r => TableFields.U(r, 1));
            List<IMessage> messages = [];
            IReadOnlyList<uint> done = heat.ClaimWeekly(uid, reached, messages, out HNINDAOJCKP error);
            if (done.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsLobbyGetAllWeeklyQuestReward, error);
                return;
            }

            SC_LobbyGetAllWeeklyQuestReward reply = new();
            reply.RewardId.AddRange(done);
            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        // ---- affection earned while a character is in the lobby: this server does not accumulate it (see LobbySetGun), so a claim
        // gives nothing (0 gained) and restarts the clock; no value is invented.
        public override void HandleLobbyClaimSingleLove(CS_LobbyClaimSingleLove request, Connection connection) =>
            connection.Send(new SC_LobbyClaimSingleLove { GunId = request.GunId, PBAHFGJLCEJ = 0, PLNPMMBGMLB = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        public override void HandleLobbyClaimAllLove(CS_LobbyClaimAllLove request, Connection connection)
        {
            SC_LobbyClaimAllLove response = new();
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (connection.Account is not null)
                foreach (uint gun in (accountState.Get<Dictionary<uint, PlacedGun>>(connection.Account.Uid, "lobby.guns") ?? []).Keys)
                {
                    response.CIMBODLBILO[gun] = 0;
                    response.PLNPMMBGMLB[gun] = now;
                }

            connection.Send(response);
        }

        // 3d dorm lobby missing handler impl starts here
        public override void HandleLobbyPartyInfo(CS_LobbyPartyInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            connection.Send(7, parties.BuildInfo(connection.Account.Uid));
        }

        /// <summary>
        /// Joins a party (A-128): it becomes the current party of the account and the client, which only reads the answer to re-ask the info, finds in it the progress
        /// of the party from the tables (sections, nodes done and open) instead of the empty room it got when the server just repeated the request.
        /// </summary>
        public override void HandleLobbyPartyJoin(CS_LobbyPartyJoin request, Connection connection)
        {
            if (connection.Account is null)
                return;

            parties.Join(connection.Account.Uid, request.HCKMOMCKFEA, request.DOGDHOGHFNH?.SectionId ?? 0);
            connection.Send(new SC_LobbyPartyJoin { HCKMOMCKFEA = request.HCKMOMCKFEA, DOGDHOGHFNH = request.DOGDHOGHFNH });
        }

        public override void HandleLobbyPartyNodeCommit(CS_LobbyPartyNodeCommit request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var messages = new List<Google.Protobuf.IMessage>();
            if (!parties.Commit(connection.Account.Uid, request.HCKMOMCKFEA, request.SectionId, request.JBEONCNOEEA, messages, out SC_LobbyPartyNodeCommit? reply))
            {
                connection.Refuse(MsgId.MsgCsLobbyPartyNodeCommit, HNINDAOJCKP.ActivityMiniGameStatusInvalid);
                return;
            }

            messages.Insert(0, reply!);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyPartyAvgSelect(CS_LobbyPartyAvgSelect request, Connection connection)
        {
            if (connection.Account is not null)
                parties.SaveAvgSelect(connection.Account.Uid, request.HCKMOMCKFEA, request.AvgId, request.NIFHINAFJJG);
            connection.Send(new SC_LobbyPartyAvgSelect { HCKMOMCKFEA = request.HCKMOMCKFEA, AvgId = request.AvgId, NIFHINAFJJG = request.NIFHINAFJJG });
        }

        public override void HandleLobbyPartyLeave(CS_LobbyPartyLeave request, Connection connection)
        {
            if (connection.Account is not null)
                parties.Leave(connection.Account.Uid);

            connection.Send(new SC_LobbyPartyLeave());
        }

        // HandleDarkZoneGetEthnicInfo 

        // HandleDarkZoneGetUnlockEggStage 

        public override void HandleLobbyBirthdayCareInfo(CS_LobbyBirthdayCareInfo request, Connection connection)
        {
            connection.Send(new SC_LobbyBirthdayCareInfo
            {
                IJEOPLAPLIB = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                MICAPNGJBPH = false,
            });
        }

        public override void HandleLobbyMessageBoardInfo(CS_LobbyMessageBoardInfo request, Connection connection)
        {
            uint nowUnix = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            connection.Send(new SC_LobbyMessageBoardInfo
            {
                CECNOCLMAOG = new BNCNOLPBNBN
                {
                    Message = new OGKLGOMMLIM
                    {
                        Timestamp = nowUnix,
                        BNAJBJBNIAK = 248,
                    },
                    LMFLEGLPBPG =
                    {
                        [1] = 3,
                    },
                },
                GFOMBFKMPGC = { },
                GLCJKEFJOLA =
                {
                    [77] = new OGKLGOMMLIM { Timestamp = nowUnix, BNAJBJBNIAK = 77 },
                    [235] = new OGKLGOMMLIM { Timestamp = nowUnix, BNAJBJBNIAK = 235 },
                    [236] = new OGKLGOMMLIM { Timestamp = nowUnix, BNAJBJBNIAK = 236 },
                    [248] = new OGKLGOMMLIM { Timestamp = nowUnix, BNAJBJBNIAK = 248 },
                },
            });
        }

        public override void HandleLobbyMessageBoardLike(CS_LobbyMessageBoardLike request, Connection connection)
        {
            connection.Send(new SC_LobbyMessageBoardLike { Timestamp = request.Timestamp, DOIFGIIIKJD = request.DOIFGIIIKJD });
            if (request.DOIFGIIIKJD)
                AddHeat(connection, 99007);
        }

        public override void HandleLobbyMessageBoardCreate(CS_LobbyMessageBoardCreate request, Connection connection)
        {
            uint nowUnix = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            BNCNOLPBNBN board = new BNCNOLPBNBN
            {
                Message = new OGKLGOMMLIM
                {
                    Timestamp = nowUnix,
                    BNAJBJBNIAK = 248,
                },
            };
            if (request.LMFLEGLPBPG.Count > 0)
            {
                foreach (var entry in request.LMFLEGLPBPG)
                    board.LMFLEGLPBPG[entry.Key] = entry.Value;
            }
            else
            {
                board.LMFLEGLPBPG[1] = 3;
            }

            connection.Send(new SC_LobbyMessageBoardCreate
            {
                CECNOCLMAOG = board,
                AMADPLLHFDD = true,
            });
            AddHeat(connection, 99020);
        }

        public override void HandleLobbyDurabilityInfo(CS_LobbyDurabilityInfo request, Connection connection)
        {
            connection.Send(new SC_LobbyDurabilityInfo
            {
                Info = new IHADGDPBCLM
                {
                    MCKEKMMKFFA = 2,
                    LIMKACAINNP =
                    {
                        [4000001] = new IHADGDPBCLM.Types.GPIHNEINDJJ
                        {
                            Status = IHADGDPBCLM.Types.KJBJEIPOBFN.EventStatusRefresh,
                            MHCGGLDOKKL = 0,
                        },
                        [4000005] = new IHADGDPBCLM.Types.GPIHNEINDJJ
                        {
                            Status = IHADGDPBCLM.Types.KJBJEIPOBFN.EventStatusRefresh,
                            MHCGGLDOKKL = 0,
                        },
                        [4000007] = new IHADGDPBCLM.Types.GPIHNEINDJJ
                        {
                            Status = IHADGDPBCLM.Types.KJBJEIPOBFN.EventStatusRefresh,
                            MHCGGLDOKKL = 0,
                        },
                    },
                    MCJCANKLOAP = 1787185299,
                },
            });
        }

        private const string AvatarKey = "lobby.avatar";

        private sealed class AvatarState
        {
            public uint Avatar { get; set; }
            public uint Clothes { get; set; } = 1002;
            public uint Type { get; set; }
        }

        /// <summary>
        /// The avatar the player controls in the activity floor ("image management"): 0 = the commander, a character id, or an NPC of <c>LobbyNpcData</c>
        /// (10001 Mayling, 10002 Helena). Client layout (read in the protocol classes): the avatar message is (1 id, 2 clothes id, 3 TYPE enum
        /// <c>LobbyAvatarType</c>: 0 character, 1 NPC); the switch request and answer are (1 id, 2 type). The server protos lacked the type, so every NPC was
        /// loaded as a character ("table id 10002 not found") and the loading froze at 87% (user reports 13:07 and 13:43). The choice is kept in
        /// <c>lobby.avatar</c> (A-41).
        /// </summary>
        public override void HandleLobbyAvatarInfo(CS_LobbyAvatarInfo request, Connection connection)
        {
            AvatarState state = connection.Account is null ? new AvatarState() : accountState.Get<AvatarState>(connection.Account.Uid, AvatarKey) ?? new AvatarState();
            // a character the account does not own is loaded without animations (T-pose): the commander (0) is the default and the fallback
            if (connection.Account is not null && state.Avatar != 0 && tableService.GetTable<LobbyNpcData>().All(n => TableFields.U(n, 1) != state.Avatar)
                && connection.Account.Guns.All(g => g.GunId != state.Avatar))
                state = new AvatarState { Clothes = state.Clothes };
            connection.Send(new SC_LobbyAvatarInfo { HIOGBPMJGON = new CGLPDAABOEP { MPFFENCOPFP = state.Avatar, OCCAHBFCHNP = state.Clothes, AvatarType = state.Type != 0 || state.Avatar == 0 || tableService.GetTable<LobbyNpcData>().All(n => TableFields.U(n, 1) != state.Avatar) ? state.Type : 1u } });
        }

        public override void HandleLobbyAvatarSwitch(CS_LobbyAvatarSwitch request, Connection connection)
        {
            if (connection.Account is not null)
            {
                lock (AccountLock.For(connection.Account.Uid))
                {
                    AvatarState state = accountState.Get<AvatarState>(connection.Account.Uid, AvatarKey) ?? new AvatarState();
                    state.Avatar = request.MPFFENCOPFP;
                    state.Type = request.OCCAHBFCHNP;
                    accountState.Set(connection.Account.Uid, AvatarKey, state);
                }
            }

            connection.Send(new SC_LobbyAvatarSwitch { MPFFENCOPFP = request.MPFFENCOPFP, OCCAHBFCHNP = request.OCCAHBFCHNP });
        }

        public override void HandleLobbyAvatarClotheChange(CS_LobbyAvatarClotheChange request, Connection connection)
        {
            if (connection.Account is not null && request.PPKOJNBJJFH != 0)
            {
                lock (AccountLock.For(connection.Account.Uid))
                {
                    AvatarState state = accountState.Get<AvatarState>(connection.Account.Uid, AvatarKey) ?? new AvatarState();
                    state.Clothes = request.PPKOJNBJJFH;
                    accountState.Set(connection.Account.Uid, AvatarKey, state);
                }
            }

            connection.Send(new SC_LobbyAvatarClotheChange { PPKOJNBJJFH = request.PPKOJNBJJFH });
        }

        public override void HandleGetLobbyMeilingConfig(CS_GetLobbyMeilingConfig request, Connection connection)
        {
            // 402..405 until the player changes the selection with SetLobbyMeilingConfig (stored by PreferenceResponder).
            CS_SetLobbyMeilingConfig? chosen = connection.Account is null ? null
                : PreferenceResponder.Recall<CS_SetLobbyMeilingConfig>(accountState, connection.Account.Uid, "SetLobbyMeilingConfig");
            SC_GetLobbyMeilingConfig reply = new();
            if (chosen is not null)
                reply.ECGAHCPCDDI.AddRange(chosen.ECGAHCPCDDI);
            else
                reply.ECGAHCPCDDI.AddRange([402u, 403u, 404u, 405u]);
            connection.Send(reply);
        }

        public override void HandleLobbyWelcomeSettingPlayed(CS_LobbyWelcomeSettingPlayed request, Connection connection)
        {
            if (connection.Account is null)
                return;

            // the cinematic ran: no more greetings until the next game day; in the "in order" mode the next day starts with the next doll
            uint uid = connection.Account.Uid;
            WelcomeSetting setting = WelcomeOfToday(uid);
            if (setting.PlayedDay != GameDay.Key())
            {
                setting.PlayedDay = GameDay.Key();
                setting.PlayedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                setting.Cursor++;
                accountState.Set(uid, "lobby.welcome", setting);
            }

            connection.Send(new SC_LobbyWelcomeSettingPlayed { OBOGLCLAIPH = WelcomeMessage(uid) });
        }

        public override void HandleLobbyCancelEvent(CS_LobbyCancelEvent request, Connection connection)
        {
            SC_LobbyCancelEvent response = new SC_LobbyCancelEvent();
            response.MNDDGCLCMDP.AddRange(request.MNDDGCLCMDP);
            connection.Send(response);
        }

        /// <summary>
        /// A lobby event (<c>LobbyEventData</c>, col 17 = reward list: lobby experience, tokens, gold) was played to its end. ASSUMPTION (A-18): the reward
        /// is paid once per event and game day, the answer repeats the event id.
        /// </summary>
        public override void HandleLobbyFinishEvent(CS_LobbyFinishEvent request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            IMessage? row = tableService.GetTable<LobbyEventData>().FirstOrDefault(r => TableFields.U(r, 1) == request.EventId);
            List<IMessage> messages = [];
            if (row is not null)
            {
                Dictionary<string, List<uint>> done = accountState.Get<Dictionary<string, List<uint>>>(uid, "lobby.events.done") ?? [];
                string day = GameDay.Key();
                done = done.Where(p => p.Key == day).ToDictionary(p => p.Key, p => p.Value);
                List<uint> today = done.GetValueOrDefault(day) ?? [];
                if (!today.Contains(request.EventId))
                {
                    foreach ((uint item, uint count) in TableFields.Map(row, 17))
                        rewardService.Grant(uid, item, (int)count, messages);
                    today.Add(request.EventId);
                    done[day] = today;
                    accountState.Set(uid, "lobby.events.done", done);
                }
            }

            messages.Add(new SC_LobbyFinishEvent { EventId = request.EventId });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>Buys a mini game of the lobby (see <see cref="ILobbyMiniGameService.Pay"/>).</summary>
        public override void HandleLobbyMiniGamePay(CS_LobbyMiniGamePay request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            if (!miniGames.Pay(connection.Account.Uid, request.LAOKONCHCHL, request.KMEFAJLEJHK, request.ObjectId, messages, out HMDODOBKKND? info))
            {
                connection.Refuse(MsgId.MsgCsLobbyMiniGamePay, info is null && tableService.GetTable<LobbyMinigameGamelistData>().All(g => g.Id != request.LAOKONCHCHL)
                    ? HNINDAOJCKP.ItemConfigNotExist
                    : HNINDAOJCKP.ItemNotEnough);
                return;
            }

            messages.Add(new SC_LobbyMiniGamePay { LAOKONCHCHL = request.LAOKONCHCHL, KMEFAJLEJHK = request.KMEFAJLEJHK, ObjectId = request.ObjectId, FJEINOBOEOI = info });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>End of a mini game run (see <see cref="ILobbyMiniGameService.Finish"/>).</summary>
        public override void HandleLobbyMiniGameFinish(CS_LobbyMiniGameFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;

            // a finish needs the start of that run (same game and stage), otherwise it is refused instead of recording a score
            if (!miniGames.CanFinish(connection.Account.Uid, request))
            {
                connection.Refuse(MsgId.MsgCsLobbyMiniGameFinish, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            // the answer goes first; the items the run collected and the achievements it completed follow
            List<IMessage> messages = [];
            SC_LobbyMiniGameFinish reply = miniGames.Finish(connection.Account.Uid, request, messages);
            messages.Insert(0, reply);
            messages.Add(new SC_CommonQuestCountUpdate { Counters = miniGames.Counters(connection.Account.Uid) });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleLobbyTriggerEvent(CS_LobbyTriggerEvent request, Connection connection)
        {
            connection.Send(new SC_LobbyTriggerEvent
            {
                ABAPKEKAPCJ = new KMIAGEBIMIP
                {
                    EventId = request.HIOILHEPJCK,
                },
                INCNFOFGJFB = { },
            });
        }

        public override void HandleLobbyAsmrBegin(CS_LobbyAsmrBegin request, Connection connection)
        {
            connection.Send(new SC_LobbyAsmrBegin
            {
                GunId = request.GunId,
            });
        }

        // ---- wedding ASMR: watch it first (stored), then claim its reward (the furniture of LobbyWeddingData column 10) once
        public override void HandleLobbyWeddingAsmrWatched(CS_LobbyWeddingAsmrWatched request, Connection connection)
        {
            if (connection.Account is not null)
            {
                uint uid = connection.Account.Uid;
                Dictionary<uint, bool> watched = accountState.Get<Dictionary<uint, bool>>(uid, "lobby.wedding") ?? [];
                watched.TryAdd(request.CIJJNNGKHLB, false);
                accountState.Set(uid, "lobby.wedding", watched);
            }

            connection.Send(new SC_LobbyWeddingAsmrWatched { CIJJNNGKHLB = request.CIJJNNGKHLB });
        }

        public override void HandleLobbyWeddingAsmrClaimReward(CS_LobbyWeddingAsmrClaimReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Dictionary<uint, bool> watched = accountState.Get<Dictionary<uint, bool>>(uid, "lobby.wedding") ?? [];
            if (!watched.TryGetValue(request.CIJJNNGKHLB, out bool claimed))
            {
                connection.Refuse(MsgId.MsgCsLobbyWeddingAsmrClaimReward, HNINDAOJCKP.LobbyWeddingAsmrhaveNotWatched);
                return;
            }

            if (claimed)
            {
                connection.Refuse(MsgId.MsgCsLobbyWeddingAsmrClaimReward, HNINDAOJCKP.LobbyWeddingAsmrhaveClaimedReward);
                return;
            }

            IMessage? row = tableService.GetTable<LobbyWeddingData>().FirstOrDefault(w => TableFields.U(w, 1) == request.CIJJNNGKHLB);
            List<IMessage> messages = [];
            if (row is not null)
                rewardService.Grant(uid, TableFields.U(row, 10), 1, messages);

            watched[request.CIJJNNGKHLB] = true;
            accountState.Set(uid, "lobby.wedding", watched);
            messages.Add(new SC_LobbyWeddingAsmrClaimReward { CIJJNNGKHLB = request.CIJJNNGKHLB });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        // ---- durability / handwritten gift / party node: confirmed with their (empty) state; no rule or reward data is known for them
        public override void HandleLobbyDurabilityFinishEvent(CS_LobbyDurabilityFinishEvent request, Connection connection) =>
            connection.Send(new SC_LobbyDurabilityFinishEvent { Info = new IHADGDPBCLM() });

        public override void HandleLobbyDurabilityFinishNode(CS_LobbyDurabilityFinishNode request, Connection connection) =>
            connection.Send(new SC_LobbyDurabilityFinishNode { EventId = request.EventId, CIBAPJPIMCE = new IHADGDPBCLM.Types.GPIHNEINDJJ() });

        public override void HandleLobbyDurabilityReadFreshEvent(CS_LobbyDurabilityReadFreshEvent request, Connection connection) =>
            connection.Send(new SC_LobbyDurabilityReadFreshEvent());

        // the request has no reply message
        public override void HandleLobbyHandmadeGiftUse(CS_LobbyHandmadeGiftUse request, Connection connection) { }

        public override void HandleLobbyHandwrittenGiftGetWords(CS_LobbyHandwrittenGiftGetWords request, Connection connection) =>
            connection.Send(new SC_LobbyHandwrittenGiftGetWords { IMABPMACCPJ = request.IMABPMACCPJ, MLOJEAFEBIF = string.Empty });

    }
}
