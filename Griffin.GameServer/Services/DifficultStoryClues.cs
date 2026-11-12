using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The "field echoes" of the hard story (DifficultyStoryData): the first clear of a stage (its battle is the StageData row with the same id,
    /// e.g. 24101 "HD-1-1"; column 14 lists the echo fragments shown on the screen) gives the clue
    /// of column 12 (e.g. 1-1 gives 1 x item 8119 "一本旧书"), and the next stage is analysed (unlocked) by spending the clue of its column 11.
    /// The server never gave the clue, so 1-2 and the rest could not be unlocked ("一本旧书不足") (A-208).
    /// </summary>
    public static class DifficultStoryClues
    {
        private const string ClearedKey = "difficult.cleared";

        /// <summary>The DifficultyStoryData row whose battle stage is <paramref name="stageId"/> (same id), if any.</summary>
        public static DifficultyStoryData? RowOfStage(ITableService tables, uint stageId) =>
            tables.GetTable<DifficultyStoryData>().FirstOrDefault(row => row.Id == stageId);

        public static HashSet<uint> Cleared(IAccountStateService accountState, uint uid) =>
            accountState.Get<HashSet<uint>>(uid, ClearedKey) ?? [];

        /// <summary>A won battle: the first clear of a hard story stage pays its clue once.</summary>
        public static void OnStageWon(ITableService tables, IAccountStateService accountState, IRewardService rewards, uint uid, uint stageId, List<IMessage> push)
        {
            if (RowOfStage(tables, stageId) is { } row)
                Pay(tables, accountState, rewards, uid, [row.Id], push);
        }

        /// <summary>Stages cleared before the clue existed on this server (their battle stage has a first-pass record) get it now.</summary>
        public static void Backfill(ITableService tables, IAccountStateService accountState, IRewardService rewards, uint uid, List<IMessage> push)
        {
            Dictionary<string, StageRecord> records = accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? [];
            List<uint> passed = tables.GetTable<DifficultyStoryData>()
                .Where(row => records.TryGetValue(row.Id.ToString(), out StageRecord? record) && record.FirstPassTime != 0)
                .Select(row => row.Id).ToList();
            Pay(tables, accountState, rewards, uid, passed, push);
        }

        private static void Pay(ITableService tables, IAccountStateService accountState, IRewardService rewards, uint uid, IEnumerable<uint> storyIds, List<IMessage> push)
        {
            HashSet<uint> cleared = Cleared(accountState, uid);
            bool changed = false;
            foreach (uint storyId in storyIds)
            {
                if (!cleared.Add(storyId))
                    continue;
                changed = true;
                if (tables.GetTable<DifficultyStoryData>().FirstOrDefault(row => row.Id == storyId) is not { } row)
                    continue;
                foreach ((uint item, uint count) in TableFields.Map(row, 12))
                    if (count > 0)
                        rewards.Grant(uid, item, (int)count, push);
            }
            if (changed)
                accountState.Set(uid, ClearedKey, cleared);
        }
    }
}
