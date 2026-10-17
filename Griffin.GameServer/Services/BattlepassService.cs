using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Database.Services;
using Serilog;
using GameBpTaskType = Griffin.Common.Proto.BpTaskType;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Battle pass of the current season (<see cref="SeasonId"/>, the one the client already showed in the first captures).
    /// Table semantics, read by field number (<see cref="TableFields"/>), checked against real values of client 1189260:
    /// <list type="bullet">
    /// <item><c>BpSeasonData</c> (id = season): 6 = maximum level, 7 = experience per level, 8 = maximum number of extra rewards,
    /// 9 / 10 = reward of one extra level for the base / advanced pass.</item>
    /// <item><c>BpRewardDescData</c>: 2 = level, 3 = season, 5 / 6 = base / advanced reward (item → amount).</item>
    /// <item><c>BpTaskData</c>: 2 = kind (1 daily, 2 weekly, 5 season), 12 = season.</item>
    /// <item>Experience is the item 601 ("BP经验") of the inventory: any reward that grants it (the quests of the other
    /// domain do) raises the level. The server does not invent experience.</item>
    /// </list>
    /// Assumptions (no official data): <c>Battlepass.reward[level]</c> is a bit mask (1 = base claimed, 2 = advanced claimed),
    /// the advanced pass is a stored flag (<see cref="BattlepassState.Status"/>), and tasks only change on refresh because
    /// task progress belongs to the quest domain.
    /// </summary>
    public interface IBattlepassService : IGameService
    {
        uint SeasonId { get; }

        Battlepass Build(uint uid);

        /// <summary>Claims the rewards of a level (0 = every claimable level). Returns the reward messages and the claimed levels.</summary>
        bool Claim(uint uid, uint level, BattlepassType type, List<IMessage> messages, out uint claimedLevel);

        /// <summary>Claims the extra rewards earned beyond the maximum level; returns the amount of extra rewards granted.</summary>
        int ClaimExtra(uint uid, List<IMessage> messages);

        IReadOnlyList<uint> Tasks(uint uid, GameBpTaskType type);

        bool Refresh(uint uid, GameBpTaskType type, uint index, out uint newTask, out uint refreshCount);

        void Activate(uint uid, bool advanced);

        uint LeftRewards(uint uid);

        /// <summary>Counts a game event on the battle pass tasks (same condition classes as the daily quests); a finished task pays its experience once per period.</summary>
        void OnCondition(uint uid, uint conditionClass, string? link, uint amount);
    }

    public sealed class BattlepassState
    {
        public Dictionary<uint, uint> Claimed { get; set; } = [];
        public uint Status { get; set; }
        public uint ExtraClaimed { get; set; }
        public List<uint> Daily { get; set; } = [];
        public List<uint> Weekly { get; set; } = [];
        public List<uint> Season { get; set; } = [];
        public uint DailyRefreshes { get; set; }
        public uint WeeklyRefreshes { get; set; }
        public bool Started { get; set; }
        /// <summary>Progress and completion of the tasks in the current period (daily: game day, weekly and season: game week).</summary>
        public Dictionary<uint, uint> Progress { get; set; } = [];
        public HashSet<uint> Done { get; set; } = [];
        public string DailyKey { get; set; } = string.Empty;
        public string WeeklyKey { get; set; } = string.Empty;
    }

    public sealed class BattlepassService(
        ITableService tableService,
        IAccountStateService accountState,
        IItemService itemService,
        IRewardService rewardService) : IBattlepassService
    {
        public const uint ExpItem = 601;
        private const string StateKey = "battlepass";
        private const int DailyCount = 4;
        private const int WeeklyCount = 9;
        private const int SeasonTaskCount = 6;

        public uint SeasonId => 30034;

        /// <summary>
        /// Task rules (<c>BpTaskData</c>: 5 = condition class, 6 = amount, 7 = reward item -> amount, 8 = link). The counted classes are the ones
        /// <see cref="DailyQuestProgressService"/> counts (400 login, 750 lounge, 2/1000 stamina spent). ASSUMPTION (same policy as the daily quests):
        /// a task of any other class belongs to an action the server cannot observe, so it counts as done when the pass is opened; its reward is the
        /// task's own table reward. Without this the pass had no source of experience at all.
        /// </summary>
        private static bool Observable(IMessage row) =>
            TableFields.U(row, 5) is 400 or 750 || (TableFields.U(row, 5) == 2 && TableFields.S(row, 8) == "1000");

        /// <summary>Resets the tasks of a finished period, then completes the unobservable ones. Returns the state (saved when changed).</summary>
        private BattlepassState Sync(uint uid)
        {
            BattlepassState state = Load(uid);
            bool changed = false;
            string day = GameDay.Key();
            string week = (GameDay.Number(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) / 7).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (state.DailyKey != day)
            {
                foreach (uint id in state.Daily) { state.Done.Remove(id); state.Progress.Remove(id); }
                state.DailyKey = day;
                changed = true;
            }

            if (state.WeeklyKey != week)
            {
                foreach (uint id in state.Weekly.Concat(state.Season)) { state.Done.Remove(id); state.Progress.Remove(id); }
                state.WeeklyKey = week;
                changed = true;
            }

            foreach (uint id in state.Daily.Concat(state.Weekly).Concat(state.Season))
            {
                if (state.Done.Contains(id) || tableService.GetTable<BpTaskData>().FirstOrDefault(r => r.Id == id) is not { } row || Observable(row))
                    continue;

                Complete(uid, state, id, row);
                changed = true;
            }

            if (changed)
                Save(uid, state);
            return state;
        }

        private void Complete(uint uid, BattlepassState state, uint id, IMessage row)
        {
            state.Done.Add(id);
            List<IMessage> sink = [];
            foreach ((uint item, uint count) in TableFields.Map(row, 7))
                if (count > 0)
                    rewardService.Grant(uid, item, (int)count, sink);
            Log.Information("Battle pass task {Task} of account {Uid} completed.", id, uid);
        }

        public void OnCondition(uint uid, uint conditionClass, string? link, uint amount)
        {
            if (conditionClass == 0 || amount == 0)
                return;

            BattlepassState state = Sync(uid);
            bool changed = false;
            foreach (uint id in state.Daily.Concat(state.Weekly).Concat(state.Season))
            {
                if (state.Done.Contains(id) || tableService.GetTable<BpTaskData>().FirstOrDefault(r => r.Id == id) is not { } row
                    || TableFields.U(row, 5) != conditionClass || (link is not null && TableFields.S(row, 8) != link && TableFields.S(row, 8) != string.Empty))
                    continue;

                uint need = Math.Max(1, TableFields.U(row, 6));
                uint progress = state.Progress.GetValueOrDefault(id) + amount;
                state.Progress[id] = progress;
                changed = true;
                if (progress >= need)
                    Complete(uid, state, id, row);
            }

            if (changed)
                Save(uid, state);
        }

        public Battlepass Build(uint uid)
        {
            BattlepassState state = Sync(uid);
            Battlepass pass = new()
            {
                Status = (BattlepassType)state.Status,
                Exp = Exp(uid),
                CurrId = SeasonId,
                LeftRewardNum = LeftRewards(uid),
                MaxLevelReward = Level(uid) >= MaxLevel(),
            };
            foreach ((uint level, uint mask) in state.Claimed)
                pass.Reward[level] = mask;
            pass.DailyTask.AddRange(state.Daily);
            pass.WeeklyTask.AddRange(state.Weekly);
            pass.OPJONAGNMKE.AddRange(state.Season);
            return pass;
        }

        public bool Claim(uint uid, uint level, BattlepassType type, List<IMessage> messages, out uint claimedLevel)
        {
            claimedLevel = 0;
            BattlepassState state = Load(uid);
            uint reached = Level(uid);
            bool advanced = state.Status != 0;
            bool any = false;

            foreach (IMessage row in tableService.GetTable<BpRewardDescData>().Where(r => TableFields.U(r, 3) == SeasonId).OrderBy(r => TableFields.U(r, 2)))
            {
                uint rowLevel = TableFields.U(row, 2);
                if (rowLevel > reached || (level != 0 && rowLevel != level))
                    continue;

                state.Claimed.TryGetValue(rowLevel, out uint mask);
                uint before = mask;
                if ((mask & 1) == 0 && type != BattlepassType.AdvanceOne && type != BattlepassType.AdvanceTwo)
                {
                    Grant(uid, TableFields.Map(row, 5), messages);
                    mask |= 1;
                }

                if ((mask & 2) == 0 && advanced && type != BattlepassType.Base)
                {
                    Grant(uid, TableFields.Map(row, 6), messages);
                    mask |= 2;
                }

                if (mask != before)
                {
                    state.Claimed[rowLevel] = mask;
                    claimedLevel = rowLevel;
                    any = true;
                }
            }

            if (any)
                Save(uid, state);
            return any;
        }

        public int ClaimExtra(uint uid, List<IMessage> messages)
        {
            BattlepassState state = Load(uid);
            uint available = ExtraEarned(uid);
            uint pending = available > state.ExtraClaimed ? available - state.ExtraClaimed : 0;
            if (pending == 0)
                return 0;

            IMessage? season = Season();
            IReadOnlyList<(uint Key, uint Value)> reward = season is null ? [] : TableFields.Map(season, state.Status != 0 ? 10 : 9);
            foreach ((uint item, uint count) in reward)
                rewardService.Grant(uid, item, (int)(count * pending), messages);

            state.ExtraClaimed += pending;
            Save(uid, state);
            return (int)pending;
        }

        public IReadOnlyList<uint> Tasks(uint uid, GameBpTaskType type)
        {
            BattlepassState state = Load(uid);
            return type switch
            {
                GameBpTaskType.Daily => state.Daily,
                GameBpTaskType.Weekly => state.Weekly,
                _ => [],
            };
        }

        public bool Refresh(uint uid, GameBpTaskType type, uint index, out uint newTask, out uint refreshCount)
        {
            newTask = 0;
            refreshCount = 0;
            BattlepassState state = Load(uid);
            List<uint> list = type == GameBpTaskType.Daily ? state.Daily : type == GameBpTaskType.Weekly ? state.Weekly : [];
            if (index >= list.Count)
                return false;

            uint kind = type == GameBpTaskType.Daily ? 1u : 2u;
            uint[] candidates = TaskIds(kind).Where(id => !list.Contains(id)).ToArray();
            if (candidates.Length == 0)
                return false;

            newTask = candidates[Random.Shared.Next(candidates.Length)];
            list[(int)index] = newTask;
            refreshCount = type == GameBpTaskType.Daily ? ++state.DailyRefreshes : ++state.WeeklyRefreshes;
            Save(uid, state);
            return true;
        }

        public void Activate(uint uid, bool advanced)
        {
            BattlepassState state = Load(uid);
            state.Started = true;
            if (advanced)
                state.Status = 1;
            Save(uid, state);
        }

        public uint LeftRewards(uint uid)
        {
            IMessage? season = Season();
            uint max = season is null ? 0 : TableFields.U(season, 8);
            return max > Load(uid).ExtraClaimed ? max - Load(uid).ExtraClaimed : 0;
        }

        private uint MaxLevel() => Season() is { } season ? TableFields.U(season, 6) : 0;

        private uint PerLevel() => Season() is { } season ? Math.Max(1u, TableFields.U(season, 7)) : 100;

        private uint Exp(uint uid) =>
            (uint)Math.Max(0, itemService.GetPlayerItems(uid).FirstOrDefault(i => i.ItemId == ExpItem)?.Count ?? 0);

        private uint Level(uint uid) => Math.Min(MaxLevel(), Exp(uid) / PerLevel());

        /// <summary>Extra rewards: every full <c>PerLevel</c> of experience beyond the maximum level, up to the season cap.</summary>
        private uint ExtraEarned(uint uid)
        {
            uint beyond = Exp(uid) / PerLevel();
            uint extra = beyond > MaxLevel() ? beyond - MaxLevel() : 0;
            IMessage? season = Season();
            return season is null ? 0 : Math.Min(extra, TableFields.U(season, 8));
        }

        private IMessage? Season() => tableService.GetTable<BpSeasonData>().FirstOrDefault(s => s.Id == SeasonId);

        private IEnumerable<uint> TaskIds(uint kind) =>
            tableService.GetTable<BpTaskData>()
                .Where(t => TableFields.U(t, 12) == SeasonId && TableFields.U(t, 2) == kind)
                .Select(t => TableFields.U(t, 1))
                .OrderBy(id => id);

        private void Grant(uint uid, IReadOnlyList<(uint Key, uint Value)> reward, List<IMessage> messages)
        {
            foreach ((uint item, uint count) in reward)
                rewardService.Grant(uid, item, (int)count, messages);
        }

        private BattlepassState Load(uint uid)
        {
            BattlepassState state = accountState.Get<BattlepassState>(uid, StateKey) ?? new BattlepassState();
            bool changed = false;
            changed |= Fill(state.Daily, 1, DailyCount);
            changed |= Fill(state.Weekly, 2, WeeklyCount);
            changed |= Fill(state.Season, 5, SeasonTaskCount);
            if (changed)
            {
                accountState.Set(uid, StateKey, state);
                Log.Information("Battle pass of account {Uid} initialised for season {Season}.", uid, SeasonId);
            }

            return state;
        }

        private bool Fill(List<uint> list, uint kind, int count)
        {
            if (list.Count >= count)
                return false;

            foreach (uint id in TaskIds(kind).Where(id => !list.Contains(id)).Take(count - list.Count).ToList())
                list.Add(id);
            return true;
        }

        private void Save(uint uid, BattlepassState state) => accountState.Set(uid, StateKey, state);
    }
}
