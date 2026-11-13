using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The task lists of two event modes that live in the common quest domain (client 4.0.5142):
    /// <list type="bullet">
    /// <item>Free skin (CommonQuestCounters type ActivityFreeSkin 41, player counter system 52; client PDNFMNOFPPL): EventFreeSkinMainData
    /// column 3 is the task list, EventFreeSkinTasklistData rows (1 = task, 2 = task list, 3 = kind, 6 = condition class, 7 = amount,
    /// 10 = reward item -> count, 11 = link) are the tasks; the reward is the progress item of the activity.</item>
    /// <item>Ruby shop (type RubyShop 34, player counter system 44; client JMNHMKLGPLN / LLDICEKPMLG): RubyTaskData rows (1 = task,
    /// 2 = activity, 3 = reward item -> count, 5 = next task of the chain, 6 = condition class, 7 = amount, 8 = condition arg 0). The client
    /// shows the first unclaimed task of each chain and claims the claimable run of the chain in one CS_CommonQuestTakeReward; on success it adds
    /// the claimed currency (RubyMainData column 2) to the shop state field 5, which the server keeps in <c>RubyShopHandler.State.TaskPoints</c>.</item>
    /// </list>
    /// The client reads a task's state from CommonQuestCounters.rewards (BOEEGCDCJEG.GGHHOBHKMND): absent = not finished, false = claimable,
    /// true = claimed; and its progress from the player counter that SC_QuestCount maps to the type (BOEEGCDCJEG.KDGIMOGCBPG), keyed by task id.
    /// </summary>
    public interface IEventTaskService : IGameService
    {
        /// <summary>The counters of both types for SC_QuestCount.</summary>
        IEnumerable<CommonQuestCounters> Counters(uint uid);

        /// <summary>The task progress as player counters (systems 52 and 44).</summary>
        IReadOnlyList<SC_PlayerStatusCounterSync> CounterSyncs(uint uid);

        bool IsTaskType(CommonQuestCounters.Types.Type type);

        /// <summary>Claims finished, unclaimed tasks of <paramref name="type"/>; returns the claimed ids.</summary>
        List<uint> Claim(uint uid, CommonQuestCounters.Types.Type type, IEnumerable<uint> ids, List<IMessage> messages);

        /// <summary>Counts a game event (same condition classes and links as the daily quests).</summary>
        void OnCondition(uint uid, uint conditionClass, string? link, uint amount);
    }

    public sealed class EventTaskState
    {
        public Dictionary<uint, uint> FreeSkin { get; set; } = [];
        public HashSet<uint> FreeSkinClaimed { get; set; } = [];
        public Dictionary<uint, uint> Ruby { get; set; } = [];
        public HashSet<uint> RubyClaimed { get; set; } = [];
        public string Day { get; set; } = string.Empty;
    }

    public sealed class EventTaskService(
        ITableService tableService,
        IAccountStateService accountState,
        IRewardService rewardService,
        ConnectionManager? connections = null) : IEventTaskService
    {
        private const string StateKey = "event.tasks";
        public const uint FreeSkinCounterSystem = 52;
        public const uint RubyCounterSystem = 44;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> Locks = new();

        /// <summary>
        /// The (condition class, link) pairs the server reports through IDailyQuestProgressService.AddCondition. A task whose class is not
        /// here belongs to an action the server cannot observe. ASSUMPTION (same policy as the daily quests and the battle pass): such a task
        /// counts as done, so the progress item / currency can still be earned.
        /// </summary>
        private static readonly (uint Class, string Link)[] Fed =
        [
            (400, ""), (750, ""), (2, "1000"), (201, "3800"), (201, "21001"), (209, "1098"), (1303, "4015"), (1504, "41001"),
            (3621, "32007"), (4110, "32017"),
        ];

        private static bool Observable(uint conditionClass, string link) =>
            Fed.Any(f => f.Class == conditionClass && (link.Length == 0 || f.Link.Length == 0 || f.Link == link));

        public bool IsTaskType(CommonQuestCounters.Types.Type type) =>
            type is CommonQuestCounters.Types.Type.ActivityFreeSkin or CommonQuestCounters.Types.Type.RubyShop;

        // ---- table access ----

        private record struct Task(uint Id, uint Class, string Link, uint Need, bool Daily, IReadOnlyList<(uint Key, uint Value)> Reward, uint Activity);

        private IEnumerable<Task> FreeSkinTasks() =>
            tableService.GetTable<EventFreeSkinTasklistData>().Cast<IMessage>()
                .Where(r => TableFields.U(r, 1) != 0)
                // ASSUMPTION: kind 1 (the login / supply / stamina rows that repeat the daily quests) resets every game day; kind 2 is one-time.
                .Select(r => new Task(TableFields.U(r, 1), TableFields.U(r, 6), TableFields.S(r, 11), Math.Max(1, TableFields.U(r, 7)),
                    TableFields.U(r, 3) == 1, TableFields.Map(r, 10), 0));

        // ASSUMPTION: ruby tasks match by condition class only (their arg 0 is not the stage-type link the daily quests use).
        private IEnumerable<Task> RubyTasks() =>
            tableService.GetTable<RubyTaskData>().Cast<IMessage>()
                .Where(r => TableFields.U(r, 1) != 0)
                .Select(r => new Task(TableFields.U(r, 1), TableFields.U(r, 6), string.Empty, Math.Max(1, TableFields.U(r, 7)),
                    false, TableFields.Map(r, 3), TableFields.U(r, 2)));

        // ---- state ----

        private EventTaskState Load(uint uid)
        {
            EventTaskState state = accountState.Get<EventTaskState>(uid, StateKey) ?? new EventTaskState();
            string today = GameDay.Key();
            if (state.Day != today)
            {
                foreach (Task task in FreeSkinTasks().Where(t => t.Daily))
                {
                    state.FreeSkin.Remove(task.Id);
                    state.FreeSkinClaimed.Remove(task.Id);
                }
                state.Day = today;
            }
            return state;
        }

        private void Save(uint uid, EventTaskState state) => accountState.Set(uid, StateKey, state);

        private static uint Progress(Dictionary<uint, uint> progress, Task task) =>
            Observable(task.Class, task.Link) ? Math.Min(task.Need, progress.GetValueOrDefault(task.Id)) : task.Need;

        private CommonQuestCounters Build(CommonQuestCounters.Types.Type type, IEnumerable<Task> tasks, Dictionary<uint, uint> progress, HashSet<uint> claimed)
        {
            CommonQuestCounters counters = new() { Type = type };
            foreach (Task task in tasks)
            {
                uint value = Progress(progress, task);
                counters.JGOJBNHMJJH[task.Id] = value;
                if (claimed.Contains(task.Id))
                    counters.Rewards[task.Id] = true;
                else if (value >= task.Need)
                    counters.Rewards[task.Id] = false;
            }
            return counters;
        }

        public IEnumerable<CommonQuestCounters> Counters(uint uid)
        {
            lock (Locks.GetOrAdd(uid, static _ => new object()))
            {
                EventTaskState state = Load(uid);
                Save(uid, state);
                return
                [
                    Build(CommonQuestCounters.Types.Type.ActivityFreeSkin, FreeSkinTasks(), state.FreeSkin, state.FreeSkinClaimed),
                    Build(CommonQuestCounters.Types.Type.RubyShop, RubyTasks(), state.Ruby, state.RubyClaimed),
                ];
            }
        }

        private static List<SC_PlayerStatusCounterSync> Syncs(uint system, IEnumerable<(uint Id, uint Value)> values) =>
            values.GroupBy(v => v.Value).Select(group =>
            {
                SC_PlayerStatusCounterSync sync = new() { Num = group.Key };
                foreach ((uint id, _) in group)
                    sync.Watchers.Add(new PELLFAAPKOL { SystemId = system, UniqueId = id });
                return sync;
            }).ToList();

        public IReadOnlyList<SC_PlayerStatusCounterSync> CounterSyncs(uint uid)
        {
            EventTaskState state;
            lock (Locks.GetOrAdd(uid, static _ => new object()))
                state = Load(uid);
            return Syncs(FreeSkinCounterSystem, FreeSkinTasks().Select(t => (t.Id, Progress(state.FreeSkin, t))))
                .Concat(Syncs(RubyCounterSystem, RubyTasks().Select(t => (t.Id, Progress(state.Ruby, t))))).ToList();
        }

        public List<uint> Claim(uint uid, CommonQuestCounters.Types.Type type, IEnumerable<uint> ids, List<IMessage> messages)
        {
            bool freeSkin = type == CommonQuestCounters.Types.Type.ActivityFreeSkin;
            List<uint> claimed = [];
            lock (Locks.GetOrAdd(uid, static _ => new object()))
            {
                EventTaskState state = Load(uid);
                Dictionary<uint, uint> progress = freeSkin ? state.FreeSkin : state.Ruby;
                HashSet<uint> done = freeSkin ? state.FreeSkinClaimed : state.RubyClaimed;
                Dictionary<uint, Task> tasks = (freeSkin ? FreeSkinTasks() : RubyTasks()).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
                Dictionary<uint, uint> rubyPoints = [];
                foreach (uint id in ids.Distinct())
                {
                    if (!tasks.TryGetValue(id, out Task task) || done.Contains(id) || Progress(progress, task) < task.Need)
                        continue;
                    foreach ((uint item, uint count) in task.Reward)
                    {
                        if (count == 0)
                            continue;
                        rewardService.Grant(uid, item, (int)count, messages);
                        if (!freeSkin && RubyCurrency(task.Activity) == item)
                            rubyPoints[task.Activity] = rubyPoints.GetValueOrDefault(task.Activity) + count;
                    }
                    done.Add(id);
                    claimed.Add(id);
                }

                if (claimed.Count > 0)
                {
                    Save(uid, state);
                    // the client adds the claimed currency to the shop's task bar (JMNHMKLGPLN.ACCOPLFEOBH); keep the server copy equal
                    foreach ((uint activity, uint points) in rubyPoints)
                    {
                        string key = $"rubyshop.{activity}";
                        Handlers.RubyShopHandler.State shop = accountState.Get<Handlers.RubyShopHandler.State>(uid, key) ?? new Handlers.RubyShopHandler.State();
                        shop.TaskPoints += points;
                        accountState.Set(uid, key, shop);
                    }
                    Log.Information("Event tasks {Ids} ({Type}) of account {Uid} claimed.", claimed, type, uid);
                }
            }
            return claimed;
        }

        private uint RubyCurrency(uint activity) =>
            tableService.GetTable<RubyMainData>().Cast<IMessage>().Where(r => TableFields.U(r, 1) == activity).Select(r => TableFields.U(r, 2)).FirstOrDefault();

        public void OnCondition(uint uid, uint conditionClass, string? link, uint amount)
        {
            if (conditionClass == 0 || amount == 0)
                return;

            List<uint> changedSkin = [];
            List<uint> changedRuby = [];
            EventTaskState state;
            lock (Locks.GetOrAdd(uid, static _ => new object()))
            {
                state = Load(uid);
                Count(FreeSkinTasks(), state.FreeSkin, state.FreeSkinClaimed, changedSkin);
                Count(RubyTasks(), state.Ruby, state.RubyClaimed, changedRuby);
                if (changedSkin.Count + changedRuby.Count == 0)
                    return;
                Save(uid, state);
            }

            void Count(IEnumerable<Task> tasks, Dictionary<uint, uint> progress, HashSet<uint> claimed, List<uint> changed)
            {
                foreach (Task task in tasks)
                {
                    if (task.Class != conditionClass || claimed.Contains(task.Id)
                        || (link is not null && task.Link.Length != 0 && task.Link != link))
                        continue;
                    uint before = progress.GetValueOrDefault(task.Id);
                    uint after = (uint)Math.Min(task.Need, (ulong)before + amount);
                    if (after == before)
                        continue;
                    progress[task.Id] = after;
                    changed.Add(task.Id);
                }
            }

            Push(uid, state, changedSkin, changedRuby);
        }

        /// <summary>Sends the new progress and, for tasks that just finished, the claimable flag.</summary>
        private void Push(uint uid, EventTaskState state, List<uint> skin, List<uint> ruby)
        {
            if (connections is null)
                return;
            List<IMessage> messages = [];
            Dictionary<uint, Task> skinTasks = FreeSkinTasks().Where(t => skin.Contains(t.Id)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            Dictionary<uint, Task> rubyTasks = RubyTasks().Where(t => ruby.Contains(t.Id)).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            messages.AddRange(Syncs(FreeSkinCounterSystem, skinTasks.Values.Select(t => (t.Id, Progress(state.FreeSkin, t)))));
            messages.AddRange(Syncs(RubyCounterSystem, rubyTasks.Values.Select(t => (t.Id, Progress(state.Ruby, t)))));
            AddFinished(CommonQuestCounters.Types.Type.ActivityFreeSkin, skinTasks.Values, state.FreeSkin);
            AddFinished(CommonQuestCounters.Types.Type.RubyShop, rubyTasks.Values, state.Ruby);
            if (messages.Count == 0)
                return;
            IMessage[] array = messages.ToArray();
            foreach (Connection connection in connections.Connections.Where(c => c.Account?.Uid == uid))
                connection.SendAutoEncrypted(array);

            void AddFinished(CommonQuestCounters.Types.Type type, IEnumerable<Task> tasks, Dictionary<uint, uint> progress)
            {
                CommonQuestCounters counters = new() { Type = type };
                foreach (Task task in tasks.Where(t => Progress(progress, t) >= t.Need))
                    counters.Rewards[task.Id] = false;
                if (counters.Rewards.Count > 0)
                    messages.Add(new SC_CommonQuestCountUpdate { Counters = counters });
            }
        }
    }
}
