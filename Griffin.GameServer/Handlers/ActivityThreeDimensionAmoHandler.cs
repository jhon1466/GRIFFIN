using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>The 3D wish board requests of their own message group; the logic lives in <see cref="ActivityHandler"/>.</summary>
    public sealed class ActivityThreeDimensionAmoHandler(IEventSigninService eventSignin, IActivityBackService activityBack, ITableService tableService,
        IEventOverrideService eventOverrides, IRewardService rewardService, IAccountStateService accountState, IItemUseService itemUse) : ActivityThreeDimensionAmoHandlerBase
    {
        private ActivityHandler Activity => new(eventSignin, activityBack, tableService, eventOverrides, rewardService, accountState, itemUse);

        public override void HandleActivityThreeDimensionsAmoGetInfo(CS_ActivityThreeDimensionsAmoGetInfo request, Connection connection) =>
            Activity.HandleActivityThreeDimensionsAmoGetInfo(request, connection);

        public override void HandleActivityThreeDimensionsAmoGetReward(CS_ActivityThreeDimensionsAmoGetReward request, Connection connection) =>
            Activity.HandleActivityThreeDimensionsAmoGetReward(request, connection);

        /// <summary>The final reward of the board (AmoActivityMainclass3dData column 8), once.</summary>
        public override void HandleActivityThreeDimensionsAmoGetSpecialReward(CS_ActivityThreeDimensionsAmoGetSpecialReward request, Connection connection)
        {
            if (connection.Account is null)
                return;
            Activity.ClaimAmo3dFinal(connection.Account.Uid, request.IKDMNOBMIFE, out List<Google.Protobuf.IMessage> messages);
            messages.Add(new SC_ActivityThreeDimensionsAmoGetSpecialReward { IKDMNOBMIFE = request.IKDMNOBMIFE });
            connection.SendAutoEncrypted(messages.ToArray());
        }
    }
}
