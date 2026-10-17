using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class NrtPvpHandler : NrtPvpHandlerBase
    {
        public override void HandleNrtPvpInfo(CS_NrtPvpInfo request, Connection connection)
        {
            // This local single-player server has no PvP matchmaking pool. Return a
            // correctly typed empty state instead of leaving the login request hanging.
            connection.Send(new SC_NrtPvpInfo());
        }
    }
}
