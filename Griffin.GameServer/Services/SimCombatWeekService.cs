using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The weekly simulation mode 要塞伯爵 (SimCombat "weekly B", normal challenge): 9 levels (<c>WeeklyCycleData</c> col 3, one <c>WeeklyDegreeData</c> row each) of a boss that is
    /// fought squad after squad (col 8 = squads, col 10 = their lineup keys = rows of <c>WeeklyBStoryData</c>, whose col 2 is the stage of the battle). Every attempt costs the ticket of the cycle (col 5, item 304); winning every
    /// squad of a level pays the reward of the rating the score reached (<c>WeeklyScoreRankData</c>) and opens the next level. Everything is read by field number; the open
    /// points are ASSUMPTION A-131 in docs/assumptions.md.
    /// </summary>
    public interface ISimCombatWeekService : IGameService
    {
        /// <summary>The whole <c>SC_SimCombatWeekInfo</c> (written by hand: the lineups of the checked-in proto have the wrong type) and the ticket top-up of the week.</summary>
        (ushort MsgId, byte[] Payload) BuildInfo(uint uid, List<IMessage> messages);

        /// <summary>Starts an attempt of the normal challenge with the squads the client sent; returns the error to refuse with, or null.</summary>
        HNINDAOJCKP? Start(uint uid, IReadOnlyList<IReadOnlyList<uint>> squads, List<IMessage> messages, out (ushort MsgId, byte[] Payload) reply);

        /// <summary>A battle of the attempt is about to start (stage check).</summary>
        HNINDAOJCKP? BeginBattle(uint uid, uint stageId);

        /// <summary>A battle of the attempt ended; a win scores and moves on to the next squad (the score push is added to <paramref name="messages"/>).</summary>
        void EndBattle(uint uid, bool won, uint points, IReadOnlyCollection<uint> challenges, List<IMessage> messages);

        /// <summary>Opens the next level once the current one is cleared.</summary>
        HNINDAOJCKP? StartNextStorey(uint uid, out SC_SimCombatWeekStartNextStorey? reply);

        /// <summary>Gives the attempt up (retreat) or leaves the mode (quit); the ticket is not refunded.</summary>
        void Abandon(uint uid);

        SimCombatWeekService.WeekView ViewOf(uint uid);

        /// <summary>The squad of the battle <paramref name="stageId"/> of the attempt in progress (the characters the player chose for it), or empty.</summary>
        IReadOnlyList<uint> SquadFor(uint uid, uint stageId);

        // ---- the boss challenges of the other tab (战线挑战 / «Desafío de encrucijada»): one entry per boss of the cycle (WeeklyCycleData col 4)
        HNINDAOJCKP? StartBoss(uint uid, uint boss, IReadOnlyList<IReadOnlyList<uint>> squads, List<IMessage> messages);

        HNINDAOJCKP? BeginBossBattle(uint uid, uint boss, uint stageId);

        void EndBossBattle(uint uid, uint boss, bool won, SimCombatWeekService.PhaseFacts facts, List<IMessage> messages);

        /// <summary>Sweep of a level / boss already cleared: spends a ticket and counts as a clear with the best score.</summary>
        HNINDAOJCKP? Raid(uint uid, SimCombatWeekly.Types.Status status, uint boss, List<IMessage> messages);

        void AbandonBoss(uint uid, uint boss);

        SimCombatWeekService.BossView BossOf(uint uid, uint boss);

        IReadOnlyList<uint> Bosses();

        /// <summary>The state of every objective as the common quest counters of the WeeklyChallenge tab.</summary>
        CommonQuestCounters QuestCounters(uint uid);

        /// <summary>Pays the objectives that are complete and not taken yet; returns the ones taken.</summary>
        List<uint> ClaimQuests(uint uid, IEnumerable<uint> ids, List<IMessage> messages);
    }

    public sealed class SimCombatWeekService(ITableService tables, IAccountStateService accountState, IGunService gunService, IItemService itemService, IRewardService rewardService)
        : ISimCombatWeekService
    {
        private const string Key = "simweek.state";
        private const uint CycleId = 4;
        private const uint PlanId = 10004;
        private const uint TicketItem = 304;
        /// <summary>Tickets of the week (the mode shows "0/5" attempts, A-131): the balance is raised to this number when a new week starts.</summary>
        public const int WeeklyTickets = 5;

        public sealed class WeekState
        {
            public long Week { get; set; }
            public int Level { get; set; }
            public bool Started { get; set; }
            public bool Cleared { get; set; }
            public Dictionary<string, List<uint>> Lineups { get; set; } = [];
            public List<uint> Done { get; set; } = [];
            public uint Score { get; set; }
            public uint Attempts { get; set; }
            public long TicketsWeek { get; set; } = -1;
            public Dictionary<string, uint> Best { get; set; } = [];
            public List<uint> Paid { get; set; } = [];
            public Dictionary<string, BossState> Bosses { get; set; } = [];
            public List<uint> QuestsDone { get; set; } = [];
            public List<uint> QuestsClaimed { get; set; } = [];
            /// <summary>Objectives paid by the first versions (they paid at once, A-134) or marked with the old rules (A-135/A-136): never paid twice.</summary>
            public List<uint> QuestsPaid { get; set; } = [];
            public int Version { get; set; }
        }

        public sealed class BossState
        {
            public bool Started { get; set; }
            public bool Cleared { get; set; }
            public Dictionary<string, List<uint>> Lineups { get; set; } = [];
            public List<uint> Done { get; set; } = [];
            public uint Score { get; set; }
            public uint Best { get; set; }
            public uint Attempts { get; set; }
            public int Clears { get; set; }
            public List<uint> Challenges { get; set; } = [];
            public List<uint> KeysWon { get; set; } = [];
            public Dictionary<string, uint> KeyScores { get; set; } = [];
            /// <summary>Per phase: the achievements of the battle report (non_repeat_achievements) of the battles won in it.</summary>
            public Dictionary<string, List<uint>> KeyAch { get; set; } = [];
            /// <summary>What the report of each phase won in the attempt in progress said (A-138): the objectives that need several phases are judged with these.</summary>
            public Dictionary<string, PhaseFacts> Attempt { get; set; } = [];
        }

        /// <summary>What the battle report of one phase says about it (A-139); the same extraction is used by the server and by the log re-evaluation tool.</summary>
        public sealed class PhaseFacts
        {
            public List<uint> Achievements { get; set; } = [];
            public int DeadGuns { get; set; }
            public int Guns { get; set; }
            /// <summary>Points of the phase: <c>stage_progress.points</c> when the client sends it, else the hit points the boss lost (maximum minus what is left).</summary>
            public uint Score { get; set; }
            /// <summary>Hit points left of the main enemy (the one with the most maximum hit points) in percent, -1 when the report has none.</summary>
            public double BossHpPercent { get; set; } = -1;
            /// <summary>Layers of the tracked states on each gun of the player's side (stage_progress.objects[].buff_level; a missing value is 0 layers).</summary>
            public List<Dictionary<uint, uint>> PlayerBuffs { get; set; } = [];
            /// <summary>The same for the enemy units and for battle_boss[].buff_level.</summary>
            public List<Dictionary<uint, uint>> BossBuffs { get; set; } = [];
            /// <summary>Times each tracked skill triggered (stage_progress.simweek_skill_trigger_count; a missing value is 0).</summary>
            public Dictionary<uint, uint> Skills { get; set; } = [];
            /// <summary>Every state / skill id the report lists (even with 0): the ones the mode follows. An id that is not here cannot be measured.</summary>
            public List<uint> Tracked { get; set; } = [];

            public static PhaseFacts FromProgress(StageProgress? progress)
            {
                var facts = new PhaseFacts();
                if (progress is null)
                    return facts;
                facts.Achievements = progress.NonRepeatAchievements.ToList();
                // guns of the player's side (camp 1, object type 2) that end the battle with 0 hit points
                var guns = progress.Objects.Values.Where(o => (int)o.Type == 2 && o.KGMEBGGOOAM == 1).ToList();
                facts.Guns = guns.Count;
                facts.DeadGuns = guns.Count(o => o.LNGNOFLIBMJ <= 0);
                var enemies = progress.Objects.Values.Where(o => (int)o.Type == 3 && o.KGMEBGGOOAM == 2 && o.FINGOFJFMMN > 0).ToList();
                if (enemies.Count > 0)
                {
                    var main = enemies.OrderByDescending(o => o.FINGOFJFMMN).First();
                    facts.BossHpPercent = Math.Max(0, main.LNGNOFLIBMJ) * 100.0 / main.FINGOFJFMMN;
                }

                foreach (var gun in guns)
                    facts.PlayerBuffs.Add(gun.BuffLevel.ToDictionary(p => p.Key, p => p.Value));
                foreach (var enemy in progress.Objects.Values.Where(o => o.KGMEBGGOOAM == 2))
                    facts.BossBuffs.Add(enemy.BuffLevel.ToDictionary(p => p.Key, p => p.Value));
                foreach (var boss in progress.BattleBoss)
                    facts.BossBuffs.Add(boss.BuffLevel.ToDictionary(p => p.Key, p => p.Value));
                facts.Skills = progress.SimweekSkillTriggerCount.ToDictionary(p => p.Key, p => p.Value);
                facts.Tracked = facts.Skills.Keys.Concat(facts.PlayerBuffs.SelectMany(d => d.Keys)).Concat(facts.BossBuffs.SelectMany(d => d.Keys)).Distinct().ToList();
                long lost = enemies.Sum(o => (long)(o.FINGOFJFMMN - Math.Max(0, o.LNGNOFLIBMJ)));
                facts.Score = progress.Points > 0 ? progress.Points : (uint)Math.Max(1, Math.Min(lost, int.MaxValue));
                return facts;
            }
        }

        public sealed record ObjectiveLine(uint Id, uint Group, uint Boss, uint Key, uint Class, string Kind, string Measured, string Required, bool Met, string Reason);

        public sealed record BossView(uint Boss, bool Started, bool Cleared, uint Score, uint Best, uint Attempts, int Clears, int Squads);

        public sealed record WeekView(int Level, int Levels, uint Storey, bool Started, bool Cleared, uint Score, uint Best, uint Attempts, IReadOnlyList<uint> Done, int Rating);

        private sealed record LevelRow(uint Storey, uint Number, List<uint> Keys, List<uint> Stages);

        private static long CurrentWeek() => (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 86400 + 3) / 7;

        private WeekState Load(uint uid)
        {
            WeekState state = accountState.Get<WeekState>(uid, Key) ?? new WeekState { Week = CurrentWeek(), Version = 3 };
            if (state.Version < 3)
            {
                // version 3 (A-137/A-138): the objectives are judged by the battle report; what the older rules marked is cleared (what they paid is remembered so that nothing is paid twice)
                state.QuestsPaid = state.QuestsPaid.Concat(state.QuestsDone).Concat(state.QuestsClaimed).Distinct().ToList();
                state.QuestsDone = [];
                state.QuestsClaimed = [];
                state.Version = 3;
            }

            if (state.Week != CurrentWeek())
                state = new WeekState { Week = CurrentWeek(), TicketsWeek = state.TicketsWeek, Best = [], Paid = [], Bosses = [], QuestsDone = [], QuestsClaimed = [], QuestsPaid = [], Version = 3 };
            return state;
        }

        private void Save(uint uid, WeekState state) => accountState.Set(uid, Key, state);

        private List<LevelRow> Levels()
        {
            WeeklyCycleData? cycle = tables.GetTable<WeeklyCycleData>().FirstOrDefault(row => TableFields.U(row, 1) == CycleId);
            if (cycle is null)
                return [];
            var degrees = tables.GetTable<WeeklyDegreeData>().ToDictionary(row => TableFields.U(row, 1));
            // the key of a squad is a row of WeeklyBStoryData (col 2 = the StageData of that battle, type 24)
            var stageOfKey = tables.GetTable<WeeklyBStoryData>().ToDictionary(row => TableFields.U(row, 1), row => TableFields.U(row, 2));
            var levels = new List<LevelRow>();
            foreach (uint storey in TableFields.List(cycle, 3))
                if (degrees.TryGetValue(storey, out WeeklyDegreeData? degree))
                    {
                    List<uint> keys = TableFields.List(degree, 10).ToList();
                    levels.Add(new LevelRow(storey, TableFields.U(degree, 3), keys, keys.Select(key => stageOfKey.GetValueOrDefault(key)).ToList()));
                }
            return levels;
        }

        /// <summary>
        /// The rows of the boss challenges: <c>WeeklyDegreeData</c> of every boss of the cycle (col 4 of the cycle). Col 10 holds the squad keys; boss 12 has the column damaged in the table
        /// (a float), so the keys follow the rule of the table ((level + 10) * 10 + squad: 211 for boss 11, 221 for boss 12... all present in <c>WeeklyBStoryData</c>) (A-133).
        /// </summary>
        private LevelRow? BossRow(uint boss)
        {
            WeeklyCycleData? cycle = tables.GetTable<WeeklyCycleData>().FirstOrDefault(row => TableFields.U(row, 1) == CycleId);
            if (cycle is null || !TableFields.List(cycle, 4).Contains(boss))
                return null;
            WeeklyDegreeData? degree = tables.GetTable<WeeklyDegreeData>().FirstOrDefault(row => TableFields.U(row, 1) == boss);
            if (degree is null)
                return null;
            var stageOfKey = tables.GetTable<WeeklyBStoryData>().ToDictionary(row => TableFields.U(row, 1), row => TableFields.U(row, 2));
            uint squads = Math.Max(1, TableFields.U(degree, 8));
            List<uint> keys = TableFields.List(degree, 10).Where(stageOfKey.ContainsKey).ToList();
            if (keys.Count != squads)
                keys = Enumerable.Range(1, (int)squads).Select(i => (TableFields.U(degree, 3) + 10) * 10 + (uint)i).Where(stageOfKey.ContainsKey).ToList();
            return keys.Count == 0 ? null : new LevelRow(boss, TableFields.U(degree, 3), keys, keys.Select(k => stageOfKey[k]).ToList());
        }

        public IReadOnlyList<uint> Bosses()
        {
            WeeklyCycleData? cycle = tables.GetTable<WeeklyCycleData>().FirstOrDefault(row => TableFields.U(row, 1) == CycleId);
            return cycle is null ? [] : TableFields.List(cycle, 4).ToList();
        }

        private DLNCFFHEHIJ EntryOf(uint boss, BossState b)
        {
            var entry = new DLNCFFHEHIJ { Storey = boss, Score = b.Score, MaxScore = b.Best, KNPHEBDLFFP = boss, CEAKINDONJD = b.Attempts };
            foreach ((string key, List<uint> guns) in b.Lineups)
            {
                var lineup = new MFKLGJFHCFL();
                lineup.Guns.AddRange(guns.Select(id => new Gun { Id = id }));
                entry.Lineups[uint.Parse(key)] = lineup;
            }

            return entry;
        }

        // ---------------------------------------------------------------- mission objectives (WeeklyQuestData / WeeklyQuestByTypeData)

        private sealed record QuestInfo(uint Id, uint Type, uint Class, uint Target, uint Challenge, uint Rating, uint Key, Dictionary<uint, uint> Reward, uint Cmp = 0);

        private QuestInfo? QuestRow(uint id)
        {
            WeeklyQuestData? row = tables.GetTable<WeeklyQuestData>().FirstOrDefault(r => TableFields.U(r, 1) == id);
            return row is null ? null : new QuestInfo(id, TableFields.U(row, 2), TableFields.U(row, 8), Math.Max(1, TableFields.U(row, 9)), TableFields.U(row, 4), TableFields.U(row, 12), TableFields.U(row, 13),
                TableFields.Map(row, 10).ToDictionary(p => p.Key, p => p.Value), TableFields.U(row, 5));
        }

        private List<uint> QuestsOfGroup(uint group)
        {
            WeeklyQuestByTypeData? row = tables.GetTable<WeeklyQuestByTypeData>().FirstOrDefault(r => TableFields.U(r, 1) == group);
            return row is null ? [] : TableFields.List(row, 2).ToList();
        }

        /// <summary>Progress of a pending objective that is a counter: the number of the other objectives of its group that are done (class 334 "all objectives").</summary>
        private uint QuestProgress(WeekState state, BossState? boss, uint id)
        {
            QuestInfo? quest = QuestRow(id);
            if (quest is null || quest.Class != 334)
                return 0;
            uint group = 1000 + (boss is null ? 0 : state.Bosses.FirstOrDefault(p => ReferenceEquals(p.Value, boss)) is { Key: { } k } ? uint.Parse(k) : 0);
            return (uint)QuestsOfGroup(group).Count(other => other != id && state.QuestsDone.Contains(other));
        }

        /// <summary>
        /// The achievements of the battle report that stand for the conditions of the weekly bosses (A-138). The client judges them while it plays and lists the ones met in
        /// stage_progress.non_repeat_achievements; AchievementChallengeClientData rows 3001-3032 hold one per phase stage (the stage is in one of the text columns);
        /// 1030 / 1031 / 1032 are the "2;2;N" turn limits (5, 10 and 15). Generated from the table of this build.
        /// </summary>
        private static readonly Dictionary<uint, uint> PhaseAchievement = new()
        {
            [72901] = 3001, [72902] = 3002, [72903] = 3003, [72921] = 3007, [72922] = 3008, [72923] = 3009, [72931] = 3010, [72932] = 3011, [72933] = 3012, [72941] = 3013,
            [72942] = 3014, [72951] = 3015, [72952] = 3016, [72961] = 3017, [72962] = 3018, [72971] = 3019, [72972] = 3020, [72981] = 3021, [72982] = 3022, [72991] = 3023,
            [72992] = 3024, [720001] = 3025, [720002] = 3026, [720003] = 3027, [720004] = 3028, [720005] = 3029, [720006] = 3030, [720007] = 3031, [720008] = 3032,
        };

        private static readonly Dictionary<uint, uint> TurnAchievement = new() { [5] = 1030, [10] = 1031, [15] = 1032 };

        private enum Side { Player, Boss, Any }

        /// <summary>A mechanic measured from the report (A-140): states (buff ids) and/or skills (ids), the side that carries them, the comparison (&lt;=, &gt;=, ==0, units&lt;=) and the number.</summary>
        private sealed record Mechanic(Side Side, uint[] Buffs, uint[] Skills, string Op, uint N, string Text);

        // ids of BattleBuffPerformData / BattleSkillDisplayData by the name the objective's text uses; the objective -> mechanic pairs are read from the text of WeeklyQuestData (col 6)
        private static readonly uint[] Tremor = [62833700], Slow = [628345022], Frost = [69243034, 69253034], Collapse = [6897102], Morale = [67778041], Expose = [6777101], MoveDown = [64871201],
            Magnet = [6764801], Power = [68965011], Shield = [68965012], DoubleDef = [6481601], Together = [6482601], Beacon = [6774601, 6775601], Resolve = [678916081], AttackUp = [6763701],
            ShortSnake = [692412012, 692512012, 69241201, 69251201], ShortFrost = [692422012, 692522012, 69242201, 69252201], ShortScorpion = [692432012, 692532012, 69243201, 69253201],
            ShortShield = [692452012, 692552012, 69245201, 69255201, 469255202], Suppress = [6552401, 6552402], Heat = [61765011, 61775011, 6176501, 6177501], Barrel = [7026101, 7013201, 70261101],
            Duel = [6964102, 6965102], Wail = [6965403, 6965404, 6964103], Overpress = [6777501, 6778501], Evade = [67891801, 67891802], Bomb = [6774901];

        private static readonly Dictionary<uint, Mechanic> Mechanics = new()
        {
            [254011] = new(Side.Player, Beacon, [], "units<=", 2, "humanoids that got beacon lock"),
            [255031] = new(Side.Boss, Magnet, [], ">=", 50, "layers of magnetic disturbance on the boss"),
            [255051] = new(Side.Boss, AttackUp, [], "<=", 10, "layers of attack up"), [255053] = new(Side.Boss, AttackUp, [], "<=", 10, "layers of attack up"),
            [251041] = new(Side.Boss, Morale, [], "<=", 15, "layers of battle morale"), [251043] = new(Side.Boss, Morale, [], "<=", 15, "layers of battle morale"),
            [251021] = new(Side.Player, Expose, [], "<=", 4, "layers of exposure on a humanoid"),
            [251031] = new(Side.Any, [], Overpress, "==0", 0, "triggers of overwhelming suppression"), [251033] = new(Side.Any, [], Overpress, "==0", 0, "triggers of overwhelming suppression"),
            [249011] = new(Side.Boss, DoubleDef, [], "==0", 0, "layers of double defence"), [249012] = new(Side.Boss, Together, [], "==0", 0, "layers of together"),
            [249013] = new(Side.Boss, DoubleDef, [], "==0", 0, "layers of double defence"), [249014] = new(Side.Boss, Together, [], "==0", 0, "layers of together"),
            [249041] = new(Side.Player, MoveDown, [], "<=", 2, "layers of movement down I"), [249043] = new(Side.Player, MoveDown, [], "<=", 2, "layers of movement down I"),
            [250031] = new(Side.Boss, Power, [], "==0", 0, "layers of power boost"), [250032] = new(Side.Boss, Shield, [], "==0", 0, "layers of shield resonance"),
            [250034] = new(Side.Boss, Power, [], "==0", 0, "layers of power boost"), [250035] = new(Side.Boss, Shield, [], "==0", 0, "layers of shield resonance"),
            [250021] = new(Side.Boss, Collapse, [], "<=", 2, "layers of collapse"),
            [252031] = new(Side.Any, Frost, [], "<=", 2, "layers of frost stiffness"), [252033] = new(Side.Any, Frost, [], "<=", 2, "layers of frost stiffness"),
            [252041] = new(Side.Boss, ShortSnake, [], ">=", 1, "short circuit of the snake king"), [252042] = new(Side.Boss, ShortFrost, [], ">=", 1, "short circuit of the frost dragon"),
            [252043] = new(Side.Boss, ShortScorpion, [], ">=", 1, "short circuit of the scorpion"), [252044] = new(Side.Boss, ShortShield, [], ">=", 1, "short circuit of the burning shield"),
            [245011] = new(Side.Boss, Tremor, [], ">=", 30, "layers of tremor"), [245021] = new(Side.Boss, Slow, [], ">=", 1, "times slowed-mind was gained"),
            [246011] = new(Side.Any, Suppress, Suppress, "==0", 0, "damage taken from the global suppression"),
            [247031] = new(Side.Any, [], Heat, "<=", 1, "triggers of high temperature"), [247033] = new(Side.Any, [], Heat, "<=", 1, "triggers of high temperature"),
            [247041] = new(Side.Any, [], Barrel, "<=", 6, "water barrels destroyed"), [247043] = new(Side.Any, [], Barrel, "<=", 6, "water barrels destroyed"),
            [248031] = new(Side.Boss, Duel, Duel, "==0", 0, "triggers of the duel protocol"), [248041] = new(Side.Any, [], Wail, "<=", 1, "triggers of the megahertz wail"),
            [253031] = new(Side.Any, [], Evade, "<=", 3, "triggers of evade"), [253033] = new(Side.Any, [], Evade, "<=", 3, "triggers of evade"),
            [253021] = new(Side.Boss, Resolve, [], "<=", 2, "layers of resolve"),
            [254031] = new(Side.Any, [], Bomb, "<=", 4, "triggers of the proximity mines"), [254033] = new(Side.Any, [], Bomb, "<=", 4, "triggers of the proximity mines"),
        };

        /// <summary>Objectives whose condition is "the boss ends with less than N percent of its hit points" (text of the table); the report holds the hit points, so they are measured.</summary>
        private static readonly Dictionary<uint, double> BossHpObjective = new() { [253012] = 5, [253032] = 5 };

        /// <summary>
        /// Judges every objective of a boss (the group 1000 + boss and the sub-objective groups its objectives name) against the reports of the attempt in progress and says why (A-138/A-139).
        /// <paramref name="done"/> are the objectives already complete (they feed the "all the other objectives" and the group objectives).
        /// </summary>
        public IReadOnlyList<ObjectiveLine> ExplainBoss(uint boss, BossState b, IReadOnlyCollection<uint> done)
        {
            LevelRow? row = BossRow(boss);
            if (row is null)
                return [];
            uint root = 1000 + boss;
            var groups = new List<uint> { root };
            groups.AddRange(QuestsOfGroup(root).Select(QuestRow).Where(q => q is { Challenge: not 0 }).Select(q => q!.Challenge).Distinct().Where(g => QuestsOfGroup(g).Count > 0));
            var complete = new HashSet<uint>(done);
            var lines = new Dictionary<uint, ObjectiveLine>();
            bool changed;
            do
            {
                changed = false;
                foreach (uint g in groups)
                    foreach (uint id in QuestsOfGroup(g))
                    {
                        if (QuestRow(id) is not { } q)
                            continue;
                        ObjectiveLine line = JudgeBoss(b, row, boss, g, q, complete);
                        if (complete.Contains(id) && !line.Met)
                            line = line with { Met = true, Reason = "already complete" };
                        lines[id] = line;
                        if (line.Met && complete.Add(id))
                            changed = true;
                    }
            }
            while (changed);
            return groups.SelectMany(QuestsOfGroup).Distinct().Where(lines.ContainsKey).Select(id => lines[id]).ToList();
        }

        /// <summary>Which data decides an objective (for the coverage list of the tool): "measured", "achievement" (client-judged, reported) or "unevaluable".</summary>
        public (string Path, string Detail) DescribeRule(uint boss, uint questId)
        {
            LevelRow? row = BossRow(boss);
            if (row is null || QuestRow(questId) is not { } q)
                return ("unevaluable", "no such objective");
            if (q.Class is 318 or 317 or 334 or 316 || (q.Class == 321 && q.Id != 23004))
                return ("measured", "win / score / other objectives");
            if (q.Class == 329)
                return TurnAchievement.Keys.Any(t => t <= q.Target) ? ("achievement", $"turn achievement {TurnAchievement[TurnAchievement.Keys.Where(t => t <= q.Target).Max()]}") : ("unevaluable", "no turn achievement");
            if (q.Class == 330)
                return ("measured", "guns at 0 hp in the report");
            if (Mechanics.TryGetValue(q.Id, out Mechanic? m))
                return ("measured", $"{m.Text} {m.Op} {m.N} (ids {string.Join(",", m.Buffs.Concat(m.Skills))}; falls back to the achievement when the report does not list them)");
            if (BossHpObjective.ContainsKey(q.Id))
                return ("measured", "enemy hit points left");
            if (q.Type == 3 && q.Challenge != 0 && QuestsOfGroup(q.Challenge).Count > 0)
                return ("measured", "sub-objectives of group " + q.Challenge);
            IEnumerable<uint> keys = q.Key != 0 ? [q.Key] : row.Keys;
            var stages = keys.Select(k => row.Stages[row.Keys.IndexOf(k)]).ToList();
            return stages.All(PhaseAchievement.ContainsKey)
                ? ("achievement", "phase achievement " + string.Join(",", stages.Select(s => PhaseAchievement[s])))
                : ("unevaluable", "no achievement for stage " + string.Join(",", stages.Where(s => !PhaseAchievement.ContainsKey(s))));
        }

        private ObjectiveLine JudgeBoss(BossState boss, LevelRow row, uint bossId, uint group, QuestInfo q, IReadOnlySet<uint> complete)
        {
            string phaseNames(IEnumerable<uint> keys) => string.Join("+", keys);
            IReadOnlyList<uint> phases = q.Key != 0 ? [q.Key] : row.Keys;
            ObjectiveLine Line(string kind, string measured, string required, bool met, string reason = "") =>
                new(q.Id, group, bossId, q.Key, q.Class, kind, measured, required, met, reason);
            // the report of each phase wanted, from the attempt in progress
            var reports = phases.Select(key => (Key: key, Stage: row.Stages[row.Keys.IndexOf(key)], Facts: boss.Attempt.GetValueOrDefault(key.ToString()))).ToList();
            string missing = string.Join(",", reports.Where(r => r.Facts is null).Select(r => r.Key));
            string Per(Func<(uint Key, uint Stage, PhaseFacts Facts), string> f) => string.Join(" ", reports.Where(r => r.Facts is not null).Select(r => $"[{r.Key}] {f((r.Key, r.Stage, r.Facts!))}"));
            bool All(Func<(uint Key, uint Stage, PhaseFacts Facts), bool> f) => reports.All(r => r.Facts is not null && f((r.Key, r.Stage, r.Facts)));
            string NoReport() => missing.Length > 0 ? $"phase {missing} not won in this attempt" : "";

            switch (q.Class)
            {
                case 318:
                    return Line("complete the boss once", $"clears={boss.Clears}", ">=1", boss.Clears > 0);
                case 317:
                {
                    uint best = boss.KeyScores.GetValueOrDefault(q.Key.ToString());
                    return Line("score of the phase", $"best score {best}", $">={q.Target}", best >= q.Target, best == 0 ? "phase never won" : "");
                }
                case 334:
                {
                    int n = QuestsOfGroup(group).Count(other => other != q.Id && complete.Contains(other));
                    return Line("all the other objectives", $"{n} done", $">={q.Target}", n >= q.Target);
                }
                case 316 or 321 when q.Id != 23004:
                    return Line("kill the boss in the phase", Per(r => "won"), $"phase {phaseNames(phases)} won", All(_ => true), NoReport());
                case 329:
                {
                    uint limit = TurnAchievement.Keys.Where(t => t <= q.Target).DefaultIfEmpty(0u).Max();
                    uint ach = limit == 0 ? 0 : TurnAchievement[limit];
                    return Line("turn limit", Per(r => $"achievements {string.Join(",", r.Facts.Achievements.Where(a => a is >= 1030 and <= 1032))}"), $"<={q.Target} turns => achievement {ach} (<={limit} turns)",
                        ach != 0 && All(r => r.Facts.Achievements.Contains(ach)), ach == 0 ? "no turn achievement for that limit" : NoReport());
                }
                case 330:
                {
                    int allowed = q.Cmp == 4 ? (int)q.Target : 0;
                    return Line("guns destroyed", Per(r => $"{r.Facts.DeadGuns} of {r.Facts.Guns} guns at 0 hp"), $"<={allowed}", All(r => r.Facts.DeadGuns <= allowed), NoReport());
                }
            }

            if (Mechanics.TryGetValue(q.Id, out Mechanic? mech))
            {
                // the ids must be listed by the report (it lists the states and skills the mode follows, even with 0): otherwise nothing can be said and the achievement of the phase decides
                bool listed = reports.All(r => r.Facts is not null && mech.Buffs.Concat(mech.Skills).Any(r.Facts.Tracked.Contains));
                if (listed)
                {
                    (string Text, bool Met) Measure(PhaseFacts f)
                    {
                        IEnumerable<Dictionary<uint, uint>> units = mech.Side switch
                        {
                            Side.Player => f.PlayerBuffs,
                            Side.Boss => f.BossBuffs,
                            _ => f.PlayerBuffs.Concat(f.BossBuffs),
                        };
                        uint layers = units.Select(u => mech.Buffs.Select(id => u.GetValueOrDefault(id)).DefaultIfEmpty(0u).Max()).DefaultIfEmpty(0u).Max();
                        uint withBuff = (uint)units.Count(u => mech.Buffs.Any(id => u.GetValueOrDefault(id) > 0));
                        uint triggers = (uint)mech.Skills.Sum(id => (long)f.Skills.GetValueOrDefault(id));
                        uint value = mech.Op == "units<=" ? withBuff : mech.Buffs.Length > 0 && mech.Skills.Length == 0 ? layers : mech.Buffs.Length == 0 ? triggers : Math.Max(layers, triggers);
                        bool met = mech.Op switch { "<=" or "units<=" => value <= mech.N, ">=" => value >= mech.N, _ => value == 0 };
                        return ($"{value} {mech.Text}", met);
                    }

                    return Line("boss mechanic: " + mech.Text, Per(r => Measure(r.Facts).Text), $"{(mech.Op == "==0" ? "==0" : mech.Op + " " + mech.N)}", All(r => Measure(r.Facts).Met), NoReport());
                }
            }

            if (BossHpObjective.TryGetValue(q.Id, out double percent))
                return Line("boss hit points left", Per(r => r.Facts.BossHpPercent < 0 ? "no enemy in the report" : $"{r.Facts.BossHpPercent:0.0}% left"), $"<{percent}%", All(r => r.Facts.BossHpPercent is >= 0 && r.Facts.BossHpPercent < percent), NoReport());
            if (q.Type == 3 && q.Challenge != 0 && QuestsOfGroup(q.Challenge).Count > 0)
            {
                List<uint> subs = QuestsOfGroup(q.Challenge);
                int n = subs.Count(complete.Contains);
                return Line("all the sub-objectives of group " + q.Challenge, $"{n} of {subs.Count} done", $"{subs.Count}", n == subs.Count);
            }

            // a boss mechanic (layers of a state, hits on a part, triggers...): the client judged it while it played and reported the achievement of the phase stage
            return Line("boss mechanic (class " + q.Class + ")",
                Per(r => PhaseAchievement.TryGetValue(r.Stage, out uint a) ? $"achievement {a} {(r.Facts.Achievements.Contains(a) ? "reported" : "not reported")}" : $"stage {r.Stage} has no achievement"),
                "achievement of the phase stage reported" + (phases.Count > 1 ? " in every phase" : ""),
                All(r => PhaseAchievement.TryGetValue(r.Stage, out uint a) && r.Facts.Achievements.Contains(a)),
                reports.Any(r => !PhaseAchievement.ContainsKey(r.Stage)) ? "cannot be judged: the table has no achievement for this stage" : NoReport());
        }

        /// <summary>
        /// Checks the objectives after a win (A-134, A-137, A-139): the boss objectives with <see cref="ExplainBoss"/>, the rating objectives of the normal levels with the best score. Every
        /// objective is logged with what was measured, what was required and the result, including the ones that are not met. A finished objective is recorded as complete; the reward is
        /// taken with the claim message of the quest tab.
        /// </summary>
        private void EvaluateQuests(uint uid, WeekState state, uint group, BossState? boss, uint? wonKey, List<IMessage> messages)
        {
            bool anyDone = false;
            if (boss is null)
            {
                uint number = group - 1000;
                LevelRow? level = Levels().FirstOrDefault(l => l.Number == number);
                foreach (uint id in QuestsOfGroup(group))
                {
                    QuestInfo? q = QuestRow(id);
                    if (q is null || level is null)
                        continue;
                    uint best = state.Best.GetValueOrDefault(level.Storey.ToString());
                    int rating = RatingOf(number, best);
                    bool met = q.Class == 315 && rating >= q.Rating;
                    Log.Information("SimCombatWeek objective {Quest} (group {Group}, level {Level}, rating >= {Required}) measured best score {Best} = rating {Rating} => {Result}", id, group, number, q.Rating, best, rating, met ? "MET" : "not met");
                    if (met && !state.QuestsDone.Contains(id))
                    {
                        state.QuestsDone.Add(id);
                        anyDone = true;
                    }
                }
            }
            else
            {
                foreach (ObjectiveLine line in ExplainBoss(group - 1000, boss, state.QuestsDone))
                {
                    Log.Information("SimCombatWeek objective {Quest} (group {Group}, boss {Boss}, phase {Key}, class {Class}, {Kind}) measured {Measured}, required {Required} => {Result} {Reason}",
                        line.Id, line.Group, line.Boss, line.Key, line.Class, line.Kind, line.Measured, line.Required, line.Met ? "MET" : "not met", line.Reason);
                    if (line.Met && !state.QuestsDone.Contains(line.Id))
                    {
                        state.QuestsDone.Add(line.Id);
                        anyDone = true;
                        Log.Information("SimCombatWeek: account {Uid} completed objective {Quest} (group {Group}).", uid, line.Id, line.Group);
                    }
                }
            }

            // the quest screens and the result screen read the state of the objectives from the common quest counters (type WeeklyChallenge)
            if (anyDone)
                messages.Add(new SC_CommonQuestCountUpdate { Counters = CountersOf(state) });
        }

        private CommonQuestCounters CountersOf(WeekState state)
        {
            var counters = new CommonQuestCounters { Type = CommonQuestCounters.Types.Type.WeeklyChallenge };
            var groups = new List<uint>();
            foreach (uint level in Levels().Select(l => l.Number))
                groups.Add(1000 + level);
            groups.AddRange(Bosses().Select(b => 1000 + b));
            // the sub-objectives (type 4) that the objectives of a boss name in col 4
            foreach (uint bossGroup in Bosses().Select(b => 1000 + b))
                groups.AddRange(QuestsOfGroup(bossGroup).Select(QuestRow).Where(q => q is { Challenge: not 0 }).Select(q => q!.Challenge).Where(g => QuestsOfGroup(g).Count > 0));
            foreach (uint group in groups.Distinct())
                foreach (uint id in QuestsOfGroup(group))
                {
                    QuestInfo? q = QuestRow(id);
                    bool done = state.QuestsDone.Contains(id);
                    // present = complete, the value says whether the reward was taken (the same convention as the daily and achievement tabs)
                    if (done)
                        counters.Rewards[id] = state.QuestsClaimed.Contains(id);
                    BossState? boss = group >= 1011 && group < 1100 ? state.Bosses.GetValueOrDefault((group - 1000).ToString()) : null;
                    long progress = done ? q?.Target ?? 1 : q?.Class switch
                    {
                        317 when boss is not null => boss.KeyScores.GetValueOrDefault(q.Key.ToString()),
                        334 => QuestsOfGroup(group).Count(other => other != id && state.QuestsDone.Contains(other)),
                        _ => 0,
                    };
                    counters.JGOJBNHMJJH[id] = progress;
                }

            return counters;
        }

        public CommonQuestCounters QuestCounters(uint uid)
        {
            lock (AccountLock.For(uid))
                return CountersOf(Load(uid));
        }

        public List<uint> ClaimQuests(uint uid, IEnumerable<uint> ids, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                var taken = new List<uint>();
                foreach (uint id in ids.Distinct())
                {
                    QuestInfo? q = QuestRow(id);
                    if (q is null || !state.QuestsDone.Contains(id) || state.QuestsClaimed.Contains(id))
                        continue;
                    if (!state.QuestsPaid.Contains(id))
                        foreach ((uint item, uint count) in q.Reward)
                            rewardService.Grant(uid, item, (int)count, messages);
                    state.QuestsClaimed.Add(id);
                    taken.Add(id);
                }

                Save(uid, state);
                return taken;
            }
        }

        public HNINDAOJCKP? Raid(uint uid, SimCombatWeekly.Types.Status status, uint boss, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                if (status == SimCombatWeekly.Types.Status.Final)
                {
                    BossState b = BossStateOf(state, boss);
                    if (BossRow(boss) is null || b.Clears == 0)
                        return HNINDAOJCKP.ActivityMiniGameStatusInvalid;
                    TopUpTickets(uid, state, messages);
                    if (!itemService.TrySpend(uid, Cost(), out SC_Items update))
                    {
                        Save(uid, state);
                        return HNINDAOJCKP.ItemNotEnough;
                    }

                    messages.Add(update);
                    b.Attempts++;
                    b.Clears++;
                    EvaluateQuests(uid, state, 1000 + boss, b, null, messages);
                }
                else
                {
                    LevelRow? level = Current(Levels(), state);
                    if (level is null || BestOf(state, level) == 0)
                        return HNINDAOJCKP.ActivityMiniGameStatusInvalid;
                    TopUpTickets(uid, state, messages);
                    if (!itemService.TrySpend(uid, Cost(), out SC_Items update))
                    {
                        Save(uid, state);
                        return HNINDAOJCKP.ItemNotEnough;
                    }

                    messages.Add(update);
                    state.Attempts++;
                    EvaluateQuests(uid, state, 1000 + level.Number, null, null, messages);
                }

                Save(uid, state);
                messages.Insert(0, new SC_SimCombatWeekRaid());
                messages.Add(new SC_SimCombatWeekRefreshNtf());
                return null;
            }
        }

        private Dictionary<uint, int> Cost()
        {
            WeeklyCycleData? cycle = tables.GetTable<WeeklyCycleData>().FirstOrDefault(row => TableFields.U(row, 1) == CycleId);
            var cost = new Dictionary<uint, int>();
            if (cycle is not null)
                foreach ((uint item, uint count) in TableFields.Map(cycle, 5))
                    cost[item] = (int)count;
            if (cost.Count == 0)
                cost[TicketItem] = 1;
            return cost;
        }

        /// <summary>Rows of the rating table of a level: (rating, lowest score, highest score, reward).</summary>
        private List<(uint Rating, uint Low, uint High, Dictionary<uint, uint> Reward)> Ratings(uint number)
        {
            var rows = new List<(uint, uint, uint, Dictionary<uint, uint>)>();
            foreach (WeeklyScoreRankData row in tables.GetTable<WeeklyScoreRankData>().Where(r => TableFields.U(r, 3) == number))
            {
                List<uint> range = TableFields.List(row, 5).ToList();
                if (range.Count >= 2)
                    rows.Add((TableFields.U(row, 6), range[0], range[1], TableFields.Map(row, 7).ToDictionary(p => p.Key, p => p.Value)));
            }

            return rows.OrderBy(r => r.Item1).ToList();
        }

        private int RatingOf(uint number, uint score) => (int)Ratings(number).Where(r => score >= r.Low && score <= r.High).Select(r => r.Rating).DefaultIfEmpty(0u).Max();

        private static LevelRow? Current(List<LevelRow> levels, WeekState state) => state.Level >= 0 && state.Level < levels.Count ? levels[state.Level] : null;

        // ---------------------------------------------------------------- hand-written wire format

        private static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>();
            while (value >= 0x80)
            {
                bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }

            bytes.Add((byte)value);
            return bytes.ToArray();
        }

        private static void Field(List<byte> into, int number, ulong value)
        {
            into.AddRange(Varint((ulong)number << 3));
            into.AddRange(Varint(value));
        }

        private static void Message(List<byte> into, int number, IReadOnlyCollection<byte> payload)
        {
            into.AddRange(Varint(((ulong)number << 3) | 2));
            into.AddRange(Varint((ulong)payload.Count));
            into.AddRange(payload);
        }

        /// <summary>
        /// <c>SimCombatWeekStepB</c> as the client reads it (<c>GIDILPFDJJB</c>): 1 = lineups map (key → list of characters, each a message whose field 1 is the character id),
        /// 9 = key of the last squad fought, 12 = best score, 15 = score, 16 = every squad done.
        /// </summary>
        private static List<byte> StepB(WeekState state, LevelRow? level, uint best)
        {
            var bytes = new List<byte>();
            foreach ((string key, List<uint> guns) in state.Lineups.OrderBy(pair => uint.Parse(pair.Key)))
            {
                var lineup = new List<byte>();
                foreach (uint gun in guns)
                    Message(lineup, 1, [.. Varint(1 << 3), .. Varint(gun)]);
                var entry = new List<byte>();
                Field(entry, 1, uint.Parse(key));
                Message(entry, 2, lineup);
                Message(bytes, 1, entry);
            }

            // a cleared attempt reports no squad done: the client counts the squads done to choose the battle of its "re-challenge" button and with every squad done it indexes past the list (A-142)
            uint lastDone = level is null || state.Cleared ? 0 : level.Keys.Where(state.Done.Contains).DefaultIfEmpty(0u).Last();
            if (lastDone != 0)
                Field(bytes, 9, lastDone);
            if (best != 0)
                Field(bytes, 12, best);
            if (state.Score != 0)
                Field(bytes, 15, state.Score);
            if (level is not null && level.Keys.Count > 0 && level.Keys.All(state.Done.Contains))
                Field(bytes, 16, 1);
            return bytes;
        }

        // ---------------------------------------------------------------- state

        private uint BestOf(WeekState state, LevelRow? level) => level is null ? 0 : state.Best.GetValueOrDefault(level.Storey.ToString());

        private void TopUpTickets(uint uid, WeekState state, List<IMessage> messages)
        {
            if (state.TicketsWeek == state.Week)
                return;
            state.TicketsWeek = state.Week;
            int have = itemService.GetPlayerItems(uid).FirstOrDefault(i => i.ItemId == TicketItem)?.Count ?? 0;
            if (have < WeeklyTickets)
                rewardService.Grant(uid, TicketItem, WeeklyTickets - have, messages);
        }

        public (ushort MsgId, byte[] Payload) BuildInfo(uint uid, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                TopUpTickets(uid, state, messages);
                Save(uid, state);
                List<LevelRow> levels = Levels();
                LevelRow? level = Current(levels, state);
                var info = new SimCombatWeekly { PlanId = PlanId, Storey = level?.Storey ?? 1, StepBtimes = state.Attempts };
                info.WeeklyQuest.AddRange(state.QuestsDone.Where(id => QuestRow(id)?.Type == 2));
                foreach ((string storey, uint best) in state.Best)
                    info.HistoryScore[uint.Parse(storey)] = best;
                var infoBytes = new List<byte>(info.ToByteArray());
                if (state.Started || state.Done.Count > 0 || state.Score != 0 || BestOf(state, level) != 0)
                    Message(infoBytes, 17, StepB(state, level, BestOf(state, level)));
                // map entries of ODKPMJFEMIL (field 25): key = boss, value = DLNCFFHEHIJ; the client reads the current degree (1), the key of the last squad done (2), the scores (3, 4),
                // the lineups (5), the boss (7) and the attempts (8)
                foreach (uint boss in Bosses())
                {
                    BossState b = state.Bosses.GetValueOrDefault(boss.ToString()) ?? new BossState();
                    var raw = new List<byte>(EntryOf(boss, b).ToByteArray());
                    uint last = BossRow(boss) is { } row && !b.Cleared ? row.Keys.Where(b.Done.Contains).DefaultIfEmpty(0u).Last() : 0;
                    if (last != 0)
                        Field(raw, 2, last);
                    foreach (uint questId in QuestsOfGroup(1000 + boss))
                    {
                        bool done = state.QuestsDone.Contains(questId);
                        var quest = new List<byte>();
                        Field(quest, 1, questId);
                        Field(quest, 2, done ? QuestRow(questId)?.Target ?? 1 : QuestProgress(state, b, questId));
                        Field(quest, 3, QuestRow(questId)?.Target ?? 1);
                        Field(quest, 4, done ? 1u : 0u);
                        Message(raw, 9, quest);
                    }

                    var mapEntry = new List<byte>();
                    Field(mapEntry, 1, boss);
                    Message(mapEntry, 2, raw);
                    Message(infoBytes, 25, mapEntry);
                }

                var outer = new List<byte>();
                Message(outer, 1, infoBytes);
                Field(outer, 2, CycleId);
                return ((ushort)Common.Protocol.MsgId.MsgScSimCombatWeekInfo, outer.ToArray());
            }
        }

        public IReadOnlyList<uint> SquadFor(uint uid, uint stageId)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                foreach ((string bossKey, BossState b) in state.Bosses.Where(pair => pair.Value.Started && !pair.Value.Cleared))
                    if (BossRow(uint.Parse(bossKey)) is { } bossRow)
                    {
                        int pending = bossRow.Keys.FindIndex(key => !b.Done.Contains(key));
                        if (pending >= 0 && bossRow.Stages[pending] == stageId && b.Lineups.TryGetValue(bossRow.Keys[pending].ToString(), out List<uint>? bossSquad))
                            return bossSquad;
                    }

                LevelRow? level = Current(Levels(), state);
                if (level is null || !state.Started || state.Cleared)
                    return [];
                int next = level.Keys.FindIndex(key => !state.Done.Contains(key));
                return next >= 0 && level.Stages[next] == stageId && state.Lineups.TryGetValue(level.Keys[next].ToString(), out List<uint>? squad) ? squad : [];
            }
        }

        public WeekView ViewOf(uint uid)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                List<LevelRow> levels = Levels();
                LevelRow? level = Current(levels, state);
                return new WeekView(state.Level, levels.Count, level?.Storey ?? 0, state.Started, state.Cleared, state.Score, BestOf(state, level), state.Attempts,
                    state.Done.ToList(), level is null ? 0 : RatingOf(level.Number, state.Score));
            }
        }

        public HNINDAOJCKP? Start(uint uid, IReadOnlyList<IReadOnlyList<uint>> squads, List<IMessage> messages, out (ushort MsgId, byte[] Payload) reply)
        {
            reply = default;
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                List<LevelRow> levels = Levels();
                LevelRow? level = Current(levels, state);
                if (level is null || level.Keys.Count == 0)
                    return HNINDAOJCKP.ActivityMiniGameStatusInvalid;
                if (squads.Count < level.Keys.Count)
                    return HNINDAOJCKP.ClientInputInvalid;

                HashSet<uint> owned = gunService.GetPlayerGuns(uid).Select(g => g.GunId).ToHashSet();
                HashSet<uint> presets = tables.GetTable<GunPresetData>().Select(row => row.Id).ToHashSet();
                for (int i = 0; i < level.Keys.Count; i++)
                    if (squads[i].Count == 0 || squads[i].Any(id => !(id < 100000 ? owned.Contains(id) : presets.Contains(id))))
                        return HNINDAOJCKP.ClientInputInvalid;

                TopUpTickets(uid, state, messages);
                if (!itemService.TrySpend(uid, Cost(), out SC_Items update))
                {
                    Save(uid, state);
                    return HNINDAOJCKP.ItemNotEnough;
                }

                messages.Add(update);
                state.Started = true;
                state.Cleared = false;
                state.Done = [];
                state.Score = 0;
                state.Attempts++;
                state.Lineups = [];
                for (int i = 0; i < level.Keys.Count; i++)
                    state.Lineups[level.Keys[i].ToString()] = squads[i].ToList();
                Save(uid, state);

                var step = new List<byte>();
                Message(step, 3, StepB(state, level, BestOf(state, level)));
                reply = ((ushort)Common.Protocol.MsgId.MsgScSimCombatWeekStart, step.ToArray());
                Log.Information("SimCombatWeek: account {Uid} starts level {Level} (storey {Storey}), attempt {Attempts}.", uid, state.Level + 1, level.Storey, state.Attempts);
                return null;
            }
        }

        public HNINDAOJCKP? BeginBattle(uint uid, uint stageId)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                LevelRow? level = Current(Levels(), state);
                if (level is not null && (state.Cleared || !state.Started) && state.Lineups.Count > 0 && level.Stages.Count > 0 && level.Stages[0] == stageId)
                {
                    // "re-challenge" of the result screen (A-142): the client enters the first battle again without a new start, so a new attempt with the same squads begins here
                    if (!itemService.TrySpend(uid, Cost(), out SC_Items _))
                        return HNINDAOJCKP.ItemNotEnough;
                    state.Started = true;
                    state.Cleared = false;
                    state.Done = [];
                    state.Score = 0;
                    state.Attempts++;
                    Save(uid, state);
                    Log.Information("SimCombatWeek: account {Uid} re-challenges level {Level} (attempt {Attempts}).", uid, state.Level + 1, state.Attempts);
                    return null;
                }

                if (level is null || !state.Started || state.Cleared)
                    return HNINDAOJCKP.ActivityMiniGameStatusInvalid;
                int next = level.Keys.FindIndex(key => !state.Done.Contains(key));
                return next < 0 || (level.Stages[next] != 0 && level.Stages[next] != stageId) ? HNINDAOJCKP.ClientInputInvalid : null;
            }
        }

        public void EndBattle(uint uid, bool won, uint points, IReadOnlyCollection<uint> challenges, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                List<LevelRow> levels = Levels();
                LevelRow? level = Current(levels, state);
                if (level is null || !state.Started || state.Cleared)
                    return;
                uint next = level.Keys.FirstOrDefault(key => !state.Done.Contains(key));
                if (!won || next == 0)
                    return;

                // The score of a squad: the points the battle report holds, or an even share of the middle rating (3) of the level (A-131).
                var bands = Ratings(level.Number);
                uint target = bands.Count == 0 ? 1000 : bands.Where(b => b.Rating == 3).Select(b => (b.Low + b.High) / 2).DefaultIfEmpty((bands[0].Low + bands[^1].High) / 2).First();
                state.Score += points > 0 ? points : Math.Max(1, target / (uint)level.Keys.Count);
                state.Done.Add(next);
                if (level.Keys.All(state.Done.Contains))
                {
                    state.Cleared = true;
                    uint best = BestOf(state, level);
                    if (state.Score > best)
                        state.Best[level.Storey.ToString()] = state.Score;
                    if (!state.Paid.Contains(level.Storey))
                    {
                        state.Paid.Add(level.Storey);
                        int rating = RatingOf(level.Number, state.Score);
                        foreach ((uint item, uint count) in bands.Where(b => b.Rating == rating).SelectMany(b => b.Reward))
                            rewardService.Grant(uid, item, (int)count, messages);
                    }
                }

                if (state.Cleared)
                    EvaluateQuests(uid, state, 1000 + level.Number, null, null, messages);
                Save(uid, state);
                messages.Add(new SC_SimCombatWeekStepBscore { Score = state.Score, Bid = next, Status = SimCombatWeekly.Types.Status.StepB });
                Log.Information("SimCombatWeek: account {Uid} won squad {Key} of storey {Storey}: score {Score}{Cleared}.", uid, next, level.Storey, state.Score, state.Cleared ? " (level cleared)" : "");
            }
        }

        public HNINDAOJCKP? StartNextStorey(uint uid, out SC_SimCombatWeekStartNextStorey? reply)
        {
            reply = null;
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                List<LevelRow> levels = Levels();
                if (!state.Cleared || state.Level + 1 >= levels.Count)
                    return HNINDAOJCKP.ActivityMiniGameStatusInvalid;

                state.Level++;
                state.Started = false;
                state.Cleared = false;
                state.Done = [];
                state.Lineups = [];
                state.Score = 0;
                state.Attempts = 0;
                Save(uid, state);
                reply = new SC_SimCombatWeekStartNextStorey { Storey = levels[state.Level].Storey, Status = SimCombatWeekly.Types.Status.StepB };
                return null;
            }
        }

        private BossState BossStateOf(WeekState state, uint boss)
        {
            if (!state.Bosses.TryGetValue(boss.ToString(), out BossState? b))
                state.Bosses[boss.ToString()] = b = new BossState();
            return b;
        }

        public BossView BossOf(uint uid, uint boss)
        {
            lock (AccountLock.For(uid))
            {
                BossState b = BossStateOf(Load(uid), boss);
                return new BossView(boss, b.Started, b.Cleared, b.Score, b.Best, b.Attempts, b.Clears, BossRow(boss)?.Keys.Count ?? 0);
            }
        }

        public HNINDAOJCKP? StartBoss(uint uid, uint boss, IReadOnlyList<IReadOnlyList<uint>> squads, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                LevelRow? row = BossRow(boss);
                if (row is null)
                    return HNINDAOJCKP.ActivityMiniGameStatusInvalid;
                if (squads.Count < row.Keys.Count)
                    return HNINDAOJCKP.ClientInputInvalid;
                HashSet<uint> owned = gunService.GetPlayerGuns(uid).Select(g => g.GunId).ToHashSet();
                HashSet<uint> presets = tables.GetTable<GunPresetData>().Select(r => r.Id).ToHashSet();
                for (int i = 0; i < row.Keys.Count; i++)
                    if (squads[i].Count == 0 || squads[i].Any(id => !(id < 100000 ? owned.Contains(id) : presets.Contains(id))))
                        return HNINDAOJCKP.ClientInputInvalid;
                TopUpTickets(uid, state, messages);
                if (!itemService.TrySpend(uid, Cost(), out SC_Items update))
                {
                    Save(uid, state);
                    return HNINDAOJCKP.ItemNotEnough;
                }

                messages.Add(update);
                BossState b = BossStateOf(state, boss);
                b.Started = true;
                b.Cleared = false;
                b.Done = [];
                b.Score = 0;
                b.Attempts++;
                b.Challenges = [];
                b.Attempt = [];
                b.Lineups = [];
                for (int i = 0; i < row.Keys.Count; i++)
                    b.Lineups[row.Keys[i].ToString()] = squads[i].ToList();
                Save(uid, state);
                messages.Insert(0, new SC_SimCombatWeekStart { Final = EntryOf(boss, b) });
                Log.Information("SimCombatWeek: account {Uid} starts boss {Boss}, attempt {Attempts}.", uid, boss, b.Attempts);
                return null;
            }
        }

        public HNINDAOJCKP? BeginBossBattle(uint uid, uint boss, uint stageId)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                LevelRow? row = BossRow(boss);
                BossState b = BossStateOf(state, boss);
                if (row is not null && (b.Cleared || !b.Started) && b.Lineups.Count > 0 && row.Stages[0] == stageId)
                {
                    // "re-challenge" of the result screen (A-142): a new attempt with the same squads
                    if (!itemService.TrySpend(uid, Cost(), out SC_Items _))
                        return HNINDAOJCKP.ItemNotEnough;
                    b.Started = true;
                    b.Cleared = false;
                    b.Done = [];
                    b.Score = 0;
                    b.Attempts++;
                    b.Attempt = [];
                    Save(uid, state);
                    Log.Information("SimCombatWeek: account {Uid} re-challenges boss {Boss} (attempt {Attempts}).", uid, boss, b.Attempts);
                    return null;
                }

                if (row is null || !b.Started || b.Cleared)
                    return HNINDAOJCKP.ActivityMiniGameStatusInvalid;
                int next = row.Keys.FindIndex(key => !b.Done.Contains(key));
                return next < 0 || row.Stages[next] != stageId ? HNINDAOJCKP.ClientInputInvalid : null;
            }
        }

        public void EndBossBattle(uint uid, uint boss, bool won, PhaseFacts facts, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                LevelRow? row = BossRow(boss);
                BossState b = BossStateOf(state, boss);
                if (row is null || !b.Started || b.Cleared || !won)
                    return;
                uint next = row.Keys.FirstOrDefault(key => !b.Done.Contains(key));
                if (next == 0)
                    return;
                // the boss levels have no rating table (WeeklyScoreRankData stops at level 10): the score of a phase is what the report says (PhaseFacts.Score: its points, or the hit points the boss lost, A-139)
                uint gain = Math.Max(1, facts.Score);
                b.Score += gain;
                b.Done.Add(next);
                if (!b.KeysWon.Contains(next))
                    b.KeysWon.Add(next);
                b.KeyScores[next.ToString()] = Math.Max(b.KeyScores.GetValueOrDefault(next.ToString()), gain);
                b.Attempt[next.ToString()] = facts;
                if (row.Keys.All(b.Done.Contains))
                {
                    b.Cleared = true;
                    b.Clears++;
                    b.Best = Math.Max(b.Best, b.Score);
                }

                EvaluateQuests(uid, state, 1000 + boss, b, next, messages);

                Save(uid, state);
                messages.Add(new SC_SimCombatWeekStepBscore { Score = b.Score, Bid = next, Status = SimCombatWeekly.Types.Status.Final, KNPHEBDLFFP = boss });
                messages.Add(new SC_SimCombatWeekRefreshNtf());
                Log.Information("SimCombatWeek: account {Uid} won squad {Key} of boss {Boss}: score {Score}{Cleared}.", uid, next, boss, b.Score, b.Cleared ? " (boss cleared)" : "");
            }
        }

        public void AbandonBoss(uint uid, uint boss)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                BossState b = BossStateOf(state, boss);
                if (b.Cleared)
                    return;
                b.Started = false;
                b.Done = [];
                b.Lineups = [];
                b.Score = 0;
                Save(uid, state);
            }
        }

        public void Abandon(uint uid)
        {
            lock (AccountLock.For(uid))
            {
                WeekState state = Load(uid);
                if (state.Cleared)
                    return;
                state.Started = false;
                state.Done = [];
                state.Lineups = [];
                state.Score = 0;
                Save(uid, state);
            }
        }
    }
}
