using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    public sealed record MiniGameState(
        bool Unlocked = false,
        uint BestScore = 0,
        uint LastDuration = 0,
        uint Times = 0,
        uint StageId = 0,
        long StartedAtUnixSeconds = 0,
        List<uint>? UnlockedStages = null);

    /// <summary>State of the lobby mini games (<c>LobbyMinigameGamelistData</c>): bought, best score, current stage.</summary>
    public interface ILobbyMiniGameService : IGameService
    {
        Dictionary<uint, MiniGameState> States(uint uid);

        void Save(uint uid, Dictionary<uint, MiniGameState> states);

        /// <summary>
        /// Pays for a game (pay type 0; <c>LobbyMinigameGamelistData</c> col 7 = price) and unlocks it. Pay types 1 (stage) and 2 (character) have no price in
        /// the tables and just echo the state. ASSUMPTION A-19. Returns false when the price cannot be paid.
        /// </summary>
        bool Pay(uint uid, uint gameId, BDPHJPNIHEK type, List<IMessage> messages, out HMDODOBKKND? info);

        bool Start(uint uid, uint gameId, uint stageId);

        bool CanFinish(uint uid, CS_LobbyMiniGameFinish run);

        /// <summary>
        /// Records the end of a run: best score, times played and the stage it unlocks (the next stage of the game list). ASSUMPTION A-26: the client score is
        /// trusted, no rewards are paid here (the table rewards belong to the achievements) and finishing a stage unlocks the next one.
        /// </summary>
        SC_LobbyMiniGameFinish Finish(uint uid, CS_LobbyMiniGameFinish run);
    }

    public sealed class LobbyMiniGameService(IAccountStateService accountState, ITableService tableService, IItemUseService itemUse) : ILobbyMiniGameService
    {
        private const string StateKey = "lobby-minigames.v1";

        public Dictionary<uint, MiniGameState> States(uint uid) => accountState.Get<Dictionary<uint, MiniGameState>>(uid, StateKey) ?? [];

        public void Save(uint uid, Dictionary<uint, MiniGameState> states) => accountState.Set(uid, StateKey, states);

        public static HMDODOBKKND BuildInfo(LobbyMinigameGamelistData game, MiniGameState state)
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
                info.ShootspaceData = new JLEOGCKLFPC { DEGNALHNOMI = state.BestScore };
            }

            return info;
        }

        public SC_LobbyMiniGameFinish Finish(uint uid, CS_LobbyMiniGameFinish run)
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
            if (run.LAOKONCHCHL == 2 && TryGetNextStage(run.StageId, out uint nextStage) && !unlocked.Contains(nextStage))
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
            states[run.LAOKONCHCHL] = state;
            Save(uid, states);
            if (run.LAOKONCHCHL == 1)
                reply.Shootspace = new SC_LobbyMiniGameFinish.Types.CJMKOOCHJPA { DEGNALHNOMI = best, Times = state.Times };
            else
                reply.CGFMBCCDFPG = best;
            return reply;
        }

        public bool CanFinish(uint uid, CS_LobbyMiniGameFinish run)
        {
            MiniGameState? state = States(uid).GetValueOrDefault(run.LAOKONCHCHL);
            return state is { Unlocked: true, StartedAtUnixSeconds: > 0 }
                && state.StageId == run.StageId;
        }

        public bool Start(uint uid, uint gameId, uint stageId)
        {
            Dictionary<uint, MiniGameState> states = States(uid);
            MiniGameState state = states.GetValueOrDefault(gameId) ?? new MiniGameState();
            if (!state.Unlocked || stageId == 0)
                return false;

            if (gameId == 2)
            {
                List<uint> unlocked = state.UnlockedStages?.ToList() ?? InitialDoodleJumpStages();
                if (!tableService.GetTable<LobbyMNgameDoodlejumpLevelData>().Any(row => TableFields.U(row, 1) == stageId)
                    || !unlocked.Contains(stageId))
                    return false;

                state = state with { UnlockedStages = unlocked };
            }

            state = state with { StageId = stageId, StartedAtUnixSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            states[gameId] = state;
            Save(uid, states);
            return true;
        }

        public bool Pay(uint uid, uint gameId, BDPHJPNIHEK type, List<IMessage> messages, out HMDODOBKKND? info)
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
                        UnlockedStages = gameId == 2 ? InitialDoodleJumpStages() : state.UnlockedStages,
                    };
                    states[gameId] = state;
                    Save(uid, states);
                }

                info = BuildInfo(game, state);
                return true;
        
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
