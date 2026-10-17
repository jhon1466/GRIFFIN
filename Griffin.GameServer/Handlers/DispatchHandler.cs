using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class DispatchHandler : DispatchHandlerBase
    {
        public override void HandleGetDispatchInfo(CS_GetDispatchInfo request, Connection connection)
        {
                    connection.Send(new SC_GetDispatchInfo
                    {
                        HLIHNOCKGLD = { },
                    });
        }

    }
}
