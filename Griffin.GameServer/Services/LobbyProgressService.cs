using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    public sealed record LobbyProgress(uint Level, ulong Exp, IReadOnlyList<uint> ClaimedLevels);

    /// <summary>
    /// Lobby (dormitory) level and its rewards. <c>LobbyLevelExpData</c>: 1 = level, 2 = accumulated experience, 3 = reward list.
    /// Assumptions: the base state is the one the server has always announced (experience 40360, rewards of levels 2..49
    /// already claimed) plus the item 201 ("大休息室经验", the lobby experience item) the account owns, so any reward that grants
    /// that item raises the lobby level; claimed levels are stored in <c>lobby.levels</c>. The weekly heat (<c>LobbyWeeklyRewardData</c>)
    /// has no source on this server and stays 0, so weekly rewards are refused.
    /// </summary>
    public interface ILobbyProgressService : IGameService
    {
        const ulong BaseExp = 40360;
        const uint ExpItem = 201;

        LobbyProgress Get(uint uid);

        /// <summary>Claims the rewards of the given levels; returns the claimed ones (empty when nothing is claimable).</summary>
        IReadOnlyList<uint> Claim(uint uid, IEnumerable<uint> levels, List<IMessage> messages, out HNINDAOJCKP error);
    }

    public sealed class LobbyProgressService(
        ITableService tableService,
        IAccountStateService accountState,
        IItemService itemService,
        IRewardService rewardService) : ILobbyProgressService
    {
        private const string StateKey = "lobby.levels";

        public LobbyProgress Get(uint uid)
        {
            ulong exp = ILobbyProgressService.BaseExp
                + (ulong)Math.Max(0, itemService.GetPlayerItems(uid).FirstOrDefault(i => i.ItemId == ILobbyProgressService.ExpItem)?.Count ?? 0);
            uint level = 1;
            foreach (IMessage row in tableService.GetTable<LobbyLevelExpData>().OrderBy(r => TableFields.U(r, 1)))
                if (TableFields.U(row, 2) <= exp || TableFields.U(row, 1) == 1)
                    level = TableFields.U(row, 1);

            List<uint> claimed = Enumerable.Range(2, 48).Select(l => (uint)l).ToList();
            claimed.AddRange(accountState.Get<List<uint>>(uid, StateKey) ?? []);
            return new LobbyProgress(level, exp, claimed.Distinct().OrderBy(l => l).ToList());
        }

        public IReadOnlyList<uint> Claim(uint uid, IEnumerable<uint> levels, List<IMessage> messages, out HNINDAOJCKP error)
        {
            error = HNINDAOJCKP.LobbyLevelRewardClaimed;
            LobbyProgress progress = Get(uid);
            List<uint> stored = accountState.Get<List<uint>>(uid, StateKey) ?? [];
            List<uint> done = [];
            foreach (uint level in levels.Distinct())
            {
                if (progress.ClaimedLevels.Contains(level))
                    continue;

                IMessage? row = tableService.GetTable<LobbyLevelExpData>().FirstOrDefault(r => TableFields.U(r, 1) == level);
                if (row is null || level > progress.Level)
                {
                    error = HNINDAOJCKP.LobbyLevelNotEnough;
                    continue;
                }

                foreach ((uint item, uint count) in TableFields.Map(row, 3))
                    rewardService.Grant(uid, item, (int)count, messages);

                stored.Add(level);
                done.Add(level);
            }

            if (done.Count > 0)
                accountState.Set(uid, StateKey, stored);
            return done;
        }
    }
}
