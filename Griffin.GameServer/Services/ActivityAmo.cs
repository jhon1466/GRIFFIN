using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Amo events as read by client 4.0.5142.
    /// Wish board (activity type 19, data manager DGKPCNKBCFL): SC_GetActivityAmo maps the ActivityListData id of each open wish event
    /// (= AmoActivityMainData column 1) to the wishes already unlocked (EBLMCNFKCPI.theme_id = AmoActivitySubData id). A wish not in the
    /// list shows "unlocks in N days", N = its AmoActivitySubData column 14 minus the highest column 14 of the listed wishes, so column 14
    /// is the day of the event (0 = opening day) on which the wish unlocks. The wish quests themselves (column 9) are common quests of type 19.
    /// Tesco (Mayling procurement, HCOAEIPKLCK): CS_ActivityAmoTescoExchangeToken.num is how many of WeeklyShopConfigData column 43 are spent;
    /// each one gives column 16 of the token item, column 14.
    /// </summary>
    public static class ActivityAmo
    {
        /// <summary>The wish boards of every open Amo wish event.</summary>
        public static SC_GetActivityAmo Info(ITableService tables, IEventOverrideService overrides)
        {
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SC_GetActivityAmo reply = new();
            Dictionary<uint, IMessage> subs = tables.GetTable<AmoActivitySubData>().Cast<IMessage>()
                .GroupBy(r => TableFields.U(r, 1)).ToDictionary(g => g.Key, g => g.First());
            Dictionary<uint, ActivityListData> plans = tables.GetTable<ActivityListData>()
                .GroupBy(r => TableFields.U(r, 1)).ToDictionary(g => g.Key, g => g.First());
            foreach (IMessage main in tables.GetTable<AmoActivityMainData>())
            {
                uint id = TableFields.U(main, 1);
                if (!plans.TryGetValue(id, out ActivityListData? plan))
                    continue;
                (ulong open, ulong close) = overrides.Effective(plan);
                if ((open != 0 && now < open) || (close != 0 && now > close))
                    continue;
                // ASSUMPTION: an event day is 24 hours counted from the opening time (the client refreshes the board daily).
                ulong day = open == 0 ? 0 : (now - open) / 86400;
                CDMIKJBBLAG board = new() { CurrId = id }; // ASSUMPTION: curr_id is the event id; the client never reads it
                foreach (uint subId in TableFields.List(main, 7))
                {
                    if (!subs.TryGetValue(subId, out IMessage? sub))
                        continue;
                    if (!ulong.TryParse(TableFields.S(sub, 14), out ulong unlockDay))
                        unlockDay = 0;
                    if (unlockDay <= day)
                        board.KFFMAACHAJN.Add(new EBLMCNFKCPI { ThemeId = subId });
                }

                reply.OOEJEHGFCLL[id] = board;
            }

            return reply;
        }

        /// <summary>Spends <paramref name="num"/> exchange items for tokens; false when the items are missing.</summary>
        public static bool ExchangeToken(ITableService tables, IItemUseService itemUse, IRewardService rewards, uint uid, uint num, List<IMessage> messages)
        {
            IMessage? config = tables.GetTable<WeeklyShopConfigData>().Cast<IMessage>().FirstOrDefault(r => TableFields.U(r, 14) != 0);
            if (config is null || num == 0)
                return false;
            uint cost = TableFields.U(config, 43), token = TableFields.U(config, 14), rate = TableFields.U(config, 16);
            if (cost == 0 || token == 0 || rate == 0)
                return false;
            if (!itemUse.Spend(uid, new Dictionary<uint, int> { [cost] = checked((int)num) }, messages))
                return false;
            rewards.Grant(uid, token, checked((int)(num * rate)), messages);
            return true;
        }
    }
}
