using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class ChatNodeHandler : ChatNodeHandlerBase
    {
        public override void HandleChatNodeFetchAddr(CS_ChatNodeFetchAddr request, Connection connection)
        {
            connection.Send(new SC_ChatNodeFetchAddr
            {
                EKCAJNMIMOO = true,
            });
        }
    }
}
