using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class AvgHandler : AvgHandlerBase
    {
        public override void HandleGetAvgInfo(CS_GetAvgInfo request, Connection connection)
        {
            connection.Send(
            new SC_GetAvgInfo
            {
                CharacterAvgs = { },
            }
            );
        }
    }
}
