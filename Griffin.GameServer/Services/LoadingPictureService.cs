using Griffin.Common.Proto;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The loading-screen gallery (<c>LoadingPicData</c>). The client shows a picture as unlocked when its id is a key of the
    /// LoadingPicture quest counter (value: reward taken, always false in the official capture; progress map: unlock time).
    /// Login used to send a fixed list of five pictures captured from another account, so the gallery said "locked" even
    /// after clearing the stage or levelling the character it names. The conditions come from the description text of each
    /// picture (see <c>scripts/gen-loadingpics.py</c>): stage cleared, character level, commander level.
    /// </summary>
    public interface ILoadingPictureService : IGameService
    {
        /// <summary>Ids of the pictures the account has unlocked.</summary>
        IReadOnlySet<uint> Unlocked(uint uid);

        /// <summary>The LoadingPicture counter for <c>SC_QuestCount</c>/<c>SC_CountSection</c> at login.</summary>
        CommonQuestCounters Counters(uint uid);

        /// <summary>The counter with the pictures unlocked now that were not in <paramref name="before"/>; null when there is none.</summary>
        CommonQuestCounters? NewlyUnlocked(uint uid, IReadOnlySet<uint> before);
    }

    public sealed class LoadingPictureService(IAccountStateService accountState, IGunService gunService) : ILoadingPictureService
    {
        private const string UnlockTimesKey = "loadingpic.unlocked";

        /// <summary>Same commander level the achievements use (<c>SC_Login</c> sends 60).</summary>
        private const uint AccountLevel = 60;

        public IReadOnlySet<uint> Unlocked(uint uid)
        {
            HashSet<uint> done = [];
            foreach ((uint id, uint level) in LoadingPictureConditions.Levels)
                if (AccountLevel >= level)
                    done.Add(id);

            Dictionary<uint, int> owned = gunService.GetPlayerGuns(uid).GroupBy(g => g.GunId).ToDictionary(g => g.Key, g => g.Max(x => x.Level));
            foreach ((uint id, uint gun, uint level) in LoadingPictureConditions.GunLevels)
                if (owned.TryGetValue(gun, out int have) && have >= level)
                    done.Add(id);

            HashSet<uint> cleared = AchievementService.ClearedStages(accountState.Get<Dictionary<string, StageRecord>>(uid, "stage.records") ?? []);
            foreach ((uint id, uint[] stages) in LoadingPictureConditions.Stages)
                if (stages.Any(cleared.Contains))
                    done.Add(id);

            return done;
        }

        /// <summary>Unlock time per picture: the first time the server saw it unlocked, kept so it does not change at every login.</summary>
        private Dictionary<uint, long> Times(uint uid, IEnumerable<uint> unlocked)
        {
            lock (AccountLock.For(uid))
            {
                Dictionary<uint, long> times = accountState.Get<Dictionary<uint, long>>(uid, UnlockTimesKey) ?? [];
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool changed = false;
                foreach (uint id in unlocked)
                    changed |= times.TryAdd(id, now);
                if (changed)
                    accountState.Set(uid, UnlockTimesKey, times);
                return times;
            }
        }

        private CommonQuestCounters Build(uint uid, IEnumerable<uint> ids)
        {
            List<uint> list = ids.ToList();
            Dictionary<uint, long> times = Times(uid, list);
            CommonQuestCounters counters = new() { Type = CommonQuestCounters.Types.Type.LoadingPicture };
            foreach (uint id in list)
            {
                counters.Rewards[id] = false;
                counters.JGOJBNHMJJH[id] = times[id];
            }

            return counters;
        }

        public CommonQuestCounters Counters(uint uid) => Build(uid, Unlocked(uid));

        public CommonQuestCounters? NewlyUnlocked(uint uid, IReadOnlySet<uint> before)
        {
            List<uint> added = Unlocked(uid).Where(id => !before.Contains(id)).ToList();
            return added.Count == 0 ? null : Build(uid, added);
        }
    }
}
