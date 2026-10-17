using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class OldSchemeHandler : OldSchemeHandlerBase
    {
        public override void HandleGetAllOldScheme(CS_GetAllOldScheme request, Connection connection)
        {
                    connection.Send(new SC_GetAllOldScheme
                    {
                        HBEFGPHGDNN = { },
                        NIIJEGDNMLD = { },
                    });
        }

    }
}
