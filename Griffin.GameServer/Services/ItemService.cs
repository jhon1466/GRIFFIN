using Griffin.Database.Entities;
using Griffin.Database.Repositories;
using Griffin.Database.Services;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    public sealed class ItemService(
        IItemRepository itemRepository,
        IAccountService accountService,
        ITableService tableService) : IItemService
    {
        private const int DefaultItemCount = 999;

        public void AddAllItems(uint accountUid)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            List<ItemData> itemData = tableService.GetTable<ItemData>();
            var existingItemIds = new HashSet<uint>(account.Items.Select(i => i.ItemId));

            var newItems = itemData
                .Where(d => d.Id != 0 && !existingItemIds.Contains(d.Id))
                .Select(d => new ItemEntity
                {
                    ItemId = d.Id,
                    Type = d.Type,
                    Count = DefaultItemCount,
                })
                .ToList();

            account.AddItems(newItems);
            itemRepository.SaveChanges();
        }

        public ItemEntity[] GetPlayerItems(uint accountUid)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            return account.Items.ToArray();
        }

        public void AddItem(uint accountUid, ItemEntity item)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            account.Items.Add(item);
            itemRepository.SaveChanges();
        }

        public void Grant(uint accountUid, uint itemId, int type, int amount)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            ItemEntity? existing = account.Items.FirstOrDefault(i => i.ItemId == itemId);
            if (existing is null)
                account.Items.Add(new ItemEntity { ItemId = itemId, Type = type, Count = amount });
            else
                existing.Count = (int)Math.Min((long)existing.Count + amount, int.MaxValue);

            itemRepository.SaveChanges();
        }

        public bool TrySpend(uint accountUid, IReadOnlyDictionary<uint, int> cost, out Common.Proto.SC_Items update)
        {
            update = new Common.Proto.SC_Items();
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");

            foreach ((uint itemId, int amount) in cost)
            {
                int have = account.Items.FirstOrDefault(i => i.ItemId == itemId)?.Count ?? 0;
                if (amount < 0 || have < amount)
                    return false;
            }

            foreach ((uint itemId, int amount) in cost)
            {
                if (amount == 0)
                    continue;

                ItemEntity row = account.Items.First(i => i.ItemId == itemId);
                row.Count -= amount;
                update.Items[itemId] = row.Count;
            }

            itemRepository.SaveChanges();
            return true;
        }

        public bool RemoveItem(uint accountUid, ItemEntity item)
        {
            var account = accountService.GetByUid(accountUid)
                ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");
            if (!account.Items.Remove(item))
                return false;

            itemRepository.SaveChanges();
            return true;
        }
    }

    public interface IItemService : IGameService
    {
        ItemEntity[] GetPlayerItems(uint accountUid);
        void AddItem(uint accountUid, ItemEntity item);
        bool RemoveItem(uint accountUid, ItemEntity item);
        void AddAllItems(uint accountUid);

        /// <summary>Adds <paramref name="amount"/> to the stack of <paramref name="itemId"/> (creating it when missing).</summary>
        void Grant(uint accountUid, uint itemId, int type, int amount);

        /// <summary>Takes <paramref name="cost"/> from the item stacks all-or-nothing; the update carries the new counts.</summary>
        bool TrySpend(uint accountUid, IReadOnlyDictionary<uint, int> cost, out Common.Proto.SC_Items update);
    }
}
