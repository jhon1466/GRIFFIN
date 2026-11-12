using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The "Alva lottery" activity (ActivityListData type 27, e.g. 61805, client data manager NDFOLJHHFBD, UI Activity/Lottery), as read
    /// from client 4.0.5142:
    /// - CS_ActivityLotteryInfo is answered by SC_ActivityLotteryInfo, a map activity id -> { score }; the client keeps the last entry as
    ///   the current activity and its score.
    /// - LotteryMainData (1 = activity id, 2 = phase group, 3 = task group); LotteryPhaseByGroupData lists the phases of a group;
    ///   LotteryPhaseData (1 = id, 2 = group, 3 = phase number, 4 = score needed).
    /// - A phase (a lottery "ticket") is taken with CS_CommonQuestTakePhaseReward of type Lottery (54); the client marks it in the
    ///   phase_rewards map of the Lottery counters (BOEEGCDCJEG.LCOPHBEMEBJ) and only asks while score >= phase score and not taken.
    ///   The phase has no item reward in the tables: the ticket code is computed by the client from uid/activity/phase (KDPOMDMDFPB).
    /// - LotteryTaskData (4 = condition type, 5 = count, 6/7 = args, 12 = score shown as "+N") are watched by the client in player
    ///   counter system 67 (CounterSystemLottery) and have no claim button: the official server adds their score by itself.
    /// </summary>
    public interface IActivityLotteryService : IGameService
    {
        SC_ActivityLotteryInfo Info(uint uid);

        /// <summary>The Lottery counters (phase_rewards = taken tickets) for SC_CountSection / SC_CommonQuestCountUpdate.</summary>
        CommonQuestCounters Counters(uint uid);

        /// <summary>Takes the requested phases that are configured, reached and not taken yet; returns the ones taken.</summary>
        List<uint> ClaimPhases(uint uid, IEnumerable<uint> phaseIds);

        /// <summary>Adds score to an activity (hook for the task progress, see "left undone" in Info).</summary>
        void AddScore(uint uid, uint activityId, uint amount);
    }

    public sealed class ActivityLotteryService(ITableService tables, IAccountStateService accountState) : IActivityLotteryService
    {
        public sealed class State
        {
            public Dictionary<uint, uint> Scores { get; set; } = [];
            public HashSet<uint> TakenPhases { get; set; } = [];
        }

        private const string StateKey = "activity.lottery";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> Locks = new();

        private State Load(uint uid) => accountState.Get<State>(uid, StateKey) ?? new State();

        private IEnumerable<Google.Protobuf.IMessage> MainRows() =>
            tables.GetTable<LotteryMainData>().Where(r => TableFields.U(r, 1) != 0);

        /// <summary>Phase id -> score needed, of every phase listed by a LotteryMainData row.</summary>
        private Dictionary<uint, (uint Activity, uint Score)> Phases()
        {
            Dictionary<uint, (uint, uint)> result = [];
            foreach (var main in MainRows())
            {
                uint activity = TableFields.U(main, 1), group = TableFields.U(main, 2);
                foreach (var phase in tables.GetTable<LotteryPhaseData>().Where(p => TableFields.U(p, 2) == group && TableFields.U(p, 1) != 0))
                    result[TableFields.U(phase, 1)] = (activity, TableFields.U(phase, 4));
            }
            return result;
        }

        public SC_ActivityLotteryInfo Info(uint uid)
        {
            State state = Load(uid);
            SC_ActivityLotteryInfo reply = new();
            // ASSUMPTION: the task score is not counted yet (no server tracks the LotteryTaskData conditions / counter system 67), so the
            // score is only what AddScore stored; one entry per LotteryMainData activity.
            foreach (var main in MainRows())
            {
                uint activity = TableFields.U(main, 1);
                reply.BNKEKDKPCIE[activity] = new MONCDMEHPGI { Score = state.Scores.GetValueOrDefault(activity) };
            }
            return reply;
        }

        public CommonQuestCounters Counters(uint uid)
        {
            State state = Load(uid);
            CommonQuestCounters counters = new() { Type = CommonQuestCounters.Types.Type.Lottery };
            foreach (uint phase in Phases().Keys)
                counters.PhaseRewards[phase] = state.TakenPhases.Contains(phase);
            return counters;
        }

        public List<uint> ClaimPhases(uint uid, IEnumerable<uint> phaseIds)
        {
            lock (Locks.GetOrAdd(uid, static _ => new object()))
            {
                State state = Load(uid);
                var phases = Phases();
                List<uint> taken = [];
                // ASSUMPTION: the client also hides the claim after the activity's claim window (ActivityListData start + hours); the
                // server only checks the score and that the ticket was not taken, as the tables give no other condition.
                foreach (uint id in phaseIds.Distinct())
                {
                    if (!phases.TryGetValue(id, out var phase) || state.TakenPhases.Contains(id))
                        continue;
                    if (state.Scores.GetValueOrDefault(phase.Activity) < phase.Score)
                        continue;
                    state.TakenPhases.Add(id);
                    taken.Add(id);
                }
                if (taken.Count > 0)
                    accountState.Set(uid, StateKey, state);
                return taken;
            }
        }

        public void AddScore(uint uid, uint activityId, uint amount)
        {
            lock (Locks.GetOrAdd(uid, static _ => new object()))
            {
                State state = Load(uid);
                state.Scores[activityId] = state.Scores.GetValueOrDefault(activityId) + amount;
                accountState.Set(uid, StateKey, state);
            }
        }
    }
}
