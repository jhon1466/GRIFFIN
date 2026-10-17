using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class AutoDismantleHandler : AutoDismantleHandlerBase
    {
        public override void HandleGetAutoDismantleRulesInfo(CS_GetAutoDismantleRulesInfo request, Connection connection)
        {
            connection.Send(
            new SC_GetAutoDismantleRulesInfo
            {
                CEOJCLOOAOA = new NGOKNFICEOD
                {
                    HOECFIANEAB = null,
                    JGPEJLJCGMB = null,
                },
            }
            );
        }
    }
}
