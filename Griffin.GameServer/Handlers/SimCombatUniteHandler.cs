using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class SimCombatUniteHandler : SimCombatUniteHandlerBase
    {
        public override void HandleSimCombatUniteInfo(CS_SimCombatUniteInfo request, Connection connection)
        {
            connection.Send(new SC_SimCombatUniteInfo
            {
                Info = new CLEKMDAGJGD
                {
                    GOFCFHDHIIO =
                    {
                        {
                            1162u,
                            new FILNFOFMAJN
                            {
                                OHPHNKCEGCM = 1162,
                                PlanId = 90066,
                            }
                        },
                    },
                },
            });
        }
    }
}
