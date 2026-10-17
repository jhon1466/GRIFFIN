using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The achievements the client uses as UNLOCK CONDITIONS. <c>ChapterData</c> column 16 (and about sixty other places of the client)
    /// lists achievement ids; the client asks its Quest counters of type Achievement whether the id is complete, i.e. whether the
    /// id is a key of the <c>rewards</c> map (value = reward already taken). With no achievements reported, every chapter that
    /// needs the clearing of the previous one (chapter 2, the hard chapters...) and every past-activity story stayed locked
    /// ("未解锁") although the stage the condition names was cleared. ASSUMPTION A-44: only the conditions the server can know
    /// are reported (commander level; stage cleared, see <c>tools/gen-achievements.py</c>); the rest (gun levels, logins...) has no
    /// progress here and stays incomplete.
    /// </summary>
    public interface IAchievementService : IGameService
    {
        /// <summary>The achievements of the account that are complete: id -> reward taken (true also when there is no reward).</summary>
        IReadOnlyDictionary<uint, bool> Completed(uint uid);

        /// <summary>The Achievement counters (completed ids and their progress) the client expects in <c>SC_QuestCount</c>/<c>SC_CountSection</c>.</summary>
        CommonQuestCounters Counters(uint uid);

        /// <summary>The counters of the achievements complete now that were not in <paramref name="before"/>; null when there is none.</summary>
        CommonQuestCounters? NewlyCompleted(uint uid, IReadOnlyDictionary<uint, bool> before);

        /// <summary>Takes the reward of every complete, not yet taken achievement of <paramref name="ids"/>; returns the ids taken.</summary>
        List<uint> Claim(uint uid, IEnumerable<uint> ids, List<IMessage> messages);
    }

    public sealed class AchievementService(ITableService tableService, IAccountStateService accountState, IRewardService rewardService, IGunService gunService) : IAchievementService
    {
        private const string ClaimedKey = "achievement.claimed";

        /// <summary>The level of the (only) account this server offers: <c>SC_Login</c> sends 60.</summary>
        private const uint AccountLevel = 60;

        private Dictionary<uint, IReadOnlyList<(uint Item, uint Count)>>? rewards;

        private Dictionary<uint, IReadOnlyList<(uint Item, uint Count)>> Rewards()
        {
            if (rewards is not null)
                return rewards;

            Dictionary<uint, IReadOnlyList<(uint, uint)>> map = [];
            foreach (AchievementDetailData row in tableService.GetTable<AchievementDetailData>())
            {
                IReadOnlyList<(uint, uint)> reward = TableFields.Map(row, 9).Where(p => p.Value > 0).ToList();
                if (reward.Count > 0)
                    map[row.Id] = reward;
            }

            return rewards = map;
        }

        private static HashSet<uint> ClearedStages(Dictionary<string, StageRecord> records)
        {
            HashSet<uint> cleared = [];
            foreach ((string key, StageRecord record) in records)
                if (record.FirstPassTime != 0 && uint.TryParse(key, out uint id))
                    cleared.Add(id);
            foreach ((uint stageId, _, _) in Handlers.StageHandler.ClearedStages)
                cleared.Add(stageId);
            return cleared;
        }

        private Dictionary<uint, uint> CompleteProgress(uint uid)
        {
            // id -> progress shown by the client (the commander level, or 1 stage)
            Dictionary<uint, uint> done = [];
            foreach ((uint id, uint level) in AchievementConditions.Levels)
                if (AccountLevel >= level)
                    done[id] = AccountLevel;

            // "人形升至N级": the talent tree (Mind Spiral) of a character asks its level ladder; an owned character completes every step up to its level
            Dictionary<uint, int> owned = gunService.GetPlayerGuns(uid).GroupBy(g => g.GunId).ToDictionary(g => g.Key, g => g.Max(x => x.Level));
            foreach ((uint id, uint gun, uint level) in AchievementConditions.GunLevels)
                if (owned.TryGetValue(gun, out int have) && have >= level)
                    done[id] = (uint)have;

            HashSet<uint> cleared = ClearedStages(accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? []);
            foreach ((uint id, uint[] stages) in AchievementConditions.Stages)
                if (stages.Any(cleared.Contains))
                    done[id] = 1;

            return done;
        }

        public IReadOnlyDictionary<uint, bool> Completed(uint uid)
        {
            HashSet<uint> claimed = accountState.Get<HashSet<uint>>(uid, ClaimedKey) ?? [];
            Dictionary<uint, IReadOnlyList<(uint Item, uint Count)>> reward = Rewards();
            return CompleteProgress(uid).Keys.ToDictionary(id => id, id => claimed.Contains(id) || !reward.ContainsKey(id));
        }

        private static CommonQuestCounters Build(IEnumerable<KeyValuePair<uint, bool>> completed, IReadOnlyDictionary<uint, uint> progress)
        {
            CommonQuestCounters counters = new() { Type = CommonQuestCounters.Types.Type.Achievement };
            foreach ((uint id, bool taken) in completed)
            {
                counters.Rewards[id] = taken;
                counters.JGOJBNHMJJH[id] = progress.GetValueOrDefault(id, 1u);
            }

            return counters;
        }

        public CommonQuestCounters Counters(uint uid) => Build(Completed(uid), CompleteProgress(uid));

        public CommonQuestCounters? NewlyCompleted(uint uid, IReadOnlyDictionary<uint, bool> before)
        {
            IReadOnlyDictionary<uint, bool> now = Completed(uid);
            List<KeyValuePair<uint, bool>> added = now.Where(p => !before.ContainsKey(p.Key)).ToList();
            return added.Count == 0 ? null : Build(added, CompleteProgress(uid));
        }

        public List<uint> Claim(uint uid, IEnumerable<uint> ids, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                HashSet<uint> claimed = accountState.Get<HashSet<uint>>(uid, ClaimedKey) ?? [];
                IReadOnlyDictionary<uint, bool> completed = Completed(uid);
                Dictionary<uint, IReadOnlyList<(uint Item, uint Count)>> reward = Rewards();
                List<uint> taken = [];
                foreach (uint id in ids.Distinct())
                {
                    if (!completed.TryGetValue(id, out bool already) || already || !reward.TryGetValue(id, out IReadOnlyList<(uint Item, uint Count)>? items))
                        continue;

                    foreach ((uint item, uint count) in items)
                        rewardService.Grant(uid, item, checked((int)count), messages);
                    claimed.Add(id);
                    taken.Add(id);
                }

                if (taken.Count > 0)
                    accountState.Set(uid, ClaimedKey, claimed);
                return taken;
            }
        }
    }
}
