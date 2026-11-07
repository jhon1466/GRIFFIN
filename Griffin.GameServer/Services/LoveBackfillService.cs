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
