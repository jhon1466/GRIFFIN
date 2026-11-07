using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Generic mini games of the events (MiniGameHandlerBase): per game the plays (field 1), best score (2) and total score (3), and per stage
    /// the time of the best clear. The finish of the operation mini games (candy catch, card flip) belongs to this group and is answered by
    /// <see cref="OperationMiniGameHandler"/>.
    /// </summary>
    public sealed class MiniGameHandler(IAccountStateService accountState, ITableService tableService, IRewardService rewardService) : MiniGameHandlerBase
    {
        public sealed class State
        {
            public uint Times { get; set; }
            public uint Best { get; set; }
            public uint Total { get; set; }
            public Dictionary<uint, ulong> StageTime { get; set; } = [];
        }

        private static string Key(uint id) => $"minigame.{id}";

        private State Load(uint uid, uint id) => accountState.Get<State>(uid, Key(id)) ?? new State();

        private static NIPCKNMLFAK Proto(State s) => new() { Times = s.Times, CGFMBCCDFPG = s.Best, DEGNALHNOMI = s.Total };

        private static void Stages(State s, Google.Protobuf.Collections.MapField<uint, CIPEIDNGGEJ> into)
        {
            foreach ((uint stage, ulong time) in s.StageTime)
                into[stage] = new CIPEIDNGGEJ { CKHBBOFPDCE = (uint)Math.Min(time, uint.MaxValue) };
        }

        public override void HandleGetMiniGameInfo(CS_GetMiniGameInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;
            State state = Load(connection.Account.Uid, request.Id);
            SC_GetMiniGameInfo reply = new() { DMANPLMCCPG = Proto(state) };
            Stages(state, reply.DAJJGHEKODD);
            connection.Send(reply);
        }

        public override void HandleMiniGameStart(CS_MiniGameStart request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_MiniGameStart());
        }

        public override void HandleMiniGameFinish(CS_MiniGameFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            State state;
            lock (AccountLock.For(uid))
            {
                state = Load(uid, request.Id);
                state.Times++;
                state.Best = Math.Max(state.Best, request.Score);
                state.Total += request.Score;
                accountState.Set(uid, Key(request.Id), state);
            }

            connection.Send(new SC_MiniGameFinish { DMANPLMCCPG = Proto(state) });
        }

        public override void HandleMiniGameStageFinish(CS_MiniGameStageFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            State state;
            lock (AccountLock.For(uid))
            {
                state = Load(uid, request.Id);
                ulong best = state.StageTime.GetValueOrDefault(request.StageId);
                state.StageTime[request.StageId] = best == 0 ? request.Duration : Math.Min(best, request.Duration);
                accountState.Set(uid, Key(request.Id), state);
            }

            SC_MiniGameStageFinish reply = new();
            Stages(state, reply.DAJJGHEKODD);
            connection.Send(reply);
        }

        // the themed mini games share this message group; whichever class the registry picks, ThemeMiniGameHandler answers them
        private ThemeMiniGameHandler Theme => new(accountState);

        public override void HandleThemeMiniGameFinish(CS_ThemeMiniGameFinish request, Connection connection) => Theme.HandleThemeMiniGameFinish(request, connection);

        public override void HandleThemeMiniGameRank(CS_ThemeMiniGameRank request, Connection connection) => Theme.HandleThemeMiniGameRank(request, connection);

        public override void HandleThemeMiniGameRecordRedPoint(CS_ThemeMiniGameRecordRedPoint request, Connection connection) => Theme.HandleThemeMiniGameRecordRedPoint(request, connection);

        public override void HandleOperationMiniGameFinish(CS_OperationMiniGameFinish request, Connection connection) =>
            new OperationMiniGameHandler(accountState, tableService, rewardService).HandleOperationMiniGameFinish(request, connection);
    }
}
