using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    public sealed class ChapterHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IRewardService rewardService) : ChapterHandlerBase
    {
        private const string ClaimedStateKey = "chapter.rewards.claimed";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> AccountLocks = new();

        public override void HandleChapters(CS_Chapters request, Connection connection)
        {
            var chapters = new SC_Chapters();
            var chapterIds = tableService.GetTable<ChapterData>().Select(row => row.Id).ToHashSet();
            var storiesByChapter = tableService.GetTable<StoryByChapterData>()
                .Where(row => chapterIds.Contains(row.Chapter))
                .GroupBy(row => row.Chapter)
                .ToDictionary(group => group.Key, group => group.SelectMany(row => row.Id).Distinct().ToArray());

            foreach (ChapterData data in tableService.GetTable<ChapterData>().OrderBy(row => row.Id))
            {
                uint chapterType = TableFields.U(data, 6);
                var chapter = new Chapter
                {
                    Id = data.Id,
                    Stories = { storiesByChapter.GetValueOrDefault(data.Id, Array.Empty<uint>()) },
                    Rewards = { data.ChapterRewardValue },
                    Main = chapterType == 0 ? 1u : 0u,
                };
                chapters.Chapters[data.Id] = chapter;
            }

            connection.Send(0, chapters);
        }

        public override void HandleChapterTakeReward(CS_ChapterTakeReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            ChapterData? chapter = tableService.GetTable<ChapterData>().FirstOrDefault(row => row.Id == request.ChapterId);
            if (chapter is null)
            {
                connection.Send(new SC_ChapterTakeReward { ChapterId = request.ChapterId });
                return;
            }

            uint uid = connection.Account.Uid;
            lock (AccountLocks.GetOrAdd(uid, static _ => new object()))
            {
                Dictionary<string, HashSet<uint>> claimed = accountState.Get<Dictionary<string, HashSet<uint>>>(uid, ClaimedStateKey) ?? [];
                string chapterKey = chapter.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!claimed.TryGetValue(chapterKey, out HashSet<uint>? chapterClaims))
                    claimed[chapterKey] = chapterClaims = [];

                Dictionary<string, StageRecord> records = accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? [];
                // Records saved before the bitmask fix hold a count: rebuild the masks from the objectives actually reported.
                var metChallenges = accountState.Get<Dictionary<string, HashSet<uint>>>(uid, "stage.challenge_ids") ?? [];
                Dictionary<uint, StageData> stagesById = tableService.GetTable<StageData>().ToDictionary(s => s.Id);
                foreach ((string key, StageRecord saved) in records.ToArray())
                {
                    if (uint.TryParse(key, out uint savedStage) && stagesById.TryGetValue(savedStage, out StageData? savedData))
                    {
                        saved.CompleteChallenges = StageResultHandler.ChallengeMask(savedData, metChallenges.GetValueOrDefault(key) ?? []);
                    }
                }
                uint progress = CalculateProgress(
                    chapter,
                    tableService.GetTable<StoryByChapterData>(),
                    tableService.GetTable<StageData>(),
                    records);

                List<ChapterRewardClass> granted = [];
                // The client treats RewardId in SC_ChapterTakeReward as authoritative claimed state.
                // Echo already-claimed IDs for requested tiers so an account that claimed before a
                // reconnect (or before this server version) can resynchronize its UI without receiving
                // the items a second time.
                List<ChapterRewardClass> confirmedClaimed = [];
                List<IMessage> messages = [];
                string[] rewardTiers = (chapter.ChapterReward ?? string.Empty).Split('|', StringSplitOptions.TrimEntries);
                ChapterRewardClass[] requested = request.RewardId.Distinct().ToArray();
                foreach (ChapterRewardClass rewardClass in requested)
                {
                    int tier = rewardClass switch
                    {
                        ChapterRewardClass.RewardFirst => 0,
                        ChapterRewardClass.RewardSecond => 1,
                        ChapterRewardClass.RewardThird => 2,
                        _ => -1,
                    };
                    if (tier < 0 || tier >= chapter.ChapterRewardValue.Count || tier >= rewardTiers.Length)
                        continue;

                    uint threshold = chapter.ChapterRewardValue[tier];
                    uint classId = (uint)rewardClass;
                    if (chapterClaims.Contains(classId))
                    {
                        confirmedClaimed.Add(rewardClass);
                        continue;
                    }

                    if (progress < threshold)
                        continue;

                    bool accepted = false;
                    foreach ((uint itemId, int count) in ParseTierReward(rewardTiers[tier]))
                        accepted |= rewardService.Grant(uid, itemId, count, messages).Granted;

                    if (!accepted)
                    {
                        Log.Warning("Chapter reward {ChapterId}/{RewardClass} has no grantable configured items.", chapter.Id, rewardClass);
                        continue;
                    }

                    chapterClaims.Add(classId);
                    granted.Add(rewardClass);
                    confirmedClaimed.Add(rewardClass);
                }

                if (granted.Count > 0)
                    accountState.Set(uid, ClaimedStateKey, claimed);

                Log.Information(
                    "Chapter reward claim account {Uid}, chapter {ChapterId}: progress {Progress}, requested [{Requested}], newly granted [{Granted}], confirmed claimed [{Claimed}].",
                    uid,
                    chapter.Id,
                    progress,
                    string.Join(",", requested),
                    string.Join(",", granted),
                    string.Join(",", confirmedClaimed));

                messages.Add(new SC_ChapterTakeReward
                {
                    ChapterId = chapter.Id,
                    RewardId = { confirmedClaimed },
                });
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        /// <summary>
        /// Totals challenge stars for a public chapter. ChapterData ids contain the UI chapter number (for example 1),
        /// while StageData.chapter contains story ids (101, 102, ...); StoryByChapterData links those namespaces.
        /// Include the same opening-stage records that StageHandler reports to the client when the account has no saved result.
        /// </summary>
        public static uint CalculateProgress(
            ChapterData chapter,
            IEnumerable<StoryByChapterData> storyRelations,
            IEnumerable<StageData> stages,
            IReadOnlyDictionary<string, StageRecord> savedRecords)
        {
            HashSet<uint> storyIds = storyRelations
                .Where(relation => relation.Chapter == chapter.Id)
                .SelectMany(relation => relation.Id)
                .ToHashSet();
            if (storyIds.Count == 0)
            {
                // Activity chapters (ChapterData type 3, ids 3001...) have no row of their own: their stages are listed under the chapter
                // of column 7 (4001...), and that row lists the STAGE ids (30801...), not story groups. ASSUMPTION A-45.
                uint listedUnder = TableFields.U(chapter, 7);
                storyIds = storyRelations.Where(relation => relation.Chapter == listedUnder).SelectMany(relation => relation.Id).ToHashSet();
            }
            Dictionary<uint, uint> seededChallenges = StageHandler.ClearedStages
                .ToDictionary(entry => entry.StageId, entry => entry.Challenges);

            uint progress = 0;
            foreach (StageData stage in stages.Where(stage => storyIds.Contains(stage.Chapter) || storyIds.Contains(stage.Id)))
            {
                uint mask;
                string stageKey = stage.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (savedRecords.TryGetValue(stageKey, out StageRecord? saved))
                    mask = saved.CompleteChallenges;
                else if (seededChallenges.TryGetValue(stage.Id, out uint seeded))
                    mask = seeded;
                else
                    continue;

                // complete_challenges is a bitmask over StageData.challenge_list: the stars are its set bits.
                uint valid = stage.ChallengeList.Count >= 32 ? uint.MaxValue : (1u << stage.ChallengeList.Count) - 1;
                progress = checked(progress + (uint)System.Numerics.BitOperations.PopCount(mask & valid));
            }

            return progress;
        }

        private static IEnumerable<(uint ItemId, int Count)> ParseTierReward(string value)
        {
            foreach (string entry in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] pair = entry.Split(':', StringSplitOptions.TrimEntries);
                if (pair.Length == 2 && uint.TryParse(pair[0], out uint itemId) && int.TryParse(pair[1], out int count)
                    && itemId > 0 && count > 0)
                    yield return (itemId, count);
            }
        }
    }
}
