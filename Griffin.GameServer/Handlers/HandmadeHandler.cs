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
    /// The figurine collection ("handmade"): <c>HandmadeCsData</c> lists the figurines (1 = id, 2 = the item the player owns, 3 = points); the score is the sum
    /// of the points of the owned ones; <c>HandmadeRewardData</c> (1 = reward, 2 = score needed, 3 = reward) pays each step once. The wall/compartment ids come
    /// from <c>HandmadeConfigData</c>. ASSUMPTION A-33: the list of obtained figurines of the answer is the owned ones (ids of col 1).
    /// </summary>
    public sealed class HandmadeHandler(ITableService tableService, IAccountStateService accountState, IItemService itemService, IRewardService rewardService) : HandmadeHandlerBase
    {
        private const string StateKey = "handmade.claimed";

        private IEnumerable<IMessage> Figurines(uint uid)
        {
            HashSet<uint> owned = itemService.GetPlayerItems(uid).Where(i => i.Count > 0).Select(i => i.ItemId).ToHashSet();
            return tableService.GetTable<HandmadeCsData>().Where(f => owned.Contains(TableFields.U(f, 2)));
        }

        public override void HandleHandmadeData(CS_HandmadeData request, Connection connection)
        {
            BJINECMIFNJ data = new() { FJFCNNNPACN = 1335001, ELANFNLJCFM = 1335101 };
            if (tableService.GetTable<HandmadeConfigData>().FirstOrDefault() is { } config)
            {
                data.FJFCNNNPACN = TableFields.U(config, 2);
                data.ELANFNLJCFM = TableFields.U(config, 3);
            }

            if (connection.Account is not null)
            {
                uint uid = connection.Account.Uid;
                List<IMessage> owned = Figurines(uid).ToList();
                data.Score = (uint)owned.Sum(f => TableFields.U(f, 3));
                data.NBONFIHFCBE.AddRange(owned.Select(f => TableFields.U(f, 1)));
                data.CFOCLIHKKJL.AddRange(accountState.Get<List<uint>>(uid, StateKey) ?? []);
            }

            connection.Send(new SC_HandmadeData { HGMDLHCFPDE = data });
        }

        public override void HandleHandmadeClaimReward(CS_HandmadeClaimReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            lock (AccountLock.For(connection.Account.Uid))
            {
                uint uid = connection.Account.Uid;
                uint score = (uint)Figurines(uid).Sum(f => TableFields.U(f, 3));
                List<uint> claimed = accountState.Get<List<uint>>(uid, StateKey) ?? [];
                List<IMessage> messages = [];
                SC_HandmadeClaimReward reply = new();
                foreach (uint id in request.RewardId.Distinct())
                {
                    IMessage? row = tableService.GetTable<HandmadeRewardData>().FirstOrDefault(r => TableFields.U(r, 1) == id);
                    if (row is null || claimed.Contains(id) || score < TableFields.U(row, 2))
                        continue;

                    foreach ((uint item, uint count) in TableFields.Map(row, 3))
                        rewardService.Grant(uid, item, (int)count, messages);
                    claimed.Add(id);
                    reply.CFOCLIHKKJL.Add(id);
                }

                if (reply.CFOCLIHKKJL.Count == 0)
                {
                    connection.Refuse(MsgId.MsgCsHandmadeClaimReward, HNINDAOJCKP.ClientInputInvalid);
                    return;
                }

                accountState.Set(uid, StateKey, claimed);
                messages.Add(reply);
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }
    }
}
