using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class SyncHandler : SyncHandlerBase
    {
        public override void HandleSync(CS_Sync request, Connection connection)
        {
            connection.Send(new SC_Sync
            {
                Timestamp = connection.GameClockSeconds(),
                ActiveTime = 0

            });
        }
    }
}
