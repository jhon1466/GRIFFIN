using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class SimCombatTutorialHandler : SimCombatTutorialHandlerBase
    {
        public override void HandleSimCombatTutorialInfoV2(CS_SimCombatTutorialInfoV2 request, Connection connection)
        {
            connection.Send(new SC_SimCombatTutorialInfoV2
            {
                Tutorial = new PAHCFJFLGII { NPMHOABEFGB = 0 },
            });
        }
    }
}
