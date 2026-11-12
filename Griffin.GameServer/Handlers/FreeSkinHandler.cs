using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Protocol;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Free skin activity (activity type 41, e.g. activity 60301). Client data manager PDNFMNOFPPL: the activity id is the key of
    /// EventFreeSkinMainData (1 = activity id, 2 = progress item whose owned count is the progress, 3 = task list, 4 = reward group,
    /// 5 = the skin). EventFreeSkinGuideRewardData (1 = reward id, 2 = group, 3 = reward map item -> count, 4 = progress needed) lists
    /// the milestones; the client marks a milestone claimable when progress >= column 4 and its id is not in the claimed list that
    /// SC_FreeSkinInfo carries (map activity id -> claimed reward ids). The tasks that give the progress item are the common quests of
    /// type ActivityFreeSkin and are not handled here.
    /// </summary>
    public sealed class FreeSkinHandler(
        IAccountStateService accountState,
        ITableService tableService,
        IItemService itemService,
        IRewardService rewardService) : FreeSkinHandlerBase
    {
        private static string Key(uint activity) => $"freeskin.{activity}";

        private List<uint> Claimed(uint uid, uint activity) => accountState.Get<List<uint>>(uid, Key(activity)) ?? [];

        private IMessage? MainRow(uint activity) =>
            tableService.GetTable<EventFreeSkinMainData>().FirstOrDefault(r => TableFields.U(r, 1) == activity);

        private SC_FreeSkinInfo Info(uint uid, uint activity)
        {
            SC_FreeSkinInfo reply = new();
            if (MainRow(activity) is null)
                return reply;

            OEJOKJAACAO state = new();
            state.CFOCLIHKKJL.AddRange(Claimed(uid, activity).Order());
            reply.BDMFDKACPPI[activity] = state;
            return reply;
        }

        public override void HandleFreeSkinInfo(CS_FreeSkinInfo request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(Info(connection.Account.Uid, request.IKDMNOBMIFE));
        }

        public override void HandleFreeSkinGetReward(CS_FreeSkinGetReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            uint activity = request.IKDMNOBMIFE;
            IMessage? main = MainRow(activity);
            uint group = main is null ? 0 : TableFields.U(main, 4);
            IMessage? reward = main is null
                ? null
                : tableService.GetTable<EventFreeSkinGuideRewardData>()
                    .FirstOrDefault(r => TableFields.U(r, 1) == request.RewardId && TableFields.U(r, 2) == group);
            if (main is null || reward is null)
            {
                // ASSUMPTION: unknown activity / reward id is refused like other closed activity requests.
                connection.Refuse(MsgId.MsgCsFreeSkinGetReward, HNINDAOJCKP.ActivityMediumCannotGetReward);
                return;
            }

            List<IMessage> messages = [];
            lock (AccountLock.For(uid))
            {
                List<uint> claimed = Claimed(uid, activity);
                if (claimed.Contains(request.RewardId))
                {
                    // ASSUMPTION: error code for a milestone already taken (no FreeSkin-specific code exists).
                    connection.Refuse(MsgId.MsgCsFreeSkinGetReward, HNINDAOJCKP.ActivityMediumCannotGetReward);
                    return;
                }

                uint progressItem = TableFields.U(main, 2);
                long progress = itemService.GetPlayerItems(uid).Where(i => i.ItemId == progressItem).Sum(i => (long)i.Count);
                if (progress < TableFields.U(reward, 4))
                {
                    connection.Refuse(MsgId.MsgCsFreeSkinGetReward, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                using (RewardDrops drops = RewardDrops.Begin())
                {
                    foreach ((uint Key, uint Value) item in TableFields.Map(reward, 3))
                        rewardService.Grant(uid, item.Key, (int)item.Value, messages);
                    drops.Finish(messages);
                }

                claimed.Add(request.RewardId);
                accountState.Set(uid, Key(activity), claimed);
            }

            messages.Add(Info(uid, activity));
            messages.Add(new SC_FreeSkinGetReward { IKDMNOBMIFE = activity });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
