using Griffin.Common.Table;
using Griffin.Database.Entities;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Dolls that reached love level 5 (the vow level, LoveConfigData column 5) before the server granted LoveConfigData column 9
    /// (300002 铭契投影) never got it. At login every such doll gets it once; the dolls already served are remembered.
    /// </summary>
    public interface ILoveBackfillService : IGameService
    {
        void Backfill(uint accountUid);

        /// <summary>Marks the doll as served and returns what it gets (empty when it already had it).</summary>
        IReadOnlyList<(uint Item, uint Count)> Claim(uint accountUid, uint gunId);

        /// <summary>
        /// Gives the affinity keys (契合键, TalentKeyData id 910000 + doll, e.g. 911080 for Astia) of the given dolls that the account does
        /// not have yet, as public talent keys (the list of <see cref="Handlers.PublicTalentHandler"/>). Returns the keys added.
        /// </summary>
        IReadOnlyList<Handlers.PublicTalentHandler.ExtraItem> GrantAffinityKeys(uint accountUid, IEnumerable<uint> gunIds);

        /// <summary>The affinity keys of every owned doll that reached the level that unlocks them (love level 5).</summary>
        IReadOnlyList<Handlers.PublicTalentHandler.ExtraItem> BackfillAffinityKeys(uint accountUid);
    }

    public sealed class LoveBackfillService(
        ITableService tableService,
        IAccountStateService accountState,
        IGunService gunService,
        IItemService itemService) : ILoveBackfillService
    {
        private const string StateKey = "love.vowprojection.granted";
        private const uint VowLevel = 9;

        private List<(uint Item, uint Count)> Rewards(out uint startLevel)
        {
            startLevel = 5;
            if (tableService.GetTable<LoveConfigData>().FirstOrDefault() is not { } config)
                return [];
            startLevel = TableFields.U(config, 5) is var configured and > 0 ? configured : 5;
            HashSet<uint> levelNine = tableService.GetTable<LoveLevelData>().Where(l => TableFields.U(l, 1) == VowLevel)
                .SelectMany(l => TableFields.Map(l, 7).Select(p => p.Key)).ToHashSet();
            return TableFields.Map(config, 9).Where(p => p.Value > 0 && !levelNine.Contains(p.Key)).ToList();
        }

        public IReadOnlyList<(uint Item, uint Count)> Claim(uint accountUid, uint gunId)
        {
            List<(uint Item, uint Count)> rewards = Rewards(out _);
            lock (AccountLock.For(accountUid))
            {
                HashSet<uint> served = accountState.Get<HashSet<uint>>(accountUid, StateKey) ?? [];
                if (rewards.Count == 0 || !served.Add(gunId))
                    return [];
                accountState.Set(accountUid, StateKey, served);
                return rewards;
            }
        }

        public const uint AffinityKeyBase = 910000;

        public IReadOnlyList<Handlers.PublicTalentHandler.ExtraItem> GrantAffinityKeys(uint accountUid, IEnumerable<uint> gunIds)
        {
            HashSet<uint> known = tableService.GetTable<TalentKeyData>().Select(r => TableFields.U(r, 1)).ToHashSet();
            List<Handlers.PublicTalentHandler.ExtraItem> added = [];
            lock (AccountLock.For(accountUid))
            {
                List<Handlers.PublicTalentHandler.ExtraItem> extra =
                    accountState.Get<List<Handlers.PublicTalentHandler.ExtraItem>>(accountUid, Handlers.PublicTalentHandler.ExtraKey) ?? [];
                foreach (uint gunId in gunIds.Distinct())
                {
                    uint key = AffinityKeyBase + gunId;
                    if (!known.Contains(key) || extra.Any(e => e.StcId == key))
                        continue;
                    // same id scheme as the keys of a completed talent tree (GunHandler.HandleGetGunPublicSkillItem)
                    Handlers.PublicTalentHandler.ExtraItem item = new() { Id = 1843288605358900000UL + key, StcId = key };
                    extra.Add(item);
                    added.Add(item);
                }

                if (added.Count > 0)
                    accountState.Set(accountUid, Handlers.PublicTalentHandler.ExtraKey, extra);
            }

            return added;
        }

        public IReadOnlyList<Handlers.PublicTalentHandler.ExtraItem> BackfillAffinityKeys(uint accountUid)
        {
            Rewards(out uint startLevel);
            return GrantAffinityKeys(accountUid, gunService.GetPlayerGuns(accountUid).Where(g => g.LoveLevel >= startLevel).Select(g => g.GunId));
        }

        public void Backfill(uint accountUid)
        {
            if (tableService.GetTable<LoveConfigData>().FirstOrDefault() is not { } config)
                return;

            uint startLevel = TableFields.U(config, 5) is var configured and > 0 ? configured : 5;
            HashSet<uint> levelNine = tableService.GetTable<LoveLevelData>().Where(l => TableFields.U(l, 1) == VowLevel)
                .SelectMany(l => TableFields.Map(l, 7).Select(p => p.Key)).ToHashSet();
            List<(uint Item, uint Count)> rewards = TableFields.Map(config, 9).Where(p => p.Value > 0 && !levelNine.Contains(p.Key)).ToList();
            if (rewards.Count == 0)
                return;

            lock (AccountLock.For(accountUid))
            {
                HashSet<uint> served = accountState.Get<HashSet<uint>>(accountUid, StateKey) ?? [];
                bool changed = false;
                foreach (GunEntity gun in gunService.GetPlayerGuns(accountUid))
                {
                    if (gun.LoveLevel < startLevel || !served.Add(gun.GunId))
                        continue;

                    foreach ((uint item, uint count) in rewards)
                    {
                        int type = tableService.GetTable<ItemData>().FirstOrDefault(i => i.Id == item)?.Type ?? 4;
                        itemService.Grant(accountUid, item, type, (int)count);
                    }
                    changed = true;
                }

                if (changed)
                    accountState.Set(accountUid, StateKey, served);
            }
        }
    }
}
