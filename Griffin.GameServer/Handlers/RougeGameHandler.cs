using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>Rouge2 (RougeGame) requests, played by <see cref="Rouge2Service"/>.</summary>
    public sealed class RougeGameHandler(IRouge2Service rouge2) : RougeGameHandlerBase
    {
        public override void HandleRougeGameInfo(CS_RougeGameInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;
            CCIBDHDDGCD? run = rouge2.Run(connection.Account.Uid, request.PlanId);
            // no run yet: the client opens the difficulty choice
            connection.Send(new SC_RougeGameInfo { PlanId = request.PlanId, CBNMNFAOMLL = run is not null, DMANPLMCCPG = run ?? new CCIBDHDDGCD { KOHGNMPNAMJ = new IFKECOJMCHO { KFJDLALJNDI = IFKECOJMCHO.Types.OKHIGJOBCNM.PhaseDifficulty } } });
        }

        public override void HandleRougeGameOpening(CS_RougeGameOpening request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(rouge2.Opening(connection.Account.Uid, request));
        }

        public override void HandleRougeGameNode(CS_RougeGameNode request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(rouge2.Node(connection.Account.Uid, request));
        }

        public override void HandleRougeGameOption(CS_RougeGameOption request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(rouge2.Option(connection.Account.Uid, request));
        }

        public override void HandleRougeGameNextLayer(CS_RougeGameNextLayer request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(rouge2.NextLayer(connection.Account.Uid, request));
        }

        public override void HandleRougeGameMapLayer(CS_RougeGameMapLayer request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(rouge2.MapLayer(connection.Account.Uid, request));
        }

        public override void HandleRougeGameCollectionReward(CS_RougeGameCollectionReward request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_RougeGameCollectionReward());
        }
    }
}
