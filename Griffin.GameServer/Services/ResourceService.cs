using Google.Protobuf.Collections;
using Griffin.Common.Proto;
using Griffin.Database.Entities;
using Griffin.Database.Repositories;
using Griffin.Database.Services;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Per-account balances of resource items (currencies, stamina...). In the client tables the items of type 1 are the
    /// resources and <c>Resource.resource_ex</c> is indexed by item id. Balances live in <see cref="ItemEntity"/> rows with
    /// <c>Type == 1</c>, so no migration is needed. Spending is all-or-nothing and returns the
    /// <see cref="SC_ResourceUpdate"/> the client needs to refresh its counters.
    /// </summary>
    public interface IResourceService : IGameService
    {
        int GetBalance(uint accountUid, uint itemId);

        /// <summary>Gives the account the starting balance of every listed resource it does not have a row for yet.</summary>
        void SeedMissing(uint accountUid, IEnumerable<KeyValuePair<uint, int>> startingBalances);

        /// <summary>Overwrites the balances in an <c>SC_Resource</c> with the persisted ones.</summary>
        void ApplyTo(uint accountUid, Resource resource);

        bool CanAfford(uint accountUid, IReadOnlyDictionary<uint, int> cost);

        /// <summary>Deducts <paramref name="cost"/> atomically; false (and nothing changes) when any balance is short.</summary>
        bool TrySpend(uint accountUid, IReadOnlyDictionary<uint, int> cost, out SC_ResourceUpdate update);

        SC_ResourceUpdate Add(uint accountUid, IReadOnlyDictionary<uint, int> gains);
    }

    public sealed class ResourceService(IItemRepository itemRepository, IAccountService accountService) : IResourceService
    {
        public const int ResourceItemType = 1;

        public int GetBalance(uint accountUid, uint itemId)
        {
            ItemEntity? row = Rows(accountUid).FirstOrDefault(i => i.ItemId == itemId);
            return row?.Count ?? 0;
        }

        /// <summary>
        /// Serializes seeding: the account entity is shared (cached) by every connection of the account, so two logins of a brand-new
        /// account at the same moment would both see "no row" and both add it, which breaks the unique index (AccountUid, ItemId).
        /// </summary>
        private static readonly object SeedLock = new();

        public void SeedMissing(uint accountUid, IEnumerable<KeyValuePair<uint, int>> startingBalances)
        {
            lock (SeedLock)
                SeedMissingLocked(accountUid, startingBalances);
        }

        private void SeedMissingLocked(uint accountUid, IEnumerable<KeyValuePair<uint, int>> startingBalances)
        {
            AccountEntity account = Account(accountUid);
            int seeded = 0;

            foreach ((uint itemId, int amount) in startingBalances)
            {
                if (itemId == 0 || amount <= 0)
                    continue;

                // a resource the account already has a row for keeps its balance (spending can legitimately leave it at 0)
                if (account.Items.Any(i => i.ItemId == itemId && i.Type == ResourceItemType))
                    continue;

                // an old "give every item" command may have created the row with another type; reuse it
                ItemEntity? existing = account.Items.FirstOrDefault(i => i.ItemId == itemId);
                if (existing is null)
                    account.Items.Add(new ItemEntity { ItemId = itemId, Type = ResourceItemType, Count = amount });
                else
                {
                    existing.Type = ResourceItemType;
                    existing.Count = amount;
                }

                seeded++;
            }

            if (seeded == 0)
                return;

            itemRepository.SaveChanges();
            Log.Information("Seeded {Count} starting resource balance(s) for account {Uid}.", seeded, accountUid);
        }

        public void ApplyTo(uint accountUid, Resource resource)
        {
            RepeatedField<int> values = resource.ResourceEx;
            foreach (ItemEntity row in Rows(accountUid))
            {
                while (values.Count <= row.ItemId)
                    values.Add(0);

                values[(int)row.ItemId] = row.Count;
            }
        }

        public bool CanAfford(uint accountUid, IReadOnlyDictionary<uint, int> cost)
        {
            AccountEntity account = Account(accountUid);
            foreach ((uint itemId, int amount) in cost)
            {
                if (amount < 0)
                    return false;

                int have = account.Items.FirstOrDefault(i => i.ItemId == itemId && i.Type == ResourceItemType)?.Count ?? 0;
                if (have < amount)
                    return false;
            }

            return true;
        }

        public bool TrySpend(uint accountUid, IReadOnlyDictionary<uint, int> cost, out SC_ResourceUpdate update)
        {
            update = new SC_ResourceUpdate();
            if (!CanAfford(accountUid, cost))
                return false;

            AccountEntity account = Account(accountUid);
            foreach ((uint itemId, int amount) in cost)
            {
                if (amount == 0)
                    continue;

                ItemEntity row = account.Items.First(i => i.ItemId == itemId && i.Type == ResourceItemType);
                row.Count -= amount;
                update.ResourceEx[itemId] = row.Count;
            }

            itemRepository.SaveChanges();
            return true;
        }

        public SC_ResourceUpdate Add(uint accountUid, IReadOnlyDictionary<uint, int> gains)
        {
            AccountEntity account = Account(accountUid);
            SC_ResourceUpdate update = new();

            foreach ((uint itemId, int amount) in gains)
            {
                if (itemId == 0 || amount <= 0)
                    continue;

                ItemEntity? row = account.Items.FirstOrDefault(i => i.ItemId == itemId && i.Type == ResourceItemType);
                if (row is null)
                {
                    row = new ItemEntity { ItemId = itemId, Type = ResourceItemType, Count = 0 };
                    account.Items.Add(row);
                }

                row.Count = (int)Math.Min((long)row.Count + amount, int.MaxValue);
                update.ResourceEx[itemId] = row.Count;
            }

            itemRepository.SaveChanges();
            return update;
        }

        private AccountEntity Account(uint accountUid) =>
            accountService.GetByUid(accountUid)
            ?? throw new InvalidOperationException($"Account with uid '{accountUid}' was not found.");

        private IEnumerable<ItemEntity> Rows(uint accountUid) =>
            Account(accountUid).Items.Where(i => i.Type == ResourceItemType);
    }
}
