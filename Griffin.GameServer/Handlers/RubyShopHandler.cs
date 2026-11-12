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
    /// Ruby shop of the events (ActivityListData type 18, client data manager "Main/Activity/Ruby", client 4.0.5142). The requests carry the
    /// activity id itself (ActivityListData id = RubyMainData id), so no theme/module lookup is needed. Tables, as the client reads them:
    /// RubyMainData (1 = activity, 2 = currency item, 4 = task list); RubyShopByCaseIdData (activity -> goods); RubyShopData (1 = goods,
    /// 2 = activity, 3 = items given, 4 = price in the currency, 5 = purchase limit, 6 = favor exp per purchase, 7 = favor level needed);
    /// FavorLvData (2 = activity, 3 = level, 4 = total favor exp needed, 5 = level reward). The per-activity state (MailBox in the proto,
    /// JDLGMFGLHAO in the client) is 1 = favor level, 2 = favor exp, 3 = bought count per goods, 4 = favor level rewards claimed,
    /// 5 = currency earned from the ruby tasks (progress bar). Info used to answer an empty map, so the shop showed nothing.
    /// </summary>
    public sealed class RubyShopHandler(
        IAccountStateService accountState,
        ITableService tableService,
        IRewardService rewardService,
        IItemUseService itemUse,
        IEventOverrideService eventOverrides) : RubyShopHandlerBase
    {
        public sealed class State
        {
            public uint Exp { get; set; }
            public Dictionary<uint, uint> Bought { get; set; } = [];
            public List<uint> Claimed { get; set; } = [];
            public uint TaskPoints { get; set; }
        }

        private const uint RubyActivityType = 18;

        private static string Key(uint activity) => $"rubyshop.{activity}";

        private State Load(uint uid, uint activity) => accountState.Get<State>(uid, Key(activity)) ?? new State();

        private IMessage? Main(uint activity) => tableService.GetTable<RubyMainData>().FirstOrDefault(r => TableFields.U(r, 1) == activity);

        private List<IMessage> FavorRows(uint activity) =>
            tableService.GetTable<FavorLvData>().Where(r => TableFields.U(r, 2) == activity).Cast<IMessage>().OrderBy(r => TableFields.U(r, 3)).ToList();

        /// <summary>
        /// The favor level reached with <paramref name="exp"/>: the highest FavorLvData level whose total exp (column 4) is reached. The client
        /// shows level 1 when it has no data (LLBNJPOGAPM), and level 1 needs no exp.
        /// </summary>
        private uint Level(uint activity, uint exp)
        {
            uint level = 1;
            foreach (IMessage row in FavorRows(activity))
                if (exp >= TableFields.U(row, 4))
                    level = Math.Max(level, TableFields.U(row, 3));
            return level;
        }

        private MailBox Proto(uint activity, State state)
        {
            MailBox box = new() { CDBMDEDGLII = Level(activity, state.Exp), AJJGNKNNJAE = state.Exp, PJPNDGDOPPN = state.TaskPoints };
            foreach ((uint goods, uint count) in state.Bought)
                box.Goods[goods] = count;
            foreach (uint level in state.Claimed)
                box.DPKADBJNDIB[level] = true;
            return box;
        }

        public override void HandleGetRubyShopInfo(CS_GetRubyShopInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            long now = connection.GameClockSeconds();
            SC_GetRubyShopInfo reply = new();
            // every ruby shop open now (official dates or a window opened from the GM tools), as the activity calendar announces them
            foreach (ActivityListData row in tableService.GetTable<ActivityListData>().Where(r => TableFields.U(r, 2) == RubyActivityType))
            {
                (ulong open, ulong close) = eventOverrides.Effective(row);
                if ((open != 0 && (long)open > now) || (close != 0 && now > (long)close))
                    continue;
                uint activity = TableFields.U(row, 1);
                if (Main(activity) is null)
                    continue;
                reply.CIMEBELLALJ[activity] = Proto(activity, Load(uid, activity));
            }

            connection.Send(reply);
        }

        public override void HandleRubyShopBuy(CS_RubyShopBuy request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            uint activity = request.IKDMNOBMIFE, goodsId = request.BFOLFKPANIA, count = request.LONKKIMNDCG;
            IMessage? main = Main(activity);
            IMessage? goods = tableService.GetTable<RubyShopData>().FirstOrDefault(r => TableFields.U(r, 1) == goodsId && TableFields.U(r, 2) == activity);
            if (main is null || goods is null || count == 0)
            {
                connection.Refuse(MsgId.MsgCsRubyShopBuy, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            List<IMessage> messages = [];
            SC_RubyShopBuy reply;
            using RewardDrops drops = RewardDrops.Begin();
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, activity);
                if (Level(activity, state.Exp) < TableFields.U(goods, 7))
                {
                    connection.Refuse(MsgId.MsgCsRubyShopBuy, HNINDAOJCKP.RubyShopFavorLevelNotEnough);
                    return;
                }

                // column 5 is the purchase limit: the client shows "limit - bought" (DHBPDDIHJCC) and counts the goods done when bought >= it
                uint limit = TableFields.U(goods, 5);
                uint bought = state.Bought.GetValueOrDefault(goodsId);
                if (limit != 0 && bought + count > limit)
                {
                    connection.Refuse(MsgId.MsgCsRubyShopBuy, HNINDAOJCKP.RubyShopExceedGoodLimit);
                    return;
                }

                uint currency = TableFields.U(main, 2);
                long price = (long)TableFields.U(goods, 4) * count;
                if (price > 0 && !itemUse.Spend(uid, new Dictionary<uint, int> { [currency] = (int)price }, messages))
                {
                    connection.Refuse(MsgId.MsgCsRubyShopBuy, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                foreach ((uint item, uint amount) in TableFields.Map(goods, 3))
                    rewardService.Grant(uid, item, (int)(amount * count), messages);

                state.Bought[goodsId] = bought + count;
                state.Exp += TableFields.U(goods, 6) * count;
                accountState.Set(uid, Key(activity), state);
                reply = new SC_RubyShopBuy { IKDMNOBMIFE = activity, AJJGNKNNJAE = state.Exp, CDBMDEDGLII = Level(activity, state.Exp) };
            }

            drops.Finish(messages);
            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        /// <summary>Pays the rewards of <paramref name="levels"/> not yet claimed; returns the levels paid.</summary>
        private List<uint> Claim(uint uid, uint activity, State state, IEnumerable<uint> levels, List<IMessage> messages)
        {
            List<IMessage> rows = FavorRows(activity);
            List<uint> paid = [];
            foreach (uint level in levels)
            {
                if (state.Claimed.Contains(level))
                    continue;
                IMessage? row = rows.FirstOrDefault(r => TableFields.U(r, 3) == level);
                if (row is not null)
                    foreach ((uint item, uint amount) in TableFields.Map(row, 5))
                        rewardService.Grant(uid, item, (int)amount, messages);
                state.Claimed.Add(level);
                paid.Add(level);
            }

            return paid;
        }

        public override void HandleRubyShopClaimFavorReward(CS_RubyShopClaimFavorReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            uint activity = request.IKDMNOBMIFE, level = request.CDBMDEDGLII;
            if (Main(activity) is null || !FavorRows(activity).Any(r => TableFields.U(r, 3) == level))
            {
                connection.Refuse(MsgId.MsgCsRubyShopClaimFavorReward, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            List<IMessage> messages = [];
            using RewardDrops drops = RewardDrops.Begin();
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, activity);
                if (Level(activity, state.Exp) < level)
                {
                    connection.Refuse(MsgId.MsgCsRubyShopClaimFavorReward, HNINDAOJCKP.RubyShopFavorLevelNotEnough);
                    return;
                }

                if (state.Claimed.Contains(level))
                {
                    connection.Refuse(MsgId.MsgCsRubyShopClaimFavorReward, HNINDAOJCKP.RubyShopFavorLevelRewardAlreadyClaimed);
                    return;
                }

                Claim(uid, activity, state, [level], messages);
                accountState.Set(uid, Key(activity), state);
            }

            // the client marks the level claimed itself when the answer is a success (ECFEKBDJHCJ); the reply has no fields
            drops.Finish(messages);
            messages.Add(new SC_RubyShopClaimFavorReward());
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleRubyShopClaimAllFavorReward(CS_RubyShopClaimAllFavorReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            uint activity = request.IKDMNOBMIFE;
            if (Main(activity) is null)
            {
                connection.Refuse(MsgId.MsgCsRubyShopClaimAllFavorReward, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            List<IMessage> messages = [];
            SC_RubyShopClaimAllFavorReward reply = new() { IKDMNOBMIFE = activity };
            using RewardDrops drops = RewardDrops.Begin();
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, activity);
                uint reached = Level(activity, state.Exp);
                // every level from 1 to the current one, as the client's red dot checks them (GODBMCKIFDO)
                IEnumerable<uint> levels = FavorRows(activity).Select(r => TableFields.U(r, 3)).Where(l => l >= 1 && l <= reached);
                // ASSUMPTION: the reply lists the levels paid by this claim; the client merges the map into its state (LBJKGILENHA)
                foreach (uint level in Claim(uid, activity, state, levels, messages))
                    reply.DPKADBJNDIB[level] = true;
                accountState.Set(uid, Key(activity), state);
            }

            drops.Finish(messages);
            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
