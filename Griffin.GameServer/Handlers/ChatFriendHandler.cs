using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class ChatFriendHandler : ChatFriendHandlerBase
    {
        // These local conversation slots include the in-game chat used by the server's
        // command interface. Keep them visible even though this server has no other players.
        public override void HandleChatFriendList(CS_ChatFriendList request, Connection connection)
        {
            Chat EmptyConversation() => new()
            {
                CounterType = JCODPDKNMBI.CounterDefault,
                LastId = DPKCGBBDMAM.ResetNone,
                PJBOAGJCMKK = KCCBHFPHCPD.Default,
                AJNAPNNKIFC = false,
            };

            connection.Send(new SC_ChatFriendList
            {
                Friend = { EmptyConversation(), EmptyConversation(), EmptyConversation() },
            });
        }
    }
}
