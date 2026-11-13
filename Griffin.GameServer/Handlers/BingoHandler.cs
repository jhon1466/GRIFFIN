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
    /// Bingo card of the treasure activities (client 4.0.5142, data manager PCDMOKFDIEK, panels HGGBHAHPADG / GFJNJODKNJF / PDNGMLNLNLG).
    /// A treasure is an ActivityListData row of type 6 whose TreasureMainData row (column 3) names a BingoConfigData row:
    /// 2 = item spent to open a card, 4 = column rewards "item:count" (one per column), 5 = row rewards (one per row), 6 = reward of the
    /// full card, 8 = cost list "repeat:cost" indexed by the number of cards already opened (repeat 0 = one entry, the last one sticks),
    /// 10 = BingoRewardData ids that fill the card (BingoRewardData 3 = items, 4 = how many cards carry it, 6 = weight).
    /// The board is sent inside SC_TreasureData (IAHOOMEFPND field 5, CJPDLINBJCO): 1 = position -> BingoRewardData id of every opened
    /// card, 2 = rows claimed, 3 = columns claimed, 4 = full card claimed. Position = (row - 1) * columns + column, starting at 1.
    /// </summary>
    public sealed class BingoHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IRewardService rewardService,
        IItemUseService itemUse,
        IEventOverrideService eventOverrides) : BingoCardHandlerBase
    {
        public override void HandleOpenBingoCard(CS_OpenBingoCard request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            uint treasure = request.ONMNEMDCEIJ;
            uint position = request.Id;
            Bingo.Config? config = Bingo.Find(tableService, eventOverrides, treasure, (ulong)connection.GameClockSeconds());
            if (config is null || position == 0 || position > config.Columns * config.Rows)
            {
                connection.Refuse(MsgId.MsgCsOpenBingoCard, HNINDAOJCKP.ActivityBingoCardNotFound);
                return;
            }

            List<IMessage> messages = [];
            uint rewardId;
            using (RewardDrops drops = RewardDrops.Begin())
            {
                lock (AccountLock.For(uid))
                {
                    Bingo.Board board = Bingo.Load(accountState, uid, treasure);
                    if (board.Cards.Count >= config.Columns * config.Rows)
                    {
                        connection.Refuse(MsgId.MsgCsOpenBingoCard, HNINDAOJCKP.ActivityBingoAllCardsOpened);
                        return;
                    }

                    if (board.Cards.ContainsKey(position))
                    {
                        connection.Refuse(MsgId.MsgCsOpenBingoCard, HNINDAOJCKP.ActivityBingoCardRewardReceived);
                        return;
                    }

                    rewardId = Bingo.Draw(tableService, config, board);
                    if (rewardId == 0)
                    {
                        connection.Refuse(MsgId.MsgCsOpenBingoCard, HNINDAOJCKP.ActivityBingoAllCardsOpened);
                        return;
                    }

                    // the client checks the same price before sending (CJEEHIJHKKF): cost list entry at the number of opened cards
                    uint cost = config.Cost(board.Cards.Count);
                    if (config.CostItem != 0 && cost > 0
                        && !itemUse.Spend(uid, new Dictionary<uint, int> { [config.CostItem] = (int)cost }, messages))
                    {
                        connection.Refuse(MsgId.MsgCsOpenBingoCard, HNINDAOJCKP.ItemNotEnough);
                        return;
                    }

                    board.Cards[position] = rewardId;
                    accountState.Set(uid, Bingo.Key(treasure), board);

                    // ASSUMPTION: the items of the card are granted when it is turned (the card shows them as obtained; there is no
                    // separate claim request for a single card in the protocol).
                    var row = tableService.GetTable<BingoRewardData>().FirstOrDefault(r => TableFields.U(r, 1) == rewardId);
                    if (row is not null)
                        foreach ((uint item, uint count) in TableFields.Map(row, 3))
                            if (item != 0 && count > 0)
                                rewardService.Grant(uid, item, (int)count, messages);
                }

                drops.Finish(messages);
            }

            // ASSUMPTION: field 2 (finished lines) is left empty; the client only reads field 1 and claims lines itself.
            messages.Add(new SC_OpenBingoCard { Reward = { [position] = rewardId } });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }

    /// <summary>The deprecated bingo protocol of older clients (4.0.5142 never sends it); answered empty so nothing hangs.</summary>
    public sealed class LegacyBingoHandler : BingoHandlerBase
    {
        public override void HandleBingoInfo(CS_BingoInfo request, Connection connection) =>
            connection.Send(new SC_BingoInfo { Info = new GCFCPFCBMGO() });

        public override void HandleBingoScratch(CS_BingoScratch request, Connection connection) =>
            connection.Send(new SC_BingoScratch());
    }

    /// <summary>Bingo state and rules shared by the card handler, the line claims (RewardHandler) and SC_TreasureData.</summary>
    public static class Bingo
    {
        private const uint TreasureActivityType = 6;

        public sealed class Board
        {
            public Dictionary<uint, uint> Cards { get; set; } = [];
            public HashSet<uint> Rows { get; set; } = [];
            public HashSet<uint> Columns { get; set; } = [];
            public bool All { get; set; }
        }

        public sealed record Config(
            uint TreasureId,
            uint CostItem,
            IReadOnlyList<(uint Item, uint Count)> ColumnRewards,
            IReadOnlyList<(uint Item, uint Count)> RowRewards,
            IReadOnlyList<(uint Item, uint Count)> AllReward,
            IReadOnlyList<uint> Costs,
            IReadOnlyList<uint> RewardIds)
        {
            public uint Columns => (uint)ColumnRewards.Count;
            public uint Rows => (uint)RowRewards.Count;

            /// <summary>Price of the next card after <paramref name="opened"/> cards (client DJKMAABACCK: the last entry sticks).</summary>
            public uint Cost(int opened) => Costs.Count == 0 ? 0 : Costs[Math.Min(opened, Costs.Count - 1)];
        }

        public static string Key(uint treasure) => $"bingo.{treasure}";

        public static Board Load(IAccountStateService accountState, uint uid, uint treasure) =>
            accountState.Get<Board>(uid, Key(treasure)) ?? new Board();

        /// <summary>Bingo configuration of <paramref name="treasure"/> when it is an open treasure activity with a bingo card.</summary>
        public static Config? Find(ITableService tables, IEventOverrideService overrides, uint treasure, ulong now)
        {
            var activity = tables.GetTable<ActivityListData>().FirstOrDefault(r => TableFields.U(r, 1) == treasure);
            if (activity is null || TableFields.U(activity, 2) != TreasureActivityType)
                return null;
            (ulong open, ulong close) = overrides.Effective(activity);
            if ((open != 0 && now < open) || (close != 0 && now > close))
                return null;
            return Read(tables, treasure);
        }

        /// <summary>Open treasure activities that carry a bingo card.</summary>
        public static IEnumerable<Config> Open(ITableService tables, IEventOverrideService overrides, ulong now)
        {
            foreach (var activity in tables.GetTable<ActivityListData>())
            {
                if (TableFields.U(activity, 2) != TreasureActivityType)
                    continue;
                Config? config = Find(tables, overrides, TableFields.U(activity, 1), now);
                if (config is not null)
                    yield return config;
            }
        }

        private static Config? Read(ITableService tables, uint treasure)
        {
            var main = tables.GetTable<TreasureMainData>().FirstOrDefault(r => TableFields.U(r, 1) == treasure);
            uint bingoId = main is null ? 0 : TableFields.U(main, 3);
            if (bingoId == 0)
                return null;
            var row = tables.GetTable<BingoConfigData>().FirstOrDefault(r => TableFields.U(r, 1) == bingoId);
            if (row is null)
                return null;

            List<uint> costs = [];
            foreach (string part in TableFields.S(row, 8).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pair = part.Split(':');
                if (pair.Length != 2 || !uint.TryParse(pair[0], out uint repeat) || !uint.TryParse(pair[1], out uint cost))
                    continue;
                // client LFLNJHJFBLP: "repeat:cost" adds the cost repeat times, or once when repeat is 0
                for (uint i = 0; i < Math.Max(1u, repeat); i++)
                    costs.Add(cost);
            }

            Config config = new(treasure, TableFields.U(row, 2), Pairs(TableFields.S(row, 4)), Pairs(TableFields.S(row, 5)),
                TableFields.Map(row, 6).Select(p => (p.Key, p.Value)).ToList(), costs, TableFields.List(row, 10));
            return config.Columns == 0 || config.Rows == 0 ? null : config;
        }

        private static List<(uint Item, uint Count)> Pairs(string text) =>
            text.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split(':'))
                .Select(p => (p.Length > 0 && uint.TryParse(p[0], out uint item) ? item : 0u, p.Length > 1 && uint.TryParse(p[1], out uint count) ? count : 0u))
                .ToList();

        /// <summary>
        /// Picks the reward of the next card. Each BingoRewardData id of the card is on column 4 cards; the ids still in stock are
        /// drawn by their weight (column 6). ASSUMPTION: the official draw is weighted per id with stock left; no other rule is known.
        /// </summary>
        public static uint Draw(ITableService tables, Config config, Board board)
        {
            List<(uint Id, uint Weight)> pool = [];
            foreach (uint id in config.RewardIds)
            {
                var row = tables.GetTable<BingoRewardData>().FirstOrDefault(r => TableFields.U(r, 1) == id);
                if (row is null)
                    continue;
                uint stock = TableFields.U(row, 4);
                uint used = (uint)board.Cards.Values.Count(v => v == id);
                if (used < stock)
                    pool.Add((id, Math.Max(1u, TableFields.U(row, 6))));
            }

            if (pool.Count == 0)
                return 0;
            long roll = Random.Shared.NextInt64(pool.Sum(p => (long)p.Weight));
            foreach ((uint id, uint weight) in pool)
            {
                if (roll < weight)
                    return id;
                roll -= weight;
            }
            return pool[^1].Id;
        }

        public static bool RowComplete(Config config, Board board, uint row) =>
            row >= 1 && row <= config.Rows && Enumerable.Range(1, (int)config.Columns).All(c => board.Cards.ContainsKey((row - 1) * config.Columns + (uint)c));

        public static bool ColumnComplete(Config config, Board board, uint column) =>
            column >= 1 && column <= config.Columns && Enumerable.Range(1, (int)config.Rows).All(r => board.Cards.ContainsKey(((uint)r - 1) * config.Columns + column));

        public static CJPDLINBJCO ToProto(Board board)
        {
            CJPDLINBJCO proto = new() { EBJGBABBELD = board.All };
            proto.EENJDLHFCPI.Add(board.Cards);
            foreach (uint row in board.Rows)
                proto.FLEJPMNDEMN[row] = true;
            foreach (uint column in board.Columns)
                proto.PPACOCKHNPE[column] = true;
            return proto;
        }

        /// <summary>
        /// CS_TakeBingoExtraReward: reward type Row (0) / Col (1) with the line number, or All (3) for the full card (client DGOFICDCCOD /
        /// DGIDAMNEDDM). A line is claimable when every card of it is open (client EGAIGKAHGKK / GFFOALFJOOG); the full card when every card is.
        /// </summary>
        public static void TakeExtraReward(CS_TakeBingoExtraReward request, Connection connection, ITableService tables, IEventOverrideService overrides,
            IAccountStateService accountState, IRewardService rewardService)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Config? config = Find(tables, overrides, request.ONMNEMDCEIJ, (ulong)connection.GameClockSeconds());
            if (config is null)
            {
                connection.Refuse(MsgId.MsgCsTakeBingoExtraReward, HNINDAOJCKP.ActivityBingoCardNotFound);
                return;
            }

            List<IMessage> messages = [];
            using (RewardDrops drops = RewardDrops.Begin())
            {
                lock (AccountLock.For(uid))
                {
                    Board board = Load(accountState, uid, config.TreasureId);
                    uint index = request.PAGNJJKGIDA;
                    IReadOnlyList<(uint Item, uint Count)> reward;
                    HNINDAOJCKP error = HNINDAOJCKP.Success;
                    switch ((int)request.RewardType)
                    {
                        case 0: // Row
                            if (index < 1 || index > config.Rows) error = HNINDAOJCKP.ActivityBingoExtraIdNotFound;
                            else if (board.Rows.Contains(index)) error = HNINDAOJCKP.ActivityBingoExtraRewardReceived;
                            else if (!RowComplete(config, board, index)) error = HNINDAOJCKP.ActivityBingoRewardNotReach;
                            reward = error == HNINDAOJCKP.Success ? [config.RowRewards[(int)index - 1]] : [];
                            if (error == HNINDAOJCKP.Success) board.Rows.Add(index);
                            break;
                        case 1: // Col
                            if (index < 1 || index > config.Columns) error = HNINDAOJCKP.ActivityBingoExtraIdNotFound;
                            else if (board.Columns.Contains(index)) error = HNINDAOJCKP.ActivityBingoExtraRewardReceived;
                            else if (!ColumnComplete(config, board, index)) error = HNINDAOJCKP.ActivityBingoRewardNotReach;
                            reward = error == HNINDAOJCKP.Success ? [config.ColumnRewards[(int)index - 1]] : [];
                            if (error == HNINDAOJCKP.Success) board.Columns.Add(index);
                            break;
                        case 3: // All
                            if (board.All) error = HNINDAOJCKP.ActivityBingoExtraRewardReceived;
                            else if (board.Cards.Count < config.Columns * config.Rows) error = HNINDAOJCKP.ActivityBingoRewardNotReach;
                            reward = error == HNINDAOJCKP.Success ? config.AllReward : [];
                            if (error == HNINDAOJCKP.Success) board.All = true;
                            break;
                        default:
                            error = HNINDAOJCKP.ActivityBingoExtraIdNotFound;
                            reward = [];
                            break;
                    }

                    if (error != HNINDAOJCKP.Success)
                    {
                        connection.Refuse(MsgId.MsgCsTakeBingoExtraReward, error);
                        return;
                    }

                    accountState.Set(uid, Key(config.TreasureId), board);
                    foreach ((uint item, uint count) in reward)
                        if (item != 0 && count > 0)
                            rewardService.Grant(uid, item, (int)count, messages);
                }

                drops.Finish(messages);
            }

            messages.Add(new SC_TakeBingoExtraReward());
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
