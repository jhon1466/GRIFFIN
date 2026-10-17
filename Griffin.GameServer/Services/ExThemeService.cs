using Google.Protobuf;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    public interface IExThemeService : IGameService
    {
        IReadOnlySet<uint> Picked(uint uid);

        /// <summary>Pays the reward of a theme once; returns null when the theme does not exist or was already picked.</summary>
        Dictionary<uint, uint>? Pick(uint uid, uint themeId, List<IMessage> messages);
    }

    public sealed class ExThemeService(ITableService tableService, IAccountStateService accountState, IRewardService rewardService) : IExThemeService
    {
        private const string StateKey = "ex.themes.picked";

        public IReadOnlySet<uint> Picked(uint uid) => (accountState.Get<List<uint>>(uid, StateKey) ?? []).ToHashSet();

        public Dictionary<uint, uint>? Pick(uint uid, uint themeId, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                IMessage? row = tableService.GetTable<ExThemeData>().FirstOrDefault(r => TableFields.U(r, 1) == themeId);
                List<uint> picked = accountState.Get<List<uint>>(uid, StateKey) ?? [];
                if (row is null || picked.Contains(themeId))
                    return null;

                Dictionary<uint, uint> rewards = [];
                foreach ((uint item, uint count) in TableFields.Map(row, 9))
                {
                    rewardService.Grant(uid, item, (int)count, messages);
                    rewards[item] = rewards.GetValueOrDefault(item) + count;
                }

                picked.Add(themeId);
                accountState.Set(uid, StateKey, picked);
                return rewards;
        
            }
        }
    }
}
