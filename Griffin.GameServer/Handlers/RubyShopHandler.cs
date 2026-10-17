using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class RubyShopHandler : RubyShopHandlerBase
    {
        public override void HandleGetRubyShopInfo(CS_GetRubyShopInfo request, Connection connection)
        {
                    connection.Send(new SC_GetRubyShopInfo
                    {
                        CIMEBELLALJ = { },
                    });
        }

    }
}
