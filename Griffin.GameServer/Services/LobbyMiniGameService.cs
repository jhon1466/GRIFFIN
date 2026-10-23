using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>What the player has on one difficulty of the space shooter (client record: times played, best score, flags bought and cleared).</summary>
    public sealed record MiniGameStageRecord(uint Times = 0, uint Score = 0, bool Cleared = false);

    public sealed record MiniGameState(
        bool Unlocked = false,
        uint BestScore = 0,
        uint LastDuration = 0,
        uint Times = 0,
        uint StageId = 0,
        long StartedAtUnixSeconds = 0,
        List<uint>? UnlockedStages = null,
        List<uint>? Guns = null,
        List<uint>? BoughtStages = null,
        List<uint>? Skills = null,
        Dictionary<uint, uint>? Totals = null,
        Dictionary<uint, MiniGameStageRecord>? Records = null,
        uint RunGun = 0);

    public enum MiniGameAchievementKind { TotalScore, SingleScore, CoinsSingle, CoinsTotal, KillsSingle, KillsTotal, KillMonster, CharClear, CharSurvive }

    /// <summary>Counters the achievements read (per game, kept apart from the state of the run).</summary>
    public sealed class MiniGameStats
    {
        public ulong TotalScore { get; set; }
        public uint CoinsTotal { get; set; }
        public uint KillsTotal { get; set; }
        public uint BestRunCoins { get; set; }
        public uint BestRunKills { get; set; }
        public Dictionary<uint, uint> KillsByMonster { get; set; } = [];
        public Dictionary<uint, uint> Clears { get; set; } = [];
        public Dictionary<uint, uint> Survivals { get; set; } = [];
        public List<uint> Claimed { get; set; } = [];
    }

    /// <summary>State of the lobby mini games (<c>LobbyMinigameGamelistData</c>): bought, best score, current stage, characters, difficulties and achievements.</summary>
    public interface ILobbyMiniGameService : IGameService
    {
        Dictionary<uint, MiniGameState> States(uint uid);

        void Save(uint uid, Dictionary<uint, MiniGameState> states);

        /// <summary>
        /// Pays for a game (pay type 0; <c>LobbyMinigameGamelistData</c> col 7 = price), a difficulty of the space shooter (type 1, object = difficulty; price
        /// <c>StspDifficultyData</c> col 5 of the difficulty or of its +900 row) or one of its characters (type 2, object = character; price
        /// <c>StspPlayerData</c> col 2) and records it. Returns false when the price cannot be paid. A-19, A-165, A-166.
        /// </summary>
        bool Pay(uint uid, uint gameId, BDPHJPNIHEK type, uint objectId, List<IMessage> messages, out HMDODOBKKND? info);

        bool Start(uint uid, uint gameId, uint stageId, uint gunId);

        /// <summary>The information of a game as the client reads it (see <c>SC_LobbyMiniGameInfo</c>).</summary>
        HMDODOBKKND BuildInfo(LobbyMinigameGamelistData game, MiniGameState state);

        bool CanFinish(uint uid, CS_LobbyMiniGameFinish run);

        /// <summary>
        /// Records the end of a run: best score, times played, the stage it unlocks, the items the run collected (granted into <paramref name="messages"/>) and the counters
        /// of the achievements. ASSUMPTION A-26: the client score is trusted.
        /// </summary>
        SC_LobbyMiniGameFinish Finish(uint uid, CS_LobbyMiniGameFinish run, List<IMessage> messages);

        /// <summary>Buys a talent of the space shooter (<c>StspTalentData</c>; cost in column 7).</summary>
        bool BuySkill(uint uid, uint gameId, uint skillId, List<IMessage> messages);

        /// <summary>The achievements of the mini games as quest counters (completed ones with their taken flag, progress of all).</summary>
        CommonQuestCounters Counters(uint uid);

        /// <summary>Pays the reward (item 1610000) of the completed achievements in <paramref name="ids"/> once; returns the ids taken.</summary>
        List<uint> Claim(uint uid, IEnumerable<uint> ids, List<IMessage> messages);
    }

    public sealed class LobbyMiniGameService(IAccountStateService accountState, ITableService tableService, IItemUseService itemUse, IRewardService rewards) : ILobbyMiniGameService
    {
        private const string StateKey = "lobby-minigames.v1";
        private const string StatsKey = "lobby-minigames.stats.v1";
        private const uint ShooterGame = 1;
        private const uint DoodleGame = 2;

        /// <summary>The character the space shooter gives for free; the others are bought (<c>StspPlayerData</c> col 2).</summary>
        public const uint FreeGun = 1034;

        /// <summary>
        /// The difficulties of the shooter are the ones the config of the game lists (<c>LobbyMinigameGamelistData</c> col 3 = config id 999 → <c>StspConfigData</c> col 5:
        /// 901, 902, 903; the client keys its records and its cards by these ids, not by 1..3). The first one is free and always open.
        /// </summary>
        private IReadOnlyList<uint> Difficulties => tableService.GetTable<StspConfigData>().FirstOrDefault(row => TableFields.U(row, 1) == ShooterConfig(tableService)) is { } config
            ? TableFields.List(config, 5)
            : [901, 902, 903];

        private uint FirstDifficulty => Difficulties.FirstOrDefault();

        private static uint ShooterConfig(ITableService tables) =>
            tables.GetTable<LobbyMinigameGamelistData>().FirstOrDefault(row => row.Id == ShooterGame) is { } game ? TableFields.U(game, 3) : 999;

        public Dictionary<uint, MiniGameState> States(uint uid) => accountState.Get<Dictionary<uint, MiniGameState>>(uid, StateKey) ?? [];

        public void Save(uint uid, Dictionary<uint, MiniGameState> states) => accountState.Set(uid, StateKey, states);

        private Dictionary<uint, MiniGameStats> Stats(uint uid) => accountState.Get<Dictionary<uint, MiniGameStats>>(uid, StatsKey) ?? [];

        public HMDODOBKKND BuildInfo(LobbyMinigameGamelistData game, MiniGameState state)
        {
            HMDODOBKKND info = new() { MPNGHAKGLDM = state.Unlocked };
            if (game.GMHMLJJNBCD == 2)
            {
                info.DoodleJumpData = new FOMAJCHMPFL
                {
                    KNFOFFBOCML = state.Unlocked,
                    CGFMBCCDFPG = state.BestScore,
                    Duration = state.LastDuration,
                    Times = state.Times,
                };
            }
            else if (game.GMHMLJJNBCD == 1)
            {
                JLEOGCKLFPC shooter = new() { DEGNALHNOMI = state.BestScore };
                // characters: the free one plus the bought ones
                shooter.Guns.Add(FreeGun);
                shooter.Guns.AddRange((state.Guns ?? []).Where(gun => gun != FreeGun));
                uint first = FirstDifficulty;
                // difficulties: the first is always open; a record exists for each so the client can show its buy dialog (flag 3 = bought, flag 4 = cleared)
                foreach (uint difficulty in Difficulties)
                {
                    MiniGameStageRecord record = state.Records?.GetValueOrDefault(difficulty) ?? new MiniGameStageRecord();
                    bool bought = difficulty == first || state.BoughtStages?.Contains(difficulty) == true;
                    shooter.FDPOAJJJKGC[difficulty] = new CFGNAOGCNNF { Times = record.Times, CGFMBCCDFPG = record.Score, MPNGHAKGLDM = bought, KNFOFFBOCML = record.Cleared };
                }

                shooter.IMOHEBMLOBE.AddRange(state.Skills ?? []);
                foreach ((uint item, uint count) in state.Totals ?? [])
                    shooter.LPMHIBPDHJK[item] = count;
                info.ShootspaceData = shooter;
            }

            return info;
        }

        public SC_LobbyMiniGameFinish Finish(uint uid, CS_LobbyMiniGameFinish run, List<IMessage> messages)
        {
            Dictionary<uint, MiniGameState> states = States(uid);
            MiniGameState state = states.GetValueOrDefault(run.LAOKONCHCHL) ?? new MiniGameState();
            List<uint> unlocked = state.UnlockedStages?.ToList() ?? [];
            SC_LobbyMiniGameFinish reply = new() { LAOKONCHCHL = run.LAOKONCHCHL, StageId = run.StageId, Score = run.Score };
            if (!CanFinish(uid, run))
                return reply;

            uint best = Math.Max(state.BestScore, run.Score);
            // Advance inside the configured level group. Sorting every row globally would
            // accidentally make the next difficulty/group available after the last stage.
            if (run.LAOKONCHCHL == DoodleGame && TryGetNextStage(run.StageId, out uint nextStage) && !unlocked.Contains(nextStage))
            {
                unlocked.Add(nextStage);
                reply.UnlockStage.Add(nextStage);
            }

            state = state with
            {
                BestScore = best,
                LastDuration = run.Duration,
                Times = state.Times == uint.MaxValue ? uint.MaxValue : state.Times + 1,
                UnlockedStages = unlocked,
                StageId = 0,
                StartedAtUnixSeconds = 0,
            };

            uint runKills = 0, runCoins = 0;
            Dictionary<uint, uint> kills = [];
            if (run.LAOKONCHCHL == ShooterGame && run.Shootspace is { } shooter)
            {
                // the difficulty the run was played on is the stage of the request; the items it collected are paid and added to the totals the client shows
                uint difficulty = Difficulties.Contains(run.StageId) ? run.StageId : FirstDifficulty;
                MiniGameStageRecord record = state.Records?.GetValueOrDefault(difficulty) ?? new MiniGameStageRecord();
                record = record with { Times = record.Times + 1, Score = Math.Max(record.Score, run.Score), Cleared = record.Cleared || run.Score > 0 };
                Dictionary<uint, uint> totals = new(state.Totals ?? []);
                HashSet<uint> payable = tableService.GetTable<StspItemData>().Select(row => TableFields.U(row, 1)).ToHashSet();
                foreach ((uint item, uint count) in shooter.DPIODNMOADH)
                {
                    if (count == 0 || count > 100_000)
                        continue;

                    totals[item] = totals.GetValueOrDefault(item) + count;
                    reply.Shootspace ??= new SC_LobbyMiniGameFinish.Types.CJMKOOCHJPA();
                    reply.Shootspace.DPIODNMOADH[item] = count;
                    if (payable.Contains(item))
                        rewards.Grant(uid, item, (int)count, messages);
                    runCoins += count;
                }

                foreach ((uint monster, uint count) in shooter.IFDNNGFHCKK)
                {
                    kills[monster] = count;
                    runKills += count;
                }

                state = state with
                {
                    Records = new Dictionary<uint, MiniGameStageRecord>(state.Records ?? []) { [difficulty] = record },
                    Totals = totals,
                };
                reply.CGFMBCCDFPG = record.Score;
                reply.Shootspace ??= new SC_LobbyMiniGameFinish.Types.CJMKOOCHJPA();
                reply.Shootspace.DEGNALHNOMI = best;
                reply.Shootspace.Times = record.Times;
                foreach ((uint item, uint count) in totals)
                    reply.Shootspace.LPMHIBPDHJK[item] = count;
            }
            else if (run.LAOKONCHCHL == DoodleGame && run.DoodleJump is { } doodle)
            {
                foreach (CS_LobbyMiniGameFinish.Types.JKDDIPINCDO round in doodle.EGHGOPPMCPH)
                {
                    foreach ((uint monster, uint count) in round.IFDNNGFHCKK)
                    {
                        kills[monster] = kills.GetValueOrDefault(monster) + count;
                        runKills += count;
                    }

                    foreach ((_, uint count) in round.EMPHGJBAHBP)
                        runCoins += count;
                }

                reply.CGFMBCCDFPG = best;
            }
            else
            {
                reply.CGFMBCCDFPG = best;
            }

            states[run.LAOKONCHCHL] = state;
            Save(uid, states);
            RecordStats(uid, run, state.RunGun, runKills, runCoins, kills);
            return reply;
        }

        private void RecordStats(uint uid, CS_LobbyMiniGameFinish run, uint gun, uint runKills, uint runCoins, Dictionary<uint, uint> kills)
        {
            Dictionary<uint, MiniGameStats> all = Stats(uid);
            MiniGameStats stats = all.GetValueOrDefault(run.LAOKONCHCHL) ?? new MiniGameStats();
            stats.TotalScore += run.Score;
            stats.CoinsTotal = (uint)Math.Min((ulong)stats.CoinsTotal + runCoins, uint.MaxValue);
            stats.KillsTotal = (uint)Math.Min((ulong)stats.KillsTotal + runKills, uint.MaxValue);
            stats.BestRunCoins = Math.Max(stats.BestRunCoins, runCoins);
            stats.BestRunKills = Math.Max(stats.BestRunKills, runKills);
            foreach ((uint monster, uint count) in kills)
            {
                // the monster ids of the tables are 7001x..7005x: the type is the id without its last digit
                uint type = monster >= 10000 ? monster / 10 : monster;
                stats.KillsByMonster[type] = stats.KillsByMonster.GetValueOrDefault(type) + count;
            }

            if (run.LAOKONCHCHL == ShooterGame && gun != 0)
            {
                if (run.Score > 0)
                    stats.Clears[gun] = stats.Clears.GetValueOrDefault(gun) + 1;
                if (run.Duration > 1000)
                    stats.Survivals[gun] = stats.Survivals.GetValueOrDefault(gun) + 1;
            }

            all[run.LAOKONCHCHL] = stats;
            accountState.Set(uid, StatsKey, all);
        }

        public bool CanFinish(uint uid, CS_LobbyMiniGameFinish run)
        {
            MiniGameState? state = States(uid).GetValueOrDefault(run.LAOKONCHCHL);
            return state is { Unlocked: true, StartedAtUnixSeconds: > 0 }
                && (state.StageId == run.StageId || state.StageId == 0 || run.LAOKONCHCHL == ShooterGame);
        }

        public bool Start(uint uid, uint gameId, uint stageId, uint gunId)
        {
            Dictionary<uint, MiniGameState> states = States(uid);
            MiniGameState state = states.GetValueOrDefault(gameId) ?? new MiniGameState();
            // the real client starts the space shooter with only its character and the doodle jump with only the game id: no stage (0)
            if (!state.Unlocked)
                return false;

            if (gameId == DoodleGame && stageId != 0)
            {
                List<uint> unlocked = state.UnlockedStages?.ToList() ?? InitialDoodleJumpStages();
                if (!tableService.GetTable<LobbyMNgameDoodlejumpLevelData>().Any(row => TableFields.U(row, 1) == stageId)
                    || !unlocked.Contains(stageId))
                    return false;

                state = state with { UnlockedStages = unlocked };
            }

            if (gameId == ShooterGame && gunId != 0 && gunId != FreeGun && state.Guns?.Contains(gunId) != true)
                return false;

            state = state with { StageId = stageId, StartedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RunGun = gunId };
            states[gameId] = state;
            Save(uid, states);
            return true;
        }

        public bool Pay(uint uid, uint gameId, BDPHJPNIHEK type, uint objectId, List<IMessage> messages, out HMDODOBKKND? info)
        {
            lock (AccountLock.For(uid))
            {
                info = null;
                LobbyMinigameGamelistData? game = tableService.GetTable<LobbyMinigameGamelistData>().FirstOrDefault(row => row.Id == gameId);
                if (game is null)
                    return false;

                Dictionary<uint, MiniGameState> states = States(uid);
                MiniGameState state = states.GetValueOrDefault(gameId) ?? new MiniGameState();
                if (type == BDPHJPNIHEK.PayGame && !state.Unlocked)
                {
                    Dictionary<uint, int> cost = TableFields.Map(game, 7).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => (int)p.Value);
                    if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
                        return false;

                    state = state with
                    {
                        Unlocked = true,
                        UnlockedStages = gameId == DoodleGame ? InitialDoodleJumpStages() : state.UnlockedStages,
                    };
                    states[gameId] = state;
                    Save(uid, states);
                }
                else if (gameId == ShooterGame && state.Unlocked && objectId != 0)
                {
                    if (type == BDPHJPNIHEK.PayGun && objectId != FreeGun && state.Guns?.Contains(objectId) != true)
                    {
                        if (!Spend(uid, GunPrice(objectId), messages))
                            return false;
                        state = state with { Guns = [.. state.Guns ?? [], objectId] };
                    }
                    else if (type == BDPHJPNIHEK.PayStage && objectId != FirstDifficulty && Difficulties.Contains(objectId) && state.BoughtStages?.Contains(objectId) != true)
                    {
                        if (!Spend(uid, DifficultyPrice(objectId), messages))
                            return false;
                        state = state with { BoughtStages = [.. state.BoughtStages ?? [], objectId] };
                    }

                    states[gameId] = state;
                    Save(uid, states);
                }

                info = BuildInfo(game, state);
                return true;
            }
        }

        private bool Spend(uint uid, IReadOnlyDictionary<uint, uint> price, List<IMessage> messages) =>
            price.Count == 0 || itemUse.Spend(uid, price.ToDictionary(p => p.Key, p => (int)p.Value), messages);

        /// <summary>The price of a character: <c>StspPlayerData</c> column 2 ({item, count}); the free one has none.</summary>
        private IReadOnlyDictionary<uint, uint> GunPrice(uint gunId) =>
            tableService.GetTable<StspPlayerData>().FirstOrDefault(row => TableFields.U(row, 1) == gunId) is { } row
                ? TableFields.Map(row, 2).ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<uint, uint>();

        /// <summary>The price of a difficulty: <c>StspDifficultyData</c> column 5 of its row (902 and 903 cost 100 of item 1610000, 901 is free).</summary>
        private IReadOnlyDictionary<uint, uint> DifficultyPrice(uint difficulty) =>
            tableService.GetTable<StspDifficultyData>().FirstOrDefault(row => TableFields.U(row, 1) == difficulty) is { } row
                ? TableFields.Map(row, 5).ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<uint, uint>();

        public bool BuySkill(uint uid, uint gameId, uint skillId, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                Dictionary<uint, MiniGameState> states = States(uid);
                MiniGameState state = states.GetValueOrDefault(gameId) ?? new MiniGameState();
                if (gameId != ShooterGame || !state.Unlocked || tableService.GetTable<StspTalentData>().FirstOrDefault(row => TableFields.U(row, 1) == skillId) is not { } talent)
                    return false;

                if (state.Skills?.Contains(skillId) == true)
                    return true;

                if (!Spend(uid, TableFields.Map(talent, 7).ToDictionary(p => p.Key, p => p.Value), messages))
                    return false;

                states[gameId] = state with { Skills = [.. state.Skills ?? [], skillId] };
                Save(uid, states);
                return true;
            }
        }

        private double Progress(Dictionary<uint, MiniGameState> states, Dictionary<uint, MiniGameStats> all, (uint Id, uint Game, MiniGameAchievementKind Kind, uint Target, uint Gun, uint Monster, uint Seconds, uint Reward) a)
        {
            MiniGameStats stats = all.GetValueOrDefault(a.Game) ?? new MiniGameStats();
            return a.Kind switch
            {
                MiniGameAchievementKind.TotalScore => stats.TotalScore,
                MiniGameAchievementKind.SingleScore => (states.GetValueOrDefault(a.Game) ?? new MiniGameState()).BestScore,
                MiniGameAchievementKind.CoinsSingle => stats.BestRunCoins,
                MiniGameAchievementKind.CoinsTotal => stats.CoinsTotal,
                MiniGameAchievementKind.KillsSingle => stats.BestRunKills,
                MiniGameAchievementKind.KillsTotal => stats.KillsTotal,
                MiniGameAchievementKind.KillMonster => stats.KillsByMonster.GetValueOrDefault(a.Monster),
                MiniGameAchievementKind.CharClear => stats.Clears.GetValueOrDefault(a.Gun),
                MiniGameAchievementKind.CharSurvive => stats.Survivals.GetValueOrDefault(a.Gun),
                _ => 0,
            };
        }

        public CommonQuestCounters Counters(uint uid)
        {
            Dictionary<uint, MiniGameState> states = States(uid);
            Dictionary<uint, MiniGameStats> all = Stats(uid);
            HashSet<uint> claimed = all.Values.SelectMany(stats => stats.Claimed).ToHashSet();
            CommonQuestCounters counters = new() { Type = CommonQuestCounters.Types.Type.LobbyMiniGame };
            foreach (var achievement in LobbyMiniGameAchievementTable.All)
            {
                // like the other achievement counters (AchievementService.Build): an id is listed only once it is complete; the client reads any listed id as reached,
                // so listing the ones in progress lit the red dot of the achievements button for rewards that could not be taken
                double progress = Progress(states, all, achievement);
                if (progress < achievement.Target)
                    continue;

                counters.JGOJBNHMJJH[achievement.Id] = (uint)Math.Min(progress, uint.MaxValue);
                counters.Rewards[achievement.Id] = claimed.Contains(achievement.Id);
            }

            return counters;
        }

        public List<uint> Claim(uint uid, IEnumerable<uint> ids, List<IMessage> messages)
        {
            lock (AccountLock.For(uid))
            {
                Dictionary<uint, MiniGameState> states = States(uid);
                Dictionary<uint, MiniGameStats> all = Stats(uid);
                HashSet<uint> claimed = all.Values.SelectMany(stats => stats.Claimed).ToHashSet();
                List<uint> taken = [];
                foreach (uint id in ids.Distinct())
                {
                    var achievement = LobbyMiniGameAchievementTable.All.FirstOrDefault(a => a.Id == id);
                    if (achievement.Id == 0 || claimed.Contains(id) || Progress(states, all, achievement) < achievement.Target)
                        continue;

                    rewards.Grant(uid, 1610000, (int)achievement.Reward, messages);
                    MiniGameStats stats = all.GetValueOrDefault(achievement.Game) ?? new MiniGameStats();
                    stats.Claimed.Add(id);
                    all[achievement.Game] = stats;
                    taken.Add(id);
                }

                if (taken.Count > 0)
                    accountState.Set(uid, StatsKey, all);
                return taken;
            }
        }

        private List<uint> InitialDoodleJumpStages() => tableService
            .GetTable<LobbyMNgameDoodlejumpLevelByLevelGroupData>()
            .Select(group => TableFields.List(group, 2).FirstOrDefault())
            .Where(stageId => stageId > 0)
            .Distinct()
            .ToList();

        private bool TryGetNextStage(uint stageId, out uint nextStage)
        {
            LobbyMNgameDoodlejumpLevelData? level = tableService.GetTable<LobbyMNgameDoodlejumpLevelData>()
                .FirstOrDefault(row => TableFields.U(row, 1) == stageId);
            if (level is not null)
            {
                uint groupId = TableFields.U(level, 2);
                LobbyMNgameDoodlejumpLevelByLevelGroupData? group = tableService.GetTable<LobbyMNgameDoodlejumpLevelByLevelGroupData>()
                    .FirstOrDefault(row => TableFields.U(row, 1) == groupId);
                IReadOnlyList<uint> stages = group is null ? [] : TableFields.List(group, 2);
                int index = Array.IndexOf(stages.ToArray(), stageId);
                if (index >= 0 && index + 1 < stages.Count)
                {
                    nextStage = stages[index + 1];
                    return true;
                }
            }

            nextStage = 0;
            return false;
        }
    }
}
