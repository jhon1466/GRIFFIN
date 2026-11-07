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
    /// Box gacha of the events (LHJOGAOHJFA). ActivityGachaConfigData (id = the box set, 3/4 = item and amount per draw) lists its boxes in
    /// ActivityGachaGroupByGroupData; each box (ActivityGachaGroupData, 4 = reward pool) holds ActivityGachaRewardData rows with a stock
    /// (4) and an item (5). A draw takes prizes out of the current box; when the box is settled the next one opens. The state is
    /// ActivityGacha: curr_id = 1-based box index, one record per box with the prizes drawn (reward row -> count).
    /// ASSUMPTION A-193: the activity id of the requests is the box set id (falls back to field 3 when it is not one).
    /// </summary>
    public sealed class ActivityBoxGacha(IAccountStateService accountState, ITableService tableService, IRewardService rewardService, IItemUseService itemUse)
    {
        public sealed class State
        {
            public uint Box { get; set; } = 1;
            public Dictionary<uint, Dictionary<uint, uint>> Drawn { get; set; } = [];
            public List<uint> Finished { get; set; } = [];
        }

        private static string Key(uint set) => $"activity.boxgacha.{set}";

        private uint SetOf(uint activityId, uint fallback) =>
            tableService.GetTable<ActivityGachaConfigData>().Any(r => r.Id == activityId) ? activityId : fallback;

        private List<uint> Boxes(uint set) =>
            tableService.GetTable<ActivityGachaGroupByGroupData>().FirstOrDefault(r => r.Group == set)?.Id.ToList() ?? [];

        private ActivityGacha Proto(uint set, State state)
        {
            ActivityGacha gacha = new() { CurrId = state.Box };
            foreach (uint box in Boxes(set))
            {
                GachaRewardRecord record = new() { GachaGroupId = box, State = state.Finished.Contains(box) ? DJEAJAIHBGD.ActivityGroupStateEnd : DJEAJAIHBGD.ActivityGroupStateStart };
                foreach ((uint row, uint count) in state.Drawn.GetValueOrDefault(box) ?? [])
                    record.Rewards[row] = count;
                gacha.Records.Add(record);
            }

            return gacha;
        }

        public void HandleGetActivityGacha(CS_GetActivityGacha request, Connection connection)
        {
            if (connection.Account is null)
                return;
            SC_GetActivityGacha reply = new();
            foreach (ActivityGachaConfigData config in tableService.GetTable<ActivityGachaConfigData>())
                reply.Gacha[config.Id] = Proto(config.Id, accountState.Get<State>(connection.Account.Uid, Key(config.Id)) ?? new State());
            connection.Send(reply);
        }

        public void HandleActivityGacha(CS_ActivityGacha request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            uint set = SetOf(request.IKDMNOBMIFE, request.KGPCGAMGONK);
            ActivityGachaConfigData? config = tableService.GetTable<ActivityGachaConfigData>().FirstOrDefault(r => r.Id == set);
            if (config is null || request.Num == 0)
            {
                connection.Refuse(MsgId.MsgCsActivityGacha, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            List<IMessage> messages = [];
            SC_ActivityGacha reply = new() { IKDMNOBMIFE = request.IKDMNOBMIFE, Num = request.Num, KGPCGAMGONK = request.KGPCGAMGONK };
            lock (AccountLock.For(uid))
            {
                State state = accountState.Get<State>(uid, Key(set)) ?? new State();
                List<uint> boxes = Boxes(set);
                if (state.Box < 1 || state.Box > boxes.Count)
                {
                    connection.Refuse(MsgId.MsgCsActivityGacha, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                uint box = boxes[(int)state.Box - 1];
                uint pool = tableService.GetTable<ActivityGachaGroupData>().FirstOrDefault(r => r.Id == box)?.GachaGroup ?? 0;
                Dictionary<uint, uint> drawn = state.Drawn.GetValueOrDefault(box) ?? [];
                List<ActivityGachaRewardData> prizes = tableService.GetTable<ActivityGachaRewardData>().Where(r => r.GachaGroup == pool).ToList();
                uint left = (uint)prizes.Sum(p => (long)p.Stock - drawn.GetValueOrDefault(p.Id));
                uint draws = Math.Min(request.Num, left);
                if (draws == 0)
                {
                    connection.Refuse(MsgId.MsgCsActivityGacha, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                if (config.GachaItem != 0 && config.GachaCost > 0
                    && !itemUse.Spend(uid, new Dictionary<uint, int> { [config.GachaItem] = (int)(config.GachaCost * draws) }, messages))
                {
                    connection.Refuse(MsgId.MsgCsActivityGacha, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                for (int i = 0; i < draws; i++)
                {
                    int roll = Random.Shared.Next((int)prizes.Sum(p => p.Stock - drawn.GetValueOrDefault(p.Id)));
                    foreach (ActivityGachaRewardData prize in prizes)
                    {
                        int stock = (int)(prize.Stock - drawn.GetValueOrDefault(prize.Id));
                        if (roll >= stock)
                        {
                            roll -= stock;
                            continue;
                        }

                        drawn[prize.Id] = drawn.GetValueOrDefault(prize.Id) + 1;
                        if (prize.POGNNHKIGCB is { } items)
                            for (int k = 0; k < Math.Min(items.BAALJFJDGBB.Count, items.Value.Count); k++)
                            {
                                rewardService.Grant(uid, items.BAALJFJDGBB[k], (int)items.Value[k], messages);
                                reply.Reward[items.BAALJFJDGBB[k]] = reply.Reward.GetValueOrDefault(items.BAALJFJDGBB[k]) + items.Value[k];
                            }
                        break;
                    }
                }

                state.Drawn[box] = drawn;
                accountState.Set(uid, Key(set), state);
            }

            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>Closes the current box and opens the next one.</summary>
        public void HandleActivityGachaSettle(CS_ActivityGachaSettle request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            uint set = SetOf(request.IKDMNOBMIFE, 0);
            State state;
            lock (AccountLock.For(uid))
            {
                state = accountState.Get<State>(uid, Key(set)) ?? new State();
                List<uint> boxes = Boxes(set);
                if (state.Box >= 1 && state.Box <= boxes.Count && !state.Finished.Contains(boxes[(int)state.Box - 1]))
                    state.Finished.Add(boxes[(int)state.Box - 1]);
                if (state.Box < boxes.Count)
                    state.Box++;
                accountState.Set(uid, Key(set), state);
            }

            connection.Send(new SC_ActivityGachaSettle { IKDMNOBMIFE = request.IKDMNOBMIFE, End = request.End, Gacha = Proto(set, state) });
        }
    }
}
