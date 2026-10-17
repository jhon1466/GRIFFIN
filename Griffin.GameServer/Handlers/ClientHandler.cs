using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class ClientHandler : ClientHandlerBase
    {
        public override void HandleClientReachCount(CS_ClientReachCount request, Connection connection)
        {
            connection.Send(new SC_ClientReachCount
            {
                LJJEFHEDHLB = request.BDDOIKKHJFH,
                JILCADDFLNO = request.JILCADDFLNO,
            });
        }
    }
}
