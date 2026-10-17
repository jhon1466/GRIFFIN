using Google.Protobuf;
using System.Collections.Concurrent;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    public sealed class StoreHandler(
        ITableService tableService,
        IResourceService resourceService,
        IRewardService rewardService,
        IItemUseService itemUse,
        IAccountStateService accountState) : StoreHandlerBase
    {
        private const string PurchasesKey = "store.purchases";
        private const string ManualRefreshKey = "store.manual-refresh.v1";
        private static readonly ConcurrentDictionary<uint, object> AccountLocks = new();

        private static object AccountLock(uint uid) => AccountLocks.GetOrAdd(uid, static _ => new object());

        public override void HandleStore(CS_Store request, Connection connection)
        {
            SC_Store response = new()
            {
                Mayling = new Mayling(),
                Refresh = new StoreRefresh(),
                Test = new StoreTest { Age = new StoreTest.Types.Age() },
            };
            long storeNow = connection.GameClockSeconds();
            Dictionary<uint, uint> purchases = connection.Account is null ? [] : Purchases(connection.Account.Uid);
            Dictionary<uint, StoreGoodData> goods = tableService.GetTable<StoreGoodData>().ToDictionary(good => good.Id);
            Dictionary<uint, StoreTagData> tags = tableService.GetTable<StoreTagData>().ToDictionary(tag => tag.Id);

            // Build the catalog from the installed client's versioned tables. The previous
            // response was copied from another account and contained fixed IDs and deadlines.
            foreach (StoreGoodByTagData group in tableService.GetTable<StoreGoodByTagData>())
            {
                uint tagId = group.Tag;
                if (!tags.TryGetValue(tagId, out StoreTagData? tag) || !IsActive(tag, storeNow))
                    continue;

                MarketTag marketTag = new();
                foreach (uint goodId in group.Id)
                {
                    if (!goods.TryGetValue(goodId, out StoreGoodData? good))
                        continue;

                    uint bought = purchases.GetValueOrDefault(good.Id);
                    if (good.Limit > 0 && bought >= good.Limit)
                        continue;

                    marketTag.Goods.Add(new MarketGood
                    {
                        Id = good.Id,
                        Num = good.Limit > 0 ? good.Limit - bought : 0,
                        PriceType = good.PriceType,
                        Price = good.Price,
                        CBGAEKOCDOL = good.BuyReward,
                    });
                }

                if (marketTag.Goods.Count == 0)
                    continue;

                response.Tags[tagId] = marketTag;
                ulong endsAt = TableFields.U64(tag, 13);
                if (endsAt > (ulong)storeNow)
                    response.Refresh.Tags[tagId] = (long)endsAt;
            }

            foreach (StoreGoodData good in goods.Values.Where(good => good.Limit > 0 || purchases.ContainsKey(good.Id)))
                response.Histories[good.Id] = new StoreHistory
                {
                    Count = purchases.GetValueOrDefault(good.Id),
                    Total = good.Limit,
                };

            if (connection.Account is not null)
            {
                Dictionary<uint, uint> manualRefresh = accountState.Get<Dictionary<uint, uint>>(
                    connection.Account.Uid, ManualRefreshKey) ?? [];
                foreach ((uint tagId, uint count) in manualRefresh)
                    response.ManualRefreshTimes[tagId] = count;
            }

            connection.Send(8, response);
        }
        public override void HandleStoreBuy(CS_StoreBuy request, Connection connection)
        {
            if (connection.Account is null)
                return;

            lock (AccountLock(connection.Account.Uid))
            {
                List<IMessage> messages = [];
                SC_StoreBuy result = Buy(connection.Account.Uid, request, messages);
                messages.Add(result);
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        public override void HandleStoreBatchBuy(CS_StoreBatchBuy request, Connection connection)
        {
            if (connection.Account is null)
                return;

            lock (AccountLock(connection.Account.Uid))
            {
                List<IMessage> messages = [];
                SC_StoreBatchBuy batch = new();
                foreach (CS_StoreBuy buy in request.HDNGMDLJHGJ)
                    batch.HDNGMDLJHGJ.Add(Buy(connection.Account.Uid, buy, messages));

                messages.Add(batch);
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        public override void HandleStoreBlackBuy(CS_StoreBlackBuy request, Connection connection)
        {
            if (connection.Account is null)
                return;

            lock (AccountLock(connection.Account.Uid))
            {

            // EsBlackstoreTypeData maps the request's type id to one of the two
            // EsGoodsByStoreTagData lists; the request index selects a row in that list.
            EsBlackstoreTypeData? type = tableService.GetTable<EsBlackstoreTypeData>()
                .FirstOrDefault(row => row.Id == request.TagId);
            if (type is null)
            {
                connection.Refuse(MsgId.MsgCsStoreBlackBuy, HNINDAOJCKP.StoreTagNotFound);
                return;
            }

            EsGoodsByStoreTagData? group = tableService.GetTable<EsGoodsByStoreTagData>()
                .FirstOrDefault(row => row.Id == type.Col3);
            if (group is null || request.Index >= group.Goods.Count)
            {
                connection.Refuse(MsgId.MsgCsStoreBlackBuy, HNINDAOJCKP.StoreCannotFindGoods);
                return;
            }

            uint goodId = group.Goods[(int)request.Index];
            EsGoodsData? good = tableService.GetTable<EsGoodsData>().FirstOrDefault(row => row.Id == goodId);
            if (good is null || good.Goods.Count == 0 || good.Price.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsStoreBlackBuy, HNINDAOJCKP.TableDataNotValid);
                return;
            }

            Dictionary<uint, int> cost = [];
            foreach ((uint itemId, uint amount) in good.Price)
            {
                if (itemId == 0 || amount == 0 || amount > int.MaxValue
                    || !tableService.GetTable<ItemData>().Any(item => item.Id == itemId))
                {
                    connection.Refuse(MsgId.MsgCsStoreBlackBuy, HNINDAOJCKP.TableDataNotValid);
                    return;
                }
                cost[itemId] = (int)amount;
            }

            if (good.Goods.Any(pair => pair.Value > int.MaxValue))
            {
                connection.Refuse(MsgId.MsgCsStoreBlackBuy, HNINDAOJCKP.TableDataNotValid);
                return;
            }
            List<(uint ItemId, int Count)> rewards = good.Goods
                .Select(pair => (pair.Key, (int)pair.Value)).ToList();
            if (rewards.Count == 0 || rewards.Any(reward => reward.ItemId == 0 || reward.Count <= 0
                || !IsKnownReward(reward.ItemId)))
            {
                connection.Refuse(MsgId.MsgCsStoreBlackBuy, HNINDAOJCKP.TableDataNotValid);
                return;
            }

            List<IMessage> messages = [];
            if (!itemUse.Spend(connection.Account.Uid, cost, messages))
            {
                connection.Refuse(MsgId.MsgCsStoreBlackBuy, HNINDAOJCKP.ItemNotEnough);
                return;
            }

            foreach ((uint itemId, int amount) in rewards)
            {
                if (rewardService.Grant(connection.Account.Uid, itemId, amount, messages).Granted)
                    continue;

                foreach ((uint refundItem, int refundAmount) in cost)
                    rewardService.Grant(connection.Account.Uid, refundItem, refundAmount, messages);
                connection.Refuse(MsgId.MsgCsStoreBlackBuy, HNINDAOJCKP.TableDataNotValid);
                return;
            }

            messages.Add(new SC_StoreBlackBuy
            {
                TagId = request.TagId,
                Index = request.Index,
                Good = new MarketGood
                {
                    Id = good.Id,
                    Num = 1,
                    PriceType = cost.Count == 1 ? cost.Keys.Single() : 0,
                    Price = cost.Count == 1 ? cost.Values.Single() : 0,
                },
            });
            connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        private bool IsKnownReward(uint itemId) =>
            tableService.GetTable<GunData>().Any(gun => gun.Id == itemId)
            || tableService.GetTable<GunWeaponData>().Any(weapon => weapon.Id == itemId)
            || tableService.GetTable<ItemData>().Any(item => item.Id == itemId);

        /// <summary>
        /// One purchase from the <c>StoreGoodData</c> table: <c>PriceType</c> is the currency item and <c>Price</c> the amount,
        /// <c>Reward</c> the "item:count,item:count;" payload and <c>Limit</c> the number of purchases (0 = unlimited).
        /// Goods paid with real money are refused (there is no payment); the counters of limited goods never reset (the
        /// refresh periods are not in the tables).
        /// </summary>
        private SC_StoreBuy Buy(uint uid, CS_StoreBuy request, List<IMessage> messages)
        {
            SC_StoreBuy Reply(MANKAOBLPBJ.Types.Code code) =>
                new() { Code = code, GoodsId = request.GoodsId, PriceType = request.PriceType };

            StoreGoodData? good = tableService.GetTable<StoreGoodData>().FirstOrDefault(g => g.Id == request.GoodsId);
            if (good is null)
            {
                Log.Warning("Store: goods {GoodsId} is not in StoreGoodData.", request.GoodsId);
                return Reply(MANKAOBLPBJ.Types.Code.Locked);
            }

            // Optional reward selection is a separate catalog flow. Until its price and
            // ownership contract is implemented, reject it rather than granting the default item.
            if (request.Optional.Count != 0)
                return Reply(MANKAOBLPBJ.Types.Code.OptionalError);

            Dictionary<uint, uint> purchases = Purchases(uid);
            uint bought = purchases.GetValueOrDefault(good.Id);
            if (good.Limit > 0 && bought >= good.Limit)
                return Reply(MANKAOBLPBJ.Types.Code.LimitAgain);

            bool paidWithResource = good.PriceType != 0
                && tableService.GetTable<ItemData>().Any(i => i.Id == good.PriceType && i.Type == ResourceService.ResourceItemType);
            if (!paidWithResource)
            {
                Log.Warning("Store: goods {GoodsId} is paid with real money or an untracked currency (price type {PriceType}); not supported.",
                    good.Id, good.PriceType);
                return Reply(MANKAOBLPBJ.Types.Code.CostError);
            }

            // PriceType is sent back to the client and must agree with the catalog row.
            // Do not silently charge a table-defined currency when the request names another one.
            if (request.PriceType != good.PriceType)
                return Reply(MANKAOBLPBJ.Types.Code.PriceAgain);

            string[] rewardParts = (good.Reward ?? string.Empty)
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            IReadOnlyList<(uint ItemId, int Count)> rewards = rewardService.ParseRewards(good.Reward);
            bool rewardsAreValid = rewardParts.Length > 0
                && rewards.Count == rewardParts.Length
                && rewards.All(reward => reward.ItemId > 0 && reward.Count > 0
                    && (tableService.GetTable<GunData>().Any(gun => gun.Id == reward.ItemId)
                        || tableService.GetTable<GunWeaponData>().Any(weapon => weapon.Id == reward.ItemId)
                        || tableService.GetTable<ItemData>().Any(item => item.Id == reward.ItemId)));
            if (!rewardsAreValid)
            {
                Log.Warning("Store: goods {GoodsId} has an empty, malformed, or unsupported reward '{Reward}'.", good.Id, good.Reward);
                return Reply(MANKAOBLPBJ.Types.Code.Locked);
            }

            Dictionary<uint, int> cost = new() { [good.PriceType] = (int)Math.Ceiling(good.Price) };
            if (!resourceService.TrySpend(uid, cost, out SC_ResourceUpdate spent))
                return Reply(MANKAOBLPBJ.Types.Code.CostError);

            List<IMessage> granted = [];
            int delivered = rewards.Count(reward => rewardService.Grant(uid, reward.ItemId, reward.Count, granted).Granted);
            if (delivered != rewards.Count)
            {
                // nothing could be delivered: give the money back instead of charging for nothing
                resourceService.Add(uid, cost);
                Log.Warning("Store: goods {GoodsId} could not be delivered; refunded.", good.Id);
                return Reply(MANKAOBLPBJ.Types.Code.Locked);
            }

            purchases[good.Id] = bought + 1;
            accountState.Set(uid, PurchasesKey, purchases);

            messages.AddRange(granted);
            messages.Add(spent);
            Log.Information("Store: account {Uid} bought goods {GoodsId} for {Cost} ({Bought}/{Limit}).",
                uid, good.Id, $"{good.PriceType}x{cost[good.PriceType]}", bought + 1, good.Limit == 0 ? "unlimited" : good.Limit);
            return Reply(MANKAOBLPBJ.Types.Code.Normal);
        }

        private Dictionary<uint, uint> Purchases(uint uid) =>
            accountState.Get<Dictionary<uint, uint>>(uid, PurchasesKey) ?? [];

        private static bool IsActive(StoreTagData tag, long gameNow)
        {
            ulong startsAt = TableFields.U64(tag, 12);
            ulong endsAt = TableFields.U64(tag, 13);
            return (startsAt == 0 || startsAt <= (ulong)gameNow)
                && (endsAt == 0 || endsAt >= (ulong)gameNow);
        }
    }
}
