using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Local server rankings do not have a real peer population. Return typed empty results
    /// with the requested scope instead of inventing players, scores, or placement.
    /// </summary>
    public sealed class LeaderBoardHandler : LeaderBoardHandlerBase
    {
        public override void HandleLeaderBoard(CS_LeaderBoard request, Connection connection) =>
            connection.Send(new SC_LeaderBoard
            {
                PlanId = request.PlanId,
                Idx = request.Idx,
                Timestamp = connection.GameClockSeconds(),
            });
    }

    public sealed class RealRankingHandler : RealRankingHandlerBase
    {
        public override void HandleRealRanking(CS_RealRanking request, Connection connection) =>
            connection.Send(new SC_RealRanking { PlanId = request.PlanId });
    }
}
