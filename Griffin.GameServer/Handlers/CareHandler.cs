using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class CareHandler : CareHandlerBase
    {

        public override void HandleCareInfo(CS_CareInfo request, Connection connection)
        {
                    connection.Send(new SC_CareInfo
                    {
                        DHJDFODDKMI = { },
                        Plans = { },
                        EBHBLOKHAJL = false,
                    });
        }
    }
}
