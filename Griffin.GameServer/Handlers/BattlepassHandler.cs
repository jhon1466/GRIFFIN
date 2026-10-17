using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Battle pass of the current season (see <see cref="IBattlepassService"/>): level from the BP experience item, rewards of
    /// <c>BpRewardDescData</c> claimed once per level and track, extra rewards beyond the maximum level, task lists with
    /// refresh. The shared tasks ("share" / "accept" / "release") need other players and answer that the task does not exist.
    /// </summary>
    public sealed class BattlepassHandler(IBattlepassService battlepass) : BattlepassHandlerBase
    {
        public override void HandleGetBattlepassInfo(CS_GetBattlepassInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            connection.Send(new SC_GetBattlepassInfo { Battlepass = battlepass.Build(connection.Account.Uid) });
        }

        public override void HandleBattlepassAcquire(CS_BattlepassAcquire request, Connection connection)
        {
            if (connection.Account is null)
                return;

            battlepass.Activate(connection.Account.Uid, advanced: false);
            connection.Send(new SC_BattlepassAcquire { IsStart = request.IsStart, Battlepass = battlepass.Build(connection.Account.Uid) });
        }

        public override void HandleBattlepassGetExtraReward(CS_BattlepassGetExtraReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            List<IMessage> messages = [];
            int granted = battlepass.ClaimExtra(connection.Account.Uid, messages);
            messages.Add(new SC_BattlepassGetExtraReward { Exp = (uint)granted });
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleBattlepassGetDailyTask(CS_BattlepassGetDailyTask request, Connection connection)
        {
            if (connection.Account is null)
                return;

            SC_BattlepassGetDailyTask response = new() { TaskType = request.TaskType };
            response.Tasks.AddRange(battlepass.Tasks(connection.Account.Uid, request.TaskType));
            connection.Send(response);
        }

        public override void HandleBattlepassRefreshTask(CS_BattlepassRefreshTask request, Connection connection)
        {
            if (connection.Account is null)
                return;

            if (!battlepass.Refresh(connection.Account.Uid, request.TaskType, request.Idx, out uint task, out uint count))
                return;

            connection.Send(new SC_BattlepassRefreshTask { TaskType = request.TaskType, Idx = request.Idx, StcId = task, RefreshNum = count });
        }

        public override void HandleBattlepassAcceptTask(CS_BattlepassAcceptTask request, Connection connection) =>
            connection.Send(new SC_BattlepassAcceptTask { State = BpShareTaskState.NoExist });

        public override void HandleBattlepassReleaseTask(CS_BattlepassReleaseTask request, Connection connection) =>
            connection.Send(new SC_BattlepassReleaseTask { State = BpShareTaskState.NotServe, Uid = request.Uid });

        public override void HandleBattlepassGetShareTaskReward(CS_BattlepassGetShareTaskReward request, Connection connection) =>
            connection.Send(new SC_BattlepassGetShareTaskReward { TaskId = request.TaskId, State = (uint)BpShareTaskState.NoExist });

        public override void HandleBattlepassTaskDetail(CS_BattlepassTaskDetail request, Connection connection) =>
            connection.Send(new SC_BattlepassTaskDetail { State = BpShareTaskState.NoExist });
    }
}
