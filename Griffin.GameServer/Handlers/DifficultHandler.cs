using System.Collections;
using System.Reflection;
using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class DifficultHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IResourceService resources,
        IItemUseService itemUse,
        IRewardService rewardService) : DifficultHandlerBase
    {

        public override void HandleDifficultInfo(CS_DifficultInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            // clues of stages cleared before the server paid them (A-208)
            List<IMessage> clues = [];
            DifficultStoryClues.Backfill(tableService, accountState, rewardService, uid, clues);
            if (clues.Count > 0)
                connection.SendAutoEncrypted(clues.ToArray());
            Dictionary<uint, DifficultStoryState> stories = accountState.Get<Dictionary<uint, DifficultStoryState>>(uid, "difficult.stories") ?? [];
            Dictionary<uint, uint> chapterRaids = accountState.Get<Dictionary<uint, uint>>(uid, "difficult.chapter_raids") ?? [];
            Dictionary<uint, uint> storyRaids = accountState.Get<Dictionary<uint, uint>>(uid, "difficult.story_raids") ?? [];
            Dictionary<string, HashSet<uint>> chapterClaims =
                accountState.Get<Dictionary<string, HashSet<uint>>>(uid, DifficultChapterHandler.ClaimedStateKey) ?? [];
            connection.Send(new SC_DifficultInfo
            {
                Difficult = BuildState(stories, chapterRaids, storyRaids, chapterClaims),
            });
        }

        public override void HandleDifficultUnlockStory(CS_DifficultUnlockStory request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            IMessage? row = DifficultyStories().FirstOrDefault(item => TableFields.U(item, 1) == request.StoryId);
            if (row is null)
            {
                connection.Send(new SC_DifficultUnlockStory { StoryId = request.StoryId });
                return;
            }

            lock (DifficultAccountLocks.For(uid))
            {
                Dictionary<uint, DifficultStoryState> stories =
                    accountState.Get<Dictionary<uint, DifficultStoryState>>(uid, "difficult.stories") ?? [];
                uint storyId = TableFields.U(row, 1);
                DifficultStoryState current = stories.GetValueOrDefault(storyId) ?? new DifficultStoryState();
                uint previousStoryId = TableFields.U(row, 3);
                uint requiredRaids = TableFields.U(row, 9);
                if (current.Unlocked || previousStoryId == 0)
                {
                    current.Unlocked = true;
                    stories[storyId] = current;
                    accountState.Set(uid, "difficult.stories", stories);
                    connection.Send(new SC_DifficultUnlockStory { StoryId = storyId });
                    return;
                }

                // the previous stage must be cleared (by its battle or a sweep); column 9 is not a sweep count (A-208)
                Dictionary<uint, uint> raids = accountState.Get<Dictionary<uint, uint>>(uid, "difficult.story_raids") ?? [];
                if (!DifficultStoryClues.Cleared(accountState, uid).Contains(previousStoryId) && raids.GetValueOrDefault(previousStoryId) == 0)
                {
                    connection.Refuse(MsgId.MsgCsDifficultUnlockStory, HNINDAOJCKP.StageNotFinish);
                    return;
                }

                // the clue is an ordinary bag item (8119...), not a resource: ItemUse spends either kind (A-208)
                Dictionary<uint, int> cost = TableFields.Map(row, 11).ToDictionary(pair => pair.Key, pair => checked((int)pair.Value));
                List<IMessage> messages = [];
                if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
                {
                    connection.Refuse(MsgId.MsgCsDifficultUnlockStory, HNINDAOJCKP.ItemNotEnough);
                    return;
                }

                current.Unlocked = true;
                stories[storyId] = current;
                accountState.Set(uid, "difficult.stories", stories);
                messages.Add(new SC_DifficultUnlockStory { StoryId = storyId });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        internal SimCombatDifficult BuildState(
            Dictionary<uint, DifficultStoryState> stories,
            Dictionary<uint, uint> chapterRaids,
            Dictionary<uint, uint> storyRaids,
            Dictionary<string, HashSet<uint>>? chapterClaims = null)
        {
            var state = new SimCombatDifficult();
            var storyRows = DifficultyStories().ToDictionary(row => TableFields.U(row, 1));
            foreach (IMessage relation in DifficultyStoryRelations())
            {
                uint chapterId = TableFields.U(relation, 1);
                IReadOnlyList<uint> storyIds = TableFields.List(relation, 2);
                uint unlockedStage = 0;
                foreach (uint storyId in storyIds)
                {
                    if (!storyRows.TryGetValue(storyId, out IMessage? row))
                        continue;
                    DifficultStoryState saved = stories.GetValueOrDefault(storyId) ?? new DifficultStoryState();
                    if (saved.Unlocked || TableFields.U(row, 3) == 0)
                        unlockedStage = Math.Max(unlockedStage, storyId);
                    if (storyRaids.TryGetValue(storyId, out uint raidCount))
                        state.RaidTime[storyId] = raidCount;
                }

                string chapterKey = chapterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                IEnumerable<uint> claimedRewards = stories.Where(pair => storyIds.Contains(pair.Key) && pair.Value.RewardClaimed)
                    .Select(pair => pair.Key)
                    .Concat(chapterClaims?.GetValueOrDefault(chapterKey) ?? [])
                    .Distinct();
                state.Info[chapterId] = new SimcombatDifficultInfo
                {
                    UnlockStage = unlockedStage,
                    DifficultRewards = { claimedRewards },
                };
                if (chapterRaids.TryGetValue(chapterId, out uint chapterCount))
                    state.HCHEDDOPGLL[chapterId] = chapterCount;
            }

            state.TotalRaid = chapterRaids.Values.Concat(storyRaids.Values)
                .Aggregate(0u, (sum, count) => checked(sum + count));
            return state;
        }

        private IEnumerable<IMessage> DifficultyStories() => GetTableMessages("DifficultyStoryData");

        private IEnumerable<IMessage> DifficultyStoryRelations() => GetTableMessages("DifficultyStoryByChapterData");

        private IEnumerable<IMessage> GetTableMessages(string tableTypeName)
        {
            Type rowType = typeof(ChapterData).Assembly.GetType($"Griffin.Common.Table.{tableTypeName}", throwOnError: true)!;
            MethodInfo getTable = typeof(ITableService).GetMethod(nameof(ITableService.GetTable))!
                .MakeGenericMethod(rowType);
            object rows = getTable.Invoke(tableService, [false])!;
            return ((IEnumerable)rows).Cast<IMessage>();
        }

        public sealed class DifficultStoryState
        {
            public bool Unlocked { get; set; }
            public bool RewardClaimed { get; set; }
        }
    }
}
