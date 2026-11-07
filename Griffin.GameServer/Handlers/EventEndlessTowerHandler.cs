using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>Endless tower requests (EndlessTowerService); the ranks of a local server hold this account only.</summary>
    public sealed class EventEndlessTowerHandler(IEndlessTowerService tower) : EventEndlessTowerHandlerBase
    {
        public override void HandleEventEndlessTowerInfo(CS_EventEndlessTowerInfo request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_EventEndlessTowerInfo { IHNCKKDOJLL = tower.Info(connection.Account.Uid, request.IKDMNOBMIFE) });
        }

        public override void HandleEventEndlessTowerHistoryInfo(CS_EventEndlessTowerHistoryInfo request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_EventEndlessTowerHistoryInfo());
        }

        public override void HandleEventEndlessTowerTaskTargetClaim(CS_EventEndlessTowerTaskTargetClaim request, Connection connection)
        {
            if (connection.Account is null)
                return;
            tower.ClaimTarget(connection.Account.Uid, request.IKDMNOBMIFE);
            connection.Send(new SC_EventEndlessTowerTaskTargetClaim { NHOAAJOECCK = true, IKDMNOBMIFE = request.IKDMNOBMIFE });
        }

        public override void HandleEventEndlessTowerMyRank(CS_EventEndlessTowerMyRank request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_EventEndlessTowerMyRank { IKDMNOBMIFE = request.IKDMNOBMIFE });
        }

        public override void HandleEventEndlessTowerRank(CS_EventEndlessTowerRank request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_EventEndlessTowerRank { IKDMNOBMIFE = request.IKDMNOBMIFE, LLJNHLLCBIP = request.LLJNHLLCBIP, JIPOFLLIHOJ = request.JIPOFLLIHOJ, LDKIMKKNPAA = request.LDKIMKKNPAA });
        }

        public override void HandleEventEndlessTowerRankGlobal(CS_EventEndlessTowerRankGlobal request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_EventEndlessTowerRankGlobal { IKDMNOBMIFE = request.IKDMNOBMIFE });
        }

        public override void HandleEventEndlessTowerLayerFirstPassInfo(CS_EventEndlessTowerLayerFirstPassInfo request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_EventEndlessTowerLayerFirstPassInfo { IKDMNOBMIFE = request.IKDMNOBMIFE, LKAKOAINCGO = request.LKAKOAINCGO, FOINIOKGDBD = request.FOINIOKGDBD, NDCGLLCAHKC = request.NDCGLLCAHKC });
        }

        public override void HandleEventEndlessTowerQuestLayerFirstPassInfo(CS_EventEndlessTowerQuestLayerFirstPassInfo request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_EventEndlessTowerQuestLayerFirstPassInfo { IKDMNOBMIFE = request.IKDMNOBMIFE, LKAKOAINCGO = request.LKAKOAINCGO, FOINIOKGDBD = request.FOINIOKGDBD, NDCGLLCAHKC = request.NDCGLLCAHKC });
        }

        public override void HandleEventEndlessTowerBatchBuyChallengeCount(CS_EventEndlessTowerBatchBuyChallengeCount request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_EventEndlessTowerBatchBuyChallengeCount { IKDMNOBMIFE = request.IKDMNOBMIFE, Data = new FCDLKJNBDGM.Types.FNGCICEAMBF() });
        }
    }
}
