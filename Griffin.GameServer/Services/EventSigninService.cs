using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Serilog;

namespace Griffin.GameServer.Services
{
    public interface IEventSigninService : IGameService
    {
        SC_GetActivityCheckIn GetStatus(uint accountUid);
        bool TryClaim(uint accountUid, uint activityId, List<IMessage> messages);

        /// <summary>Today's check-in flag and the days claimed so far of one event (the delayed sign-in reads both).</summary>
        (bool DoneToday, uint ClaimedDays) Progress(uint accountUid, uint activityId);
    }

    /// <summary>
    /// Account-persistent event check-in state backed by EventSigninRewardData. The 14 activity IDs are from the
    /// existing captured GetActivityCheckIn response; rewards and day counts come from the installed client tables.
    /// </summary>
    public sealed class EventSigninService(
        ITableService tableService,
        IAccountStateService accountState,
        IRewardService rewardService) : IEventSigninService
    {
        private const string StateKey = "event.signin";
        private static readonly uint[] CapturedActivityIds = [60813, 60901, 60607, 60704, 61507, 61002, 63003,
            69996, 60504, 62103, 61604, 61910, 61705, 61108];
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> AccountLocks = new();

        public SC_GetActivityCheckIn GetStatus(uint uid)
        {
            EventSigninState state = CurrentState(uid);
            var response = new SC_GetActivityCheckIn();
            foreach (uint activityId in ActivityIds())
            {
                List<EventSigninRewardData> rewards = Rewards(activityId);
                if (rewards.Count == 0)
                    continue;

                EventSigninProgress progress = state.Activities.GetValueOrDefault(activityId) ?? new EventSigninProgress();
                uint nextDay = rewards.FirstOrDefault(row => row.Days > progress.ClaimedThrough)?.Days
                    ?? rewards.Max(row => row.Days);
                response.ActivityCheckin[activityId] = new ActivityCheckin
                {
                    CheckinDone = progress.LastClaimDate == ChinaDateKey(),
                    CheckinDay = nextDay,
                };
            }
            return response;
        }

        public bool TryClaim(uint uid, uint activityId, List<IMessage> messages)
        {
            if (Rewards(activityId).Count == 0)
                return false;

            lock (AccountLocks.GetOrAdd(uid, static _ => new object()))
            {
                EventSigninState state = CurrentState(uid);
                EventSigninProgress progress = state.Activities.GetValueOrDefault(activityId) ?? new EventSigninProgress();
                string today = ChinaDateKey();
                if (progress.LastClaimDate == today)
                    return false;

                List<EventSigninRewardData> rewards = Rewards(activityId);
                EventSigninRewardData? row = rewards.FirstOrDefault(candidate => candidate.Days > progress.ClaimedThrough);
                if (row is null)
                    return false;

                int before = messages.Count;
                bool accepted = false;
                uint count = Math.Min((uint)row.Rewards.BAALJFJDGBB.Count, (uint)row.Rewards.Value.Count);
                for (int i = 0; i < count; i++)
                {
                    GrantResult result = rewardService.Grant(uid, row.Rewards.BAALJFJDGBB[i],
                        (int)row.Rewards.Value[i], messages);
                    accepted |= result.Granted;
                }

                if (!accepted && messages.Count == before)
                {
                    Log.Warning("No valid table rewards for event sign-in activity {ActivityId}, day {Day}.", activityId, row.Days);
                    return false;
                }

                progress.ClaimedThrough = row.Days;
                progress.LastClaimDate = today;
                state.Activities[activityId] = progress;
                accountState.Set(uid, StateKey, state);
                return true;
            }
        }

        public (bool DoneToday, uint ClaimedDays) Progress(uint uid, uint activityId)
        {
            EventSigninProgress progress = CurrentState(uid).Activities.GetValueOrDefault(activityId) ?? new EventSigninProgress();
            uint claimed = (uint)Rewards(activityId).Count(row => row.Days <= progress.ClaimedThrough);
            return (progress.LastClaimDate == ChinaDateKey(), claimed);
        }

        /// <summary>
        /// Every event with sign-in rewards that is open now (ActivityListData columns 4/5, which carry the new dates of an event reopened from
        /// the GM tools), plus the ones of the captured reply. Only the captured 14 were served before, so other sign-in events never counted.
        /// </summary>
        private IEnumerable<uint> ActivityIds()
        {
            ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            HashSet<uint> withRewards = tableService.GetTable<EventSigninRewardData>().Select(row => row.ActivityId).ToHashSet();
            HashSet<uint> open = tableService.GetTable<ActivityListData>()
                .Where(row => withRewards.Contains(row.Id))
                .Where(row => (ulong)row.OpenTime <= now && (row.CloseTime == 0 || now <= (ulong)row.CloseTime))
                .Select(row => row.Id).ToHashSet();
            return open.Union(CapturedActivityIds).OrderBy(id => id);
        }

        private List<EventSigninRewardData> Rewards(uint activityId) => tableService.GetTable<EventSigninRewardData>()
            .Where(row => row.ActivityId == activityId)
            .OrderBy(row => row.Days)
            .ToList();

        private EventSigninState CurrentState(uint uid)
        {
            EventSigninState? state = accountState.Get<EventSigninState>(uid, StateKey);
            if (state is null)
                return new EventSigninState();
            return state;
        }

        private static string ChinaDateKey() => GameDay.Key();
    }

    public sealed class EventSigninState
    {
        public Dictionary<uint, EventSigninProgress> Activities { get; set; } = [];
    }

    public sealed class EventSigninProgress
    {
        public uint ClaimedThrough { get; set; }
        public string LastClaimDate { get; set; } = string.Empty;
    }
}
