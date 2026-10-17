using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// What the backpack can do with items, from <c>ItemData</c> read by field number (checked against real rows of client
    /// 1189260): column 6 = type, column 18 = payload string, column 23 = sale value (item → amount), column 26 = recipe
    /// (item → amount of one unit of the lower tier).
    /// <list type="bullet">
    /// <item><b>Dismantle</b>: an item with a sale value is removed and its value granted per unit.</item>
    /// <item><b>Compose</b>: the target's recipe (column 26) times the quantity is spent and the item is granted.</item>
    /// <item><b>Use</b>: type 6 packs hold fixed contents (<c>item:count,item:count</c>); types 5 and 105 are boxes whose payload is
    /// a <c>DropPackageData</c> id (<c>item:count:weight</c> entries, one weighted pick per unit used).</item>
    /// </list>
    /// Anything else is refused with an error code (<c>ItemCanNotSold</c>, <c>ItemConfigNotExist</c>, <c>ItemCannotUse</c>,
    /// <c>ItemNotEnough</c>) instead of inventing an effect; <c>HNINDAOJCKP.None</c> means success.
    /// </summary>
    public interface IItemUseService : IGameService
    {
        HNINDAOJCKP Dismantle(uint uid, IReadOnlyDictionary<uint, uint> items, List<IMessage> messages);

        HNINDAOJCKP Compose(uint uid, uint itemId, uint amount, List<IMessage> messages);

        HNINDAOJCKP Use(uint uid, uint itemId, uint amount, List<IMessage> messages);

        /// <summary>Opens a keepsake (type 134) whose payload lists the items it offers: the player chooses one of them.</summary>
        HNINDAOJCKP UseGiftPick(uint uid, uint itemId, IReadOnlyList<uint> selected, uint amount, List<IMessage> messages);

        /// <summary>One weighted pick of a <c>DropPackageData</c> package (<c>item:count:weight</c> entries); empty when it drops nothing (count 0).</summary>
        IReadOnlyList<(uint Item, int Count)> RollPackage(uint packageId);

        /// <summary>Spends currencies and items together, appending <c>SC_ResourceUpdate</c> / <c>SC_Items</c> for what changed.</summary>
        bool Spend(uint uid, IReadOnlyDictionary<uint, int> cost, List<IMessage> messages);
    }

    public sealed class ItemUseService(
        ITableService tableService,
        IResourceService resourceService,
        IItemService itemService,
        IRewardService rewardService) : IItemUseService
    {
        private static readonly HashSet<int> PackTypes = [6, 109];
        private const int GiftPickType = 134;
        private static readonly HashSet<int> BoxTypes = [5, 105];

        public HNINDAOJCKP Dismantle(uint uid, IReadOnlyDictionary<uint, uint> items, List<IMessage> messages)
        {
            if (items.Count == 0)
                return HNINDAOJCKP.ItemUseNumInvalid;

            Dictionary<uint, int> cost = [];
            Dictionary<uint, int> gains = [];
            foreach ((uint itemId, uint amount) in items)
            {
                ItemData? row = Find(itemId);
                IReadOnlyList<(uint Key, uint Value)> value = row is null ? [] : TableFields.Map(row, 23);
                if (amount == 0 || value.Count == 0 || row!.Type == ResourceService.ResourceItemType)
                {
                    Log.Warning("Dismantle: item {Item} has no sale value.", itemId);
                    return amount == 0 ? HNINDAOJCKP.ItemUseNumInvalid : HNINDAOJCKP.ItemCanNotSold;
                }

                cost[itemId] = (int)amount;
                foreach ((uint gain, uint count) in value)
                    gains[gain] = gains.GetValueOrDefault(gain) + (int)(count * amount);
            }

            if (!Spend(uid, cost, messages))
                return HNINDAOJCKP.ItemNotEnough;

            foreach ((uint gain, int count) in gains)
                rewardService.Grant(uid, gain, count, messages);
            return HNINDAOJCKP.None;
        }

        public HNINDAOJCKP Compose(uint uid, uint itemId, uint amount, List<IMessage> messages)
        {
            ItemData? row = Find(itemId);
            IReadOnlyList<(uint Key, uint Value)> recipe = row is null ? [] : TableFields.Map(row, 26);
            if (amount == 0 || recipe.Count == 0)
            {
                Log.Warning("Compose: item {Item} has no recipe.", itemId);
                return amount == 0 ? HNINDAOJCKP.ItemUseNumInvalid : HNINDAOJCKP.ItemConfigNotExist;
            }

            Dictionary<uint, int> cost = recipe.ToDictionary(pair => pair.Key, pair => (int)(pair.Value * amount));
            if (!Spend(uid, cost, messages))
                return HNINDAOJCKP.ItemNotEnough;

            rewardService.Grant(uid, itemId, (int)amount, messages);
            return HNINDAOJCKP.None;
        }

        public HNINDAOJCKP Use(uint uid, uint itemId, uint amount, List<IMessage> messages)
        {
            ItemData? row = Find(itemId);
            string payload = row is null ? string.Empty : TableFields.S(row, 18);
            if (amount == 0)
                return HNINDAOJCKP.ItemUseNumInvalid;

            if (row is null || payload.Length == 0)
                return HNINDAOJCKP.ItemCannotUse;

            List<(uint Item, int Count)> contents = [];
            if (PackTypes.Contains(row.Type) && payload.Contains(':'))
            {
                foreach ((uint item, int count) in rewardService.ParseRewards(payload))
                    contents.Add((item, count * (int)amount));
            }
            else if (BoxTypes.Contains(row.Type) && uint.TryParse(payload, out uint package))
            {
                List<(uint Item, int Count, int Weight)> entries = Package(package);
                if (entries.Count == 0)
                    return HNINDAOJCKP.ItemConfigNotExist;

                for (uint i = 0; i < amount; i++)
                {
                    int roll = Random.Shared.Next(entries.Sum(e => e.Weight));
                    foreach ((uint item, int count, int weight) in entries)
                    {
                        if ((roll -= weight) >= 0)
                            continue;

                        contents.Add((item, count));
                        break;
                    }
                }
            }
            else
            {
                Log.Warning("Use: item {Item} (type {Type}) has no usable payload '{Payload}'.", itemId, row.Type, payload);
                return HNINDAOJCKP.ItemCannotUse;
            }

            if (contents.Count == 0)
                return HNINDAOJCKP.ItemCannotUse;

            if (!Spend(uid, new Dictionary<uint, int> { [itemId] = (int)amount }, messages))
                return HNINDAOJCKP.ItemNotEnough;

            foreach ((uint item, int count) in contents)
                rewardService.Grant(uid, item, count, messages);
            return HNINDAOJCKP.None;
        }

        public HNINDAOJCKP UseGiftPick(uint uid, uint itemId, IReadOnlyList<uint> selected, uint amount, List<IMessage> messages)
        {
            ItemData? row = Find(itemId);
            if (amount == 0)
                return HNINDAOJCKP.ItemUseNumInvalid;

            if (row is null || row.Type != GiftPickType)
                return HNINDAOJCKP.ItemCannotUse;

            HashSet<uint> offered = TableFields.S(row, 18).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => uint.TryParse(part, out uint id) ? id : 0u).Where(id => id != 0).ToHashSet();
            if (selected.Count != 1 || !offered.Contains(selected[0]))
                return HNINDAOJCKP.MailGiftPickItemInvalid;

            // the offered items of the 1189260 keepsakes are lobby furniture, which this server does not store as inventory
            if (Find(selected[0]) is null)
                return HNINDAOJCKP.ItemConfigNotExist;

            if (!Spend(uid, new Dictionary<uint, int> { [itemId] = (int)amount }, messages))
                return HNINDAOJCKP.ItemNotEnough;

            rewardService.Grant(uid, selected[0], (int)amount, messages);
            return HNINDAOJCKP.None;
        }

        public bool Spend(uint uid, IReadOnlyDictionary<uint, int> cost, List<IMessage> messages)
        {
            Dictionary<uint, int> resources = [];
            Dictionary<uint, int> items = [];
            foreach ((uint id, int amount) in cost)
                (Find(id)?.Type == ResourceService.ResourceItemType ? resources : items)[id] = amount;

            if (!resourceService.CanAfford(uid, resources))
                return false;

            if (items.Count > 0)
            {
                if (!itemService.TrySpend(uid, items, out SC_Items spentItems))
                    return false;

                messages.Add(spentItems);
            }

            if (resources.Count > 0 && resourceService.TrySpend(uid, resources, out SC_ResourceUpdate spent))
                messages.Add(spent);
            return true;
        }

        public IReadOnlyList<(uint Item, int Count)> RollPackage(uint packageId)
        {
            List<(uint Item, int Count, int Weight)> entries = Package(packageId);
            if (entries.Count == 0)
                return [];

            int roll = Random.Shared.Next(entries.Sum(e => e.Weight));
            foreach ((uint item, int count, int weight) in entries)
            {
                if ((roll -= weight) >= 0)
                    continue;

                return count > 0 ? [(item, count)] : [];
            }

            return [];
        }

        private List<(uint Item, int Count, int Weight)> Package(uint id)
        {
            List<(uint, int, int)> entries = [];
            foreach (DropPackageData row in tableService.GetTable<DropPackageData>().Where(r => r.Id == id && r.Type == 1))
            {
                foreach (string entry in Strings(row))
                {
                    string[] parts = entry.Split(':');
                    if (parts.Length == 3 && uint.TryParse(parts[0], out uint item) && int.TryParse(parts[1], out int count) && int.TryParse(parts[2], out int weight) && weight > 0)
                        entries.Add((item, count, weight));
                }
            }

            return entries;
        }

        private static IEnumerable<string> Strings(IMessage row)
        {
            var field = row.Descriptor.FindFieldByNumber(2);
            object? value = field?.Accessor.GetValue(row);
            return value switch
            {
                System.Collections.IEnumerable list when value is not string => list.Cast<object>().Select(o => o.ToString() ?? string.Empty),
                string text => [text],
                _ => [],
            };
        }

        private ItemData? Find(uint id) => tableService.GetTable<ItemData>().FirstOrDefault(i => i.Id == id);
    }
}
