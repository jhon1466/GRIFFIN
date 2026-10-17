using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// The archive center ("record room"). Each story (<c>InformationDetailCsData</c> col 2) has details numbered by col 3; a detail id is
    /// story * 100 + number. Unlocking detail N spends col 8 (an archive item) and needs detail N-1 already unlocked; the reward of the details unlocked
    /// since the last claim (col 9) is paid by <c>RecordRoomGetDetailReward</c>. The client keeps "last unlocked number" and "reward taken" per story
    /// (and resets "reward taken" at every unlock). ASSUMPTION A-29: this is how the client treats those fields (read in <c>OCLBJJHACOD</c>).
    /// </summary>
    public sealed class RecordRoomHandler(ITableService tableService, IAccountStateService accountState, IItemUseService itemUse, IRewardService rewardService) : RecordRoomHandlerBase
    {
        private const string StateKey = "recordroom";

        public sealed class RoomState
        {
            public uint Detail { get; set; }
            public uint Claimed { get; set; }
        }

        private IEnumerable<IMessage> Details() => tableService.GetTable<InformationDetailCsData>();

        public override void HandleRecordRoom(CS_RecordRoom request, Connection connection)
        {
            SC_RecordRoom reply = new();
            if (connection.Account is not null)
                foreach ((uint story, RoomState state) in accountState.Get<Dictionary<uint, RoomState>>(connection.Account.Uid, StateKey) ?? [])
                    reply.Storys[story] = new RecordRoomDetail { DetailIdx = state.Detail, State = state.Claimed >= state.Detail };
            connection.Send(reply);
        }

        public override void HandleRecordRoomUnLockDetail(CS_RecordRoomUnLockDetail request, Connection connection)
        {
            if (connection.Account is null)
                return;

            lock (AccountLock.For(connection.Account.Uid))
            {
                uint uid = connection.Account.Uid;
                IMessage? row = Details().FirstOrDefault(r => TableFields.U(r, 1) == request.DetailId && TableFields.U(r, 2) == request.StoryId);
                if (row is null)
                {
                    connection.Refuse(MsgId.MsgCsRecordRoomUnLockDetail, HNINDAOJCKP.RecordRoomClueNotUnlock);
                    return;
                }

                Dictionary<uint, RoomState> states = accountState.Get<Dictionary<uint, RoomState>>(uid, StateKey) ?? [];
                RoomState state = states.GetValueOrDefault(request.StoryId) ?? new RoomState();
                uint number = TableFields.U(row, 3);
                if (number != state.Detail + 1)
                {
                    connection.Refuse(MsgId.MsgCsRecordRoomUnLockDetail, HNINDAOJCKP.RecordRoomClueNotUnlock);
                    return;
                }

                List<IMessage> messages = [];
                Dictionary<uint, int> cost = TableFields.Map(row, 8).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => (int)p.Value);
                if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
                {
                    connection.Refuse(MsgId.MsgCsRecordRoomUnLockDetail, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                state.Detail = number;
                states[request.StoryId] = state;
                accountState.Set(uid, StateKey, states);
                messages.Add(new SC_RecordRoomUnLockDetail { StoryId = request.StoryId, DetailId = request.DetailId });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        public override void HandleRecordRoomGetDetailReward(CS_RecordRoomGetDetailReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            lock (AccountLock.For(connection.Account.Uid))
            {
                uint uid = connection.Account.Uid;
                Dictionary<uint, RoomState> states = accountState.Get<Dictionary<uint, RoomState>>(uid, StateKey) ?? [];
                if (!states.TryGetValue(request.StoryId, out RoomState? state) || state.Claimed >= state.Detail)
                {
                    connection.Refuse(MsgId.MsgCsRecordRoomGetDetailReward, HNINDAOJCKP.RecordRoomClueNotUnlock);
                    return;
                }

                List<IMessage> messages = [];
                foreach (IMessage row in Details().Where(r => TableFields.U(r, 2) == request.StoryId && TableFields.U(r, 3) > state.Claimed && TableFields.U(r, 3) <= state.Detail))
                    foreach ((uint item, uint count) in TableFields.Map(row, 9))
                        rewardService.Grant(uid, item, (int)count, messages);

                state.Claimed = state.Detail;
                accountState.Set(uid, StateKey, states);
                messages.Add(new SC_RecordRoomGetDetailReward { StoryId = request.StoryId, DetailId = request.StoryId * 100 + state.Detail });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }
    }
}
