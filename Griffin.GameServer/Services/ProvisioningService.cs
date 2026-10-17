using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Entities;
using Griffin.Database.Repositories;
using Griffin.Database.Services;
using Serilog;

namespace Griffin.GameServer.Services
{
    public sealed record ProvisionReport(int ItemsAdded, int ItemsRepaired, int ResourcesRepaired, IReadOnlyDictionary<uint, int> ChangedItems, SC_ResourceUpdate? ResourceUpdate);

    /// <summary>
    /// Gives an account the objects it needs to use every screen of the game (currencies, gifts, ingredients, interaction keys, furniture,
    /// cosmetics...). Until now only four resources were seeded at login and <c>/inventory addall item</c> neither ran by default nor repaired
    /// balances that were stored as 0, so screens showed "0/50" for objects the client tables list (the dormitory currency 1610000, the
    /// interaction key 1630001, the generic gift 300003...).
    /// <para>Rules: the objects come from <c>ItemData</c> of the client (the categories listed in <see cref="Amounts"/>, each with the amount
    /// the category is given); a missing row is created, a row at 0 is raised, a positive balance is NEVER touched, and the type of an existing
    /// row is corrected from the table. Battle pass, character, weapon and mod categories are not included (they have their own entities or
    /// change progress). Resources (<c>ItemData</c> type 1) go through <see cref="IResourceService"/>; item 44 (pass points) is excluded.
    /// It runs on demand (<c>/inventory provision</c>) and once per account at login (flag <c>provision.auto.v1</c>, disabled with
    /// <c>GRIFFIN_AUTO_PROVISION=0</c>), so a balance the player spends afterwards is not refilled at the next login.</para>
    /// </summary>
    public interface IProvisioningService : IGameService
    {
        ProvisionReport Provision(uint uid);

        /// <summary>Provisions once per account (login); returns null when it already ran or is disabled.</summary>
        ProvisionReport? ProvisionOnce(uint uid);
    }

    public sealed class ProvisioningService(
        IAccountService accountService,
        IItemRepository itemRepository,
        ITableService tableService,
        IResourceService resourceService,
        IAccountStateService accountState) : IProvisioningService
    {
        private const string FlagKey = "provision.auto.v2"; // v2: tipo 304 (tienda ES) añadido; se vuelve a ejecutar una vez por cuenta (no toca saldos positivos)
        private const int ResourceAmount = 99999;
        private static readonly uint[] ExcludedResources = [44];
        private static readonly object Gate = new();

        /// <summary>ItemData type -> amount given. Everything else is left alone.</summary>
        public static readonly IReadOnlyDictionary<uint, int> Amounts = new Dictionary<uint, int>
        {
            [2] = 99999, [3] = 99999, [4] = 99999,                       // puzzles/tickets, experience-like values, collectibles and currencies (1610000)
            [304] = 99999,                                               // ES shop currency and materials (3041001 "ES Cash" pays the black-market offers)
            [122] = 99,                                                  // gifts
            [172] = 99, [173] = 99, [174] = 99,                          // kitchen: seasonings, ingredients, dishes
            [162] = 10,                                                  // interaction keys (1630001...)
            [12] = 1, [13] = 1, [60] = 1, [61] = 1,                      // portraits, outfits, weapon / mod paints
            [132] = 1, [133] = 1, [134] = 1,                             // gift-like decorations
            [161] = 1, [164] = 1, [165] = 1, [166] = 1, [167] = 1, [168] = 1, [169] = 1,  // lobby furniture and recipes
            [175] = 1, [176] = 1, [177] = 1, [179] = 1, [181] = 1, [182] = 1, [183] = 1, [184] = 1, [186] = 1,
        };

        public ProvisionReport Provision(uint uid)
        {
            lock (Gate)
            {
                AccountEntity account = accountService.GetByUid(uid) ?? throw new InvalidOperationException($"Account '{uid}' was not found.");
                Dictionary<uint, ItemEntity> rows = account.Items.GroupBy(i => i.ItemId).ToDictionary(g => g.Key, g => g.First());
                Dictionary<uint, int> changed = [];
                Dictionary<uint, int> resourceGains = [];
                int added = 0, repaired = 0;

                foreach (ItemData data in tableService.GetTable<ItemData>())
                {
                    if (data.Id == 0)
                        continue;

                    if (data.Type == ResourceService.ResourceItemType)
                    {
                        if (!ExcludedResources.Contains(data.Id) && resourceService.GetBalance(uid, data.Id) <= 0)
                            resourceGains[data.Id] = ResourceAmount;
                        continue;
                    }

                    if (!Amounts.TryGetValue((uint)data.Type, out int amount))
                        continue;

                    if (!rows.TryGetValue(data.Id, out ItemEntity? row))
                    {
                        ItemEntity created = new() { ItemId = data.Id, Type = data.Type, Count = amount };
                        account.Items.Add(created);
                        rows[data.Id] = created;
                        changed[data.Id] = amount;
                        added++;
                    }
                    else if (row.Count <= 0)
                    {
                        row.Count = amount;
                        row.Type = data.Type;
                        changed[data.Id] = amount;
                        repaired++;
                    }
                    else if (row.Type != data.Type)
                    {
                        row.Type = data.Type;
                    }
                }

                if (added + repaired > 0 || rows.Count > 0)
                    itemRepository.SaveChanges();

                SC_ResourceUpdate? update = resourceGains.Count > 0 ? resourceService.Add(uid, resourceGains) : null;
                Log.Information("Provisioning of account {Uid}: {Added} item(s) added, {Repaired} raised from 0, {Resources} resource(s) raised from 0.",
                    uid, added, repaired, resourceGains.Count);
                return new ProvisionReport(added, repaired, resourceGains.Count, changed, update);
            }
        }

        public ProvisionReport? ProvisionOnce(uint uid)
        {
            if (Environment.GetEnvironmentVariable("GRIFFIN_AUTO_PROVISION") == "0" || accountState.Get<bool>(uid, FlagKey))
                return null;

            ProvisionReport report = Provision(uid);
            accountState.Set(uid, FlagKey, true);
            return report;
        }
    }
}
