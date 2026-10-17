using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Table;
using Griffin.Database.Services;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Control room: dispatches of characters to places (<c>ControlRoomDispatchPlaceData</c>: column 5 = reward, 7 = duration in
    /// seconds, 8 = characters needed, 9 = control room level that unlocks it), the dispatch-count milestones
    /// (<c>ControlRoomDispatchTotalRewardData</c>: 2 = dispatches needed, 3 = reward) and the room level
    /// (<c>ControlRoomData</c>: row of the level = cost to reach it in column 3). State is stored per account under
    /// <c>controlroom</c>. Assumptions: only the base reward of a place is paid (the extra column needs the duty tags of the
    /// characters, which the server does not evaluate). Production: <c>ControlRoomMaterialData</c> (item → column 2 = amount per day
    /// and column 3 = storage cap, both by room level); the amount produced is counted from the moment the item is chosen and
    /// withdrawn with <c>WithdrawGainItem</c> (the unlock item of column 4 is not required). Stamina: the room accumulates it
    /// (<c>ControlRoomData</c> row of the level: column 4 = production, column 5 = storage cap; ASSUMPTION: column 4 is per MINUTE, which
    /// fills level 1 in two hours) and <c>WithdrawStamina</c> moves it to the commander's stamina (item 101). Recycling is not simulated.
    /// <c>GRIFFIN_CONTROLROOM_TIME_SCALE</c> speeds the clocks up (tests use it; default 1).
    /// </summary>
    public sealed class ControlRoomHandler(
        IAccountStateService accountState,
        IAccountService accountService,
        ITableService tableService,
        IRewardService rewardService,
        IItemUseService itemUse,
        IStaminaService staminaService,
        IDailyQuestProgressService questProgress) : ControlRoomHandlerBase
    {
        private const string StateKey = "controlroom";

        public sealed class RoomState
        {
            public uint Level { get; set; } = 1;
            /// <summary>The material the room produces (0 = none), the amount waiting to be withdrawn and when it was last counted.</summary>
            public uint GainItem { get; set; }
            public double GainStored { get; set; }
            public long GainTick { get; set; }
            /// <summary>Stamina waiting in the room and when it was last counted.</summary>
            public double StaminaStored { get; set; }
            public long StaminaTick { get; set; }
            public uint Completed { get; set; }
            public List<uint> ClaimedTotals { get; set; } = [];
            public Dictionary<uint, Dispatch> Dispatches { get; set; } = [];
        }

        public sealed class Dispatch
        {
            public List<uint> Guns { get; set; } = [];
            public long Start { get; set; }
        }

        private static readonly double TimeScale = double.TryParse(Environment.GetEnvironmentVariable("GRIFFIN_CONTROLROOM_TIME_SCALE"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale) && scale > 0 ? scale : 1;

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        private RoomState Load(uint uid) => accountState.Get<RoomState>(uid, StateKey) ?? new RoomState();

        /// <summary>
        /// The state keeps real (UTC) seconds, but the client computes elapsed times against ITS clock (<c>SC_Sync</c>, the game clock, months behind
        /// the real one): a real timestamp made "stored + (clientNow - refresh)" hugely negative, and the stamina purchase dialog (which reads the
        /// stored stamina of the room) crashed with a stack overflow. Every timestamp sent is re-expressed in the client's clock, keeping the
        /// elapsed time. ASSUMPTION A-52 (the crash cause is inferred from the data; the dialog is not verified in the client).
        /// </summary>
        private static long ToGame(Connection connection, long realSeconds) => connection.GameClockSeconds() - Math.Max(0, Now() - realSeconds);

        private static ALPADFHJFBJ ToProto(Connection connection, Dispatch dispatch)
        {
            ALPADFHJFBJ proto = new() { StartTime = ToGame(connection, dispatch.Start) };
            proto.GGOLBAILKIO.AddRange(dispatch.Guns);
            return proto;
        }

        private IMessage? Place(uint id) => tableService.GetTable<ControlRoomDispatchPlaceData>().FirstOrDefault(p => TableFields.U(p, 1) == id);

        private IMessage? Material(uint item) => tableService.GetTable<ControlRoomMaterialData>().FirstOrDefault(m => TableFields.U(m, 1) == item);

        private static uint ByLevel(IMessage material, int column, uint level) =>
            TableFields.Map(material, column).Where(pair => pair.Key == level).Select(pair => pair.Value).FirstOrDefault();

        /// <summary>Counts the production since the last tick (capped by the storage of the room level).</summary>
        private void Settle(RoomState state)
        {
            long now = Now();
            if (state.GainItem != 0 && Material(state.GainItem) is { } material)
            {
                double perSecond = ByLevel(material, 2, state.Level) * TimeScale / 86400.0;
                double cap = ByLevel(material, 3, state.Level);
                state.GainStored = Math.Min(cap, state.GainStored + Math.Max(0, now - state.GainTick) * perSecond);
            }

            state.GainTick = now;

            IMessage? room = tableService.GetTable<ControlRoomData>().FirstOrDefault(r => TableFields.U(r, 1) == state.Level);
            if (room is not null)
            {
                double perMinute = TableFields.Double(room, 4);
                double cap = TableFields.U(room, 5);
                long from = state.StaminaTick == 0 ? now : state.StaminaTick;
                state.StaminaStored = Math.Min(cap, state.StaminaStored + Math.Max(0, now - from) * perMinute * TimeScale / 60.0);
                state.StaminaTick = now;
            }
        }

        public override void HandleControlRoomWithdrawStamina(CS_ControlRoomWithdrawStamina request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            RoomState state = Load(uid);
            Settle(state);
            if (request.Num == 0 || state.StaminaStored < request.Num)
            {
                connection.Refuse(MsgId.MsgCsControlRoomWithdrawStamina, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            state.StaminaStored -= request.Num;
            accountState.Set(uid, StateKey, state);
            StaminaBalance balance = staminaService.Add(uid, (int)request.Num, StaminaService.StartingValue, connection.GameClockSeconds());
            connection.SendRawPacket((ushort)MsgId.MsgScStaminaTypeResourceUpdate, StaminaService.BuildUpdatePayload(101, balance));
            connection.Send(new SC_ControlRoomWithdrawStamina());
        }

        private SC_ControlRoomSetGainItem GainMap(Connection connection, RoomState state)
        {
            SC_ControlRoomSetGainItem reply = new();
            if (state.GainItem != 0)
                reply.OIMEBMHAFPE[state.GainItem] = new KOEDCHCBJNJ { JEELHBDIIHI = (long)state.GainStored, RefreshTime = ToGame(connection, state.GainTick) };
            return reply;
        }

        public override void HandleControlRoomSetGainItem(CS_ControlRoomSetGainItem request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            RoomState state = Load(uid);
            if (Material(request.ItemId) is null)
            {
                connection.Refuse(MsgId.MsgCsControlRoomSetGainItem, HNINDAOJCKP.ControlRoomGainItemLocked);
                return;
            }

            Settle(state);
            if (state.GainItem != request.ItemId)
            {
                state.GainItem = request.ItemId;
                state.GainStored = 0;
            }

            accountState.Set(uid, StateKey, state);
            connection.Send(GainMap(connection, state));
        }

        public override void HandleControlRoomWithdrawGainItem(CS_ControlRoomWithdrawGainItem request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            RoomState state = Load(uid);
            Settle(state);
            if (state.GainItem != request.ItemId || request.Num == 0 || state.GainStored < request.Num)
            {
                connection.Refuse(MsgId.MsgCsControlRoomWithdrawGainItem, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            state.GainStored -= request.Num;
            accountState.Set(uid, StateKey, state);
            List<IMessage> messages = [];
            rewardService.Grant(uid, request.ItemId, (int)request.Num, messages);
            messages.Add(new SC_ControlRoomWithdrawGainItem());
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleControlRoomInfo(CS_ControlRoomInfo request, Connection connection)
        {
            RoomState state = connection.Account is null ? new RoomState() : Load(connection.Account.Uid);
            Settle(state);
            if (connection.Account is not null)
                accountState.Set(connection.Account.Uid, StateKey, state);
            SC_ControlRoomInfo info = new()
            {
                LIGIJKFNABP = new FOFHJPHAJBB
                {
                    Level = state.Level,
                    DFABABLCGHH = new KOEDCHCBJNJ
                    {
                        JEELHBDIIHI = (long)state.StaminaStored,
                        RefreshTime = ToGame(connection, state.StaminaTick == 0 ? Now() : state.StaminaTick),
                    },
                    GANJJEKKNMJ = 0,
                    NOMKAFOLOIO = 0,
                    BBBLPEMJMJN = 0,
                },
            };
            foreach ((uint place, Dispatch dispatch) in state.Dispatches)
                info.LIGIJKFNABP.BCFPLLOKLOJ[place] = ToProto(connection, dispatch);
            foreach ((uint item, KOEDCHCBJNJ gain) in GainMap(connection, state).OIMEBMHAFPE)
                info.LIGIJKFNABP.OIMEBMHAFPE[item] = gain;

            connection.Send(info);
        }

        public override void HandleControlRoomDispatch(CS_ControlRoomDispatch request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            RoomState state = Load(uid);
            IMessage? place = Place(request.BGDEDEFLJJO);
            uint needed = place is null ? 0 : TableFields.U(place, 8);
            List<uint> owned = accountService.GetByUid(uid)?.Guns.Select(g => g.GunId).ToList() ?? [];
            List<uint> guns = request.GGOLBAILKIO.Distinct().ToList();
            bool busy = state.Dispatches.Values.Any(d => d.Guns.Intersect(guns).Any());
            if (place is null || state.Dispatches.ContainsKey(request.BGDEDEFLJJO) || TableFields.U(place, 9) > state.Level
                || guns.Count == 0 || (needed > 0 && guns.Count > needed) || guns.Any(g => !owned.Contains(g)) || busy)
            {
                Log.Warning("ControlRoomDispatch {Place} refused for account {Uid} (guns {Guns}).", request.BGDEDEFLJJO, uid, string.Join(",", guns));
                connection.Refuse(MsgId.MsgCsControlRoomDispatch, place is null ? HNINDAOJCKP.ClientInputInvalid
                    : guns.Any(g => !owned.Contains(g)) ? HNINDAOJCKP.GunNotFound : HNINDAOJCKP.ActivityDispatchTaskGunInvalid);
                return;
            }

            Dispatch dispatch = new() { Guns = guns, Start = Now() };
            state.Dispatches[request.BGDEDEFLJJO] = dispatch;
            accountState.Set(uid, StateKey, state);
            connection.Send(new SC_ControlRoomDispatch { HLIHNOCKGLD = ToProto(connection, dispatch) });
        }

        public override void HandleControlRoomAbortDispatch(CS_ControlRoomAbortDispatch request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            RoomState state = Load(uid);
            if (!state.Dispatches.Remove(request.BGDEDEFLJJO))
            {
                connection.Refuse(MsgId.MsgCsControlRoomAbortDispatch, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            accountState.Set(uid, StateKey, state);
            connection.Send(new SC_ControlRoomAbortDispatch { BGDEDEFLJJO = request.BGDEDEFLJJO });
        }

        public override void HandleControlRoomDispatchSettle(CS_ControlRoomDispatchSettle request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            RoomState state = Load(uid);
            List<IMessage> messages = [];
            SC_ControlRoomDispatchSettle response = new();
            foreach (uint placeId in request.HABGIDKHDMP.Distinct())
            {
                IMessage? place = Place(placeId);
                if (place is null || !state.Dispatches.TryGetValue(placeId, out Dispatch? dispatch))
                    continue;

                if (Now() < dispatch.Start + (long)(TableFields.U(place, 7) / TimeScale))
                    continue;

                foreach ((uint item, uint count) in TableFields.Map(place, 5))
                    rewardService.Grant(uid, item, (int)count, messages);

                state.Dispatches.Remove(placeId);
                state.Completed++;
                response.HABGIDKHDMP.Add(placeId);
            }

            if (response.HABGIDKHDMP.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsControlRoomDispatchSettle, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            accountState.Set(uid, StateKey, state);
            questProgress.AddCondition(uid, conditionClass: 1504, link: "41001", amount: (uint)response.HABGIDKHDMP.Count);
            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleControlRoomUpgrade(CS_ControlRoomUpgrade request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            RoomState state = Load(uid);
            ControlRoomData? next = tableService.GetTable<ControlRoomData>().FirstOrDefault(r => TableFields.U(r, 1) == state.Level + 1);
            if (next is null)
            {
                connection.Refuse(MsgId.MsgCsControlRoomUpgrade, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            Dictionary<uint, int> cost = TableFields.Map(next, 3).ToDictionary(pair => pair.Key, pair => (int)pair.Value);
            List<IMessage> messages = [];
            if (!itemUse.Spend(uid, cost, messages))
            {
                connection.Refuse(MsgId.MsgCsControlRoomUpgrade, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            state.Level++;
            accountState.Set(uid, StateKey, state);
            messages.Add(new SC_ControlRoomUpgrade { JLHFICPMAEI = state.Level });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>Milestone rewards for the number of finished dispatches (claimed once).</summary>
        public override void HandleControlRoomDispatchReward(CS_ControlRoomDispatchReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            RoomState state = Load(uid);
            List<IMessage> messages = [];
            SC_ControlRoomDispatchReward response = new();
            foreach (uint id in request.RewardIds.Distinct())
            {
                ControlRoomDispatchTotalRewardData? row = tableService.GetTable<ControlRoomDispatchTotalRewardData>().FirstOrDefault(r => TableFields.U(r, 1) == id);
                if (row is null || state.ClaimedTotals.Contains(id) || state.Completed < TableFields.U(row, 2))
                    continue;

                foreach ((uint item, uint count) in TableFields.Map(row, 3))
                    rewardService.Grant(uid, item, (int)count, messages);

                state.ClaimedTotals.Add(id);
                response.RewardIds.Add(id);
            }

            if (response.RewardIds.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsControlRoomDispatchReward, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            accountState.Set(uid, StateKey, state);
            messages.Add(response);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleControlRoomScheme(CS_ControlRoomScheme request, Connection connection)
        {
            SC_ControlRoomScheme response = new()
            {
                FIBHCMPGDIK = new OneUnit
                {
                    OBMLMKKHBIA =
                    {
                        1647247360, 1645478144, 1638859520, 1640563712, 1645678848, 1645744386, 1649742336, 1646723584, 1646724352, 1645413888, 1645545216, 1645611008, 1645807872, 1645873664, 1646136064, 1646201856, 1647447296, 1647513088, 1647578880, 1647710208, 1647841282, 1647907330, 1648300800, 1648366592
                    },
                    OAADPGBJAFC = { 1704003614 },
                    FJFCNNNPACN = 1335002,
                    ELANFNLJCFM = 1335101,
                    GMHAPPNLCPG =
                    {
                        61, 501, 980, 594, 699, 1210, 152, 0, 0
                    },
                    AHGJPJEKGJH = { },
                    CDJLGOLKEBG = { },
                },
                HAOEIOLJDNP = 0,
                KKABLJJJBHM = { },
            };

            // SetWeaponExhibition is persisted by PreferenceResponder, but returning the captured exhibition here made every
            // visit to the control-room weapons wall visually undo the player's selection. This query is the read side of that
            // preference: field 1's selected weapon values map to OneUnit.field 5, and field 2 is already the matching map shape.
            if (connection.Account is not null
                && PreferenceResponder.Recall<CS_ControlRoomSetWeaponExhibition>(accountState, connection.Account.Uid, "ControlRoomSetWeaponExhibition") is { } saved)
            {
                response.FIBHCMPGDIK.GMHAPPNLCPG.Clear();
                response.FIBHCMPGDIK.GMHAPPNLCPG.AddRange(saved.HEBDFNGPBPH.OrderBy(pair => pair.Key).Select(pair => pair.Value));
                response.FIBHCMPGDIK.AHGJPJEKGJH.Clear();
                foreach ((ulong key, uint value) in saved.AHGJPJEKGJH)
                    response.FIBHCMPGDIK.AHGJPJEKGJH[key] = value;
            }

            connection.Send(response);
        }

        // Recycling needs the recycle table of the room, which the client data does not link to the item; the request is confirmed and the item kept.
        public override void HandleControlRoomRecycle(CS_ControlRoomRecycle request, Connection connection) =>
            connection.Send(new SC_ControlRoomRecycle());
    }
}
